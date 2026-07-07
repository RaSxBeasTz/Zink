using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using WinRT.Interop;

using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class VideoLibraryPage : Page
    {
        public ObservableCollection<VideoItem> Videos { get; } = new();
        private readonly List<VideoItem> _allVideos = new();
        private static readonly List<VideoItem> CachedVideos = new();
        private static bool _hasLoadedVideoCache;
        private static int _lastKnownLibraryCount;

        private const string LibraryFileName = "video_library.json";
        private readonly string[] _exts = new[] { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".wmv" };
        private static readonly HttpClient MovieArtworkHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly Dictionary<string, byte[]?> MovieArtworkCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim MovieArtworkLookupGate = new(6, 6);
        private static readonly string[] MovieArtworkExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        private static readonly string[] MovieArtworkNames = new[] { "poster", "cover", "folder", "movie", "title" };
        private static readonly HashSet<string> QueuedReleaseDateKeys = new(StringComparer.OrdinalIgnoreCase);

        // Prompt suppression (so you don't get nagged every time you visit the page)
        private const string EmptyPromptDismissedKey = "VideoLibrary_EmptyPromptDismissed";
        private bool _emptyPromptShownThisSession;

        // Prevent overlapping refreshes
        private bool _isRefreshing;
        private int _filmLoadingDepth;

        // Status message auto-clear
        private DispatcherTimer? _statusClearTimer;
        private readonly HashSet<string> _queuedArtworkKeys = new(StringComparer.OrdinalIgnoreCase);
        private int _pendingArtworkLoads;
        private int _totalArtworkLoads;

        // Dialog + scan info
        private bool _scanDialogShowing;
        private VideoLibraryScanResult? _lastScanResult;
        private VideoLibraryScanResult? _pendingScanDialogResult;
        private DateTime _lastDialogAtUtc = DateTime.MinValue;

        static VideoLibraryPage()
        {
            MovieArtworkHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        }

        public VideoLibraryPage()
        {
            InitializeComponent();

            Loaded += VideoLibraryPage_Loaded;

            Loaded += VideoLibraryPage_Subscribe;
            Unloaded += VideoLibraryPage_Unsubscribe;
        }

        private void VideoLibraryPage_Subscribe(object sender, RoutedEventArgs e)
        {
            try
            {
                VideoLibraryService.Current.LibraryChanged -= VideoLibraryService_LibraryChanged;
                VideoLibraryService.Current.LibraryChanged += VideoLibraryService_LibraryChanged;

                VideoLibraryService.Current.ScanStarted -= VideoLibraryService_ScanStarted;
                VideoLibraryService.Current.ScanStarted += VideoLibraryService_ScanStarted;

                VideoLibraryService.Current.ScanFinished -= VideoLibraryService_ScanFinished;
                VideoLibraryService.Current.ScanFinished += VideoLibraryService_ScanFinished;
            }
            catch { }
        }

        private void VideoLibraryPage_Unsubscribe(object sender, RoutedEventArgs e)
        {
            UpdateCachedVideos();

            try
            {
                VideoLibraryService.Current.LibraryChanged -= VideoLibraryService_LibraryChanged;
                VideoLibraryService.Current.ScanStarted -= VideoLibraryService_ScanStarted;
                VideoLibraryService.Current.ScanFinished -= VideoLibraryService_ScanFinished;
            }
            catch { }

            StopStatusClearTimer();
        }

        private async void VideoLibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Avoid re-running if Loaded fires again for any reason
            if (_emptyPromptShownThisSession) return;

            // Restore the last in-memory library first so returning to the page is instant.
            var restoredFromCache = RestoreCachedVideos();

            // Ensure persisted library is loaded, but only add entries that are not already cached.
            await LoadPersistedAsync(showLoadingOverlay: !restoredFromCache);

            // Now prompt if still empty
            await PromptForFolderIfEmptyAsync();
        }

        private async Task PromptForFolderIfEmptyAsync()
        {
            if (_emptyPromptShownThisSession) return;
            _emptyPromptShownThisSession = true;

            // If there are items, no prompt needed
            if (_allVideos.Count > 0) return;

            // If user previously dismissed, do not show again
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.TryGetValue(EmptyPromptDismissedKey, out var v) &&
                    v is bool b && b)
                {
                    return;
                }
            }
            catch { }

            // XamlRoot is required for WinUI 3 ContentDialog
            if (XamlRoot == null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Your video library is empty",
                Content = "This section is empty. Would you like to choose the folder where your videos are stored so Zink can keep them here for future use?",
                PrimaryButtonText = "Choose folder",
                SecondaryButtonText = "Not now",
                CloseButtonText = "Don't ask again",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await PickAndAddFolderAsync();
            }
            else if (result == ContentDialogResult.None)
            {
                // "Don't ask again"
                try
                {
                    ApplicationData.Current.LocalSettings.Values[EmptyPromptDismissedKey] = true;
                }
                catch { }
            }
            // Secondary = Not now (do nothing)
        }

        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await PickAndAddFolderAsync();
        }

        private async Task PickAndAddFolderAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            string token = StorageApplicationPermissions.FutureAccessList.Add(folder);

            // Register token with background service so it keeps scanning while app is open
            try
            {
                await VideoLibraryService.Current.AddFolderTokenAsync(token);
            }
            catch { }

            await AddFolderContentsAsync(folder, token);
            await PersistAsync();

            // Trigger prune/update pass (will also drive UI status + optional dialog)
            try
            {
                await VideoLibraryService.Current.RescanAsync(pruneMissing: true);
            }
            catch { }
        }

        private async void RescanButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await VideoLibraryService.Current.RescanAsync(pruneMissing: true);
            }
            catch { }

            ClearVideoLists();
            await LoadPersistedAsync();
        }

        // ? NEW: Info button click
        private async void LibraryInfoButton_Click(object sender, RoutedEventArgs e)
        {
            var r = _lastScanResult;

            if (r == null)
            {
                await ShowLibraryInfoDialogAsync(
                    "Video Library Info",
                    "Zink keeps your Video Library updated while the app is open.\n\nAdd one or more folders, and Zink will periodically scan for new videos and remove entries for videos that were moved or deleted.");
                return;
            }

            await ShowLibraryInfoDialogAsync("Video Library Info", BuildScanFinishedMessage(r));
        }

        // ? Service: scan started -> spinner + message
        private void VideoLibraryService_ScanStarted(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StopStatusClearTimer();

                if (LibraryStatusRing != null)
                {
                    LibraryStatusRing.Visibility = Visibility.Visible;
                    LibraryStatusRing.IsActive = true;
                }

                if (LibraryStatusText != null)
                {
                    LibraryStatusText.Text = "Adding videos...";
                }
            });
        }

        // ? Service: scan finished -> message + optional dialog
        private void VideoLibraryService_ScanFinished(object? sender, VideoLibraryScanResult e)
        {
            _lastScanResult = e;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (LibraryStatusRing != null)
                {
                    LibraryStatusRing.IsActive = false;
                    LibraryStatusRing.Visibility = Visibility.Collapsed;
                }

                if (LibraryStatusText != null)
                {
                    if (_filmLoadingDepth > 0 && e.Added > 0)
                        LibraryStatusText.Text = e.Removed == 0
                            ? $"Found {e.Added} new videos. Loading films..."
                            : $"Found {e.Added} new videos and {e.Removed} removed. Loading films...";
                    else if (e.Added == 0 && e.Removed == 0)
                        LibraryStatusText.Text = "Video library is up to date.";
                    else if (e.Removed == 0)
                        LibraryStatusText.Text = $"Finished adding videos ({e.Added} new).";
                    else if (e.Added == 0)
                        LibraryStatusText.Text = $"Finished updating videos ({e.Removed} removed).";
                    else
                        LibraryStatusText.Text = $"Finished updating videos ({e.Added} new, {e.Removed} removed).";
                }

                StartStatusClearTimer();
            });

            // Show dialog only if something changed + throttle (anti-spam)
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await TryShowScanFinishedDialogAsync(e);
                }
                catch { }
            });
        }

        // ? Service: library changed -> reload list
        private void VideoLibraryService_LibraryChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (_isRefreshing) return;
                    _isRefreshing = true;

                    await LoadPersistedAsync(showLoadingOverlay: false);
                }
                catch { }
                finally
                {
                    _isRefreshing = false;
                }
            });
        }

        private void BeginFilmLoading()
        {
            _filmLoadingDepth++;
            VideoLibraryService.Current.BeginLibraryBusy();

            try
            {
                if (MovieLibraryLoadingOverlay != null)
                    MovieLibraryLoadingOverlay.Visibility = Visibility.Visible;

                if (MovieLibraryLoadingRing != null)
                    MovieLibraryLoadingRing.IsActive = true;

                UpdateFilmLoadingProgress(0, 0);
            }
            catch { }
        }

        private void EndFilmLoading()
        {
            if (_filmLoadingDepth > 0)
            {
                _filmLoadingDepth--;
                VideoLibraryService.Current.EndLibraryBusy();
            }

            if (_filmLoadingDepth != 0)
                return;

            try
            {
                if (MovieLibraryLoadingRing != null)
                    MovieLibraryLoadingRing.IsActive = false;

                if (MovieLibraryLoadingOverlay != null)
                    MovieLibraryLoadingOverlay.Visibility = Visibility.Collapsed;

                if (MovieLibraryLoadingText != null)
                    MovieLibraryLoadingText.Text = "Loading films...";
            }
            catch { }

            if (_pendingArtworkLoads > 0)
                ShowArtworkLoadingOverlay();

            TryShowPendingScanDialog();
        }

        private void UpdateFilmLoadingProgress(int loaded, int total)
        {
            try
            {
                if (MovieLibraryLoadingText == null)
                    return;

                MovieLibraryLoadingText.Text = total > 0
                    ? $"Adding the films in your folder {loaded} out of {total}"
                    : "Adding the films in your folder...";
            }
            catch { }
        }

        private void BeginArtworkLoading()
        {
            _pendingArtworkLoads++;
            _totalArtworkLoads++;

            try
            {
                if (_filmLoadingDepth == 0)
                    ShowArtworkLoadingOverlay();
            }
            catch { }
        }

        private void EndArtworkLoading()
        {
            if (_pendingArtworkLoads > 0)
                _pendingArtworkLoads--;

            try
            {
                if (_pendingArtworkLoads > 0)
                {
                    if (_filmLoadingDepth == 0)
                        ShowArtworkLoadingOverlay();
                }
                else
                {
                    HideArtworkLoadingOverlay();
                    _totalArtworkLoads = 0;
                }
            }
            catch { }
        }

        private string BuildArtworkProgressText()
        {
            var total = Math.Max(_totalArtworkLoads, _pendingArtworkLoads);
            var completed = Math.Max(0, total - _pendingArtworkLoads);

            return total > 0
                ? $"Adding cover artwork for the films {completed} out of {total}"
                : "Adding cover artwork for the films...";
        }

        private void ShowArtworkLoadingOverlay()
        {
            try
            {
                if (MovieLibraryLoadingOverlay != null)
                    MovieLibraryLoadingOverlay.Visibility = Visibility.Visible;

                if (MovieLibraryLoadingRing != null)
                    MovieLibraryLoadingRing.IsActive = true;

                if (MovieLibraryLoadingText != null)
                    MovieLibraryLoadingText.Text = BuildArtworkProgressText();
            }
            catch { }
        }

        private void HideArtworkLoadingOverlay()
        {
            if (_filmLoadingDepth != 0)
                return;

            try
            {
                if (MovieLibraryLoadingRing != null)
                    MovieLibraryLoadingRing.IsActive = false;

                if (MovieLibraryLoadingOverlay != null)
                    MovieLibraryLoadingOverlay.Visibility = Visibility.Collapsed;

                if (MovieLibraryLoadingText != null)
                    MovieLibraryLoadingText.Text = "Loading films...";
            }
            catch { }
        }

        private void TryShowPendingScanDialog()
        {
            if (_filmLoadingDepth != 0 || _pendingScanDialogResult == null)
                return;

            var result = _pendingScanDialogResult;
            _pendingScanDialogResult = null;

            DispatcherQueue.TryEnqueue(async () =>
            {
                try { await TryShowScanFinishedDialogAsync(result); }
                catch { }
            });
        }

        private async Task TryShowScanFinishedDialogAsync(VideoLibraryScanResult result)
        {
            if (result.Added == 0 && result.Removed == 0)
                return;

            if (_filmLoadingDepth > 0)
            {
                _pendingScanDialogResult = result;
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastDialogAtUtc).TotalSeconds < 20)
                return;

            _lastDialogAtUtc = now;
            await ShowLibraryInfoDialogAsync("Video Library Updated", BuildScanFinishedMessage(result));
        }

        private async Task ShowLibraryInfoDialogAsync(string title, string message)
        {
            try
            {
                if (XamlRoot == null) return;
                if (_scanDialogShowing) return;

                _scanDialogShowing = true;

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK",
                    DefaultButton = ContentDialogButton.Close
                };

                await dialog.ShowAsync();
            }
            catch { }
            finally
            {
                _scanDialogShowing = false;
            }
        }

        private static string BuildScanFinishedMessage(VideoLibraryScanResult r)
        {
            var lines = new List<string>
            {
                "Zink has finished updating your Video Library.",
                "",
                $"New videos added: {r.Added}",
                $"Videos removed: {r.Removed}",
                "",
                "Notes:",
                "- Zink updates the library while the app is open.",
                "- If you move or delete videos, they may be removed on the next scan."
            };

            return string.Join(Environment.NewLine, lines);
        }

        private void StartStatusClearTimer()
        {
            _statusClearTimer ??= new DispatcherTimer();
            _statusClearTimer.Stop();
            _statusClearTimer.Interval = TimeSpan.FromSeconds(5);
            _statusClearTimer.Tick -= StatusClearTimer_Tick;
            _statusClearTimer.Tick += StatusClearTimer_Tick;
            _statusClearTimer.Start();
        }

        private void StopStatusClearTimer()
        {
            if (_statusClearTimer != null)
            {
                _statusClearTimer.Stop();
                _statusClearTimer.Tick -= StatusClearTimer_Tick;
            }
        }

        private void StatusClearTimer_Tick(object? sender, object e)
        {
            StopStatusClearTimer();
            try
            {
                if (LibraryStatusText != null)
                    LibraryStatusText.Text = "";
            }
            catch { }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyVideoSearch();
        }

        private void SortByBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyVideoSearch();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchBox != null)
                SearchBox.Text = "";

            ApplyVideoSearch();
        }

        private void ClearVideoLists()
        {
            _allVideos.Clear();
            Videos.Clear();
            UpdateSearchResultText();
        }

        private void AddVideoItem(VideoItem item)
        {
            _allVideos.Add(item);

            ApplyVideoSearch();
            UpdateCachedVideos();
        }

        private void AddVideoItems(IEnumerable<VideoItem> items, bool refreshView = true)
        {
            var added = false;

            foreach (var item in items)
            {
                _allVideos.Add(item);
                added = true;
            }

            if (!added)
                return;

            if (refreshView)
            {
                ApplyVideoSearch();
                UpdateCachedVideos();
            }
        }

        private bool RestoreCachedVideos()
        {
            try
            {
                if (!_hasLoadedVideoCache || CachedVideos.Count == 0)
                    return false;

                _allVideos.Clear();
                _allVideos.AddRange(CachedVideos);
                ApplyVideoSearch();
                return _allVideos.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateCachedVideos()
        {
            try
            {
                CachedVideos.Clear();
                CachedVideos.AddRange(_allVideos);
                _hasLoadedVideoCache = CachedVideos.Count > 0;
            }
            catch { }
        }

        private bool HasVideoItem(string folderToken, string relativePath)
        {
            return _allVideos.Any(v =>
                string.Equals(v.FolderToken, folderToken, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildVideoKey(string folderToken, string relativePath)
        {
            return $"{folderToken}|{relativePath}";
        }

        private void ApplyVideoSearch()
        {
            var query = GetSearchQuery();
            var filtered = GetSortedVideos(_allVideos.Where(v => VideoMatchesSearch(v, query))).ToList();

            Videos.Clear();
            foreach (var item in filtered)
            {
                Videos.Add(item);
            }

            UpdateSearchResultText();
        }

        private string GetSearchQuery()
        {
            try { return SearchBox?.Text?.Trim() ?? ""; }
            catch { return ""; }
        }

        private IEnumerable<VideoItem> GetSortedVideos(IEnumerable<VideoItem> videos)
        {
            var sortKey = GetSelectedSortKey();

            return sortKey switch
            {
                "title-az" => videos.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
                "title-za" => videos.OrderByDescending(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
                "longest" => videos.OrderByDescending(v => v.Duration).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
                "shortest" => videos.OrderBy(v => v.Duration == TimeSpan.Zero).ThenBy(v => v.Duration).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
                "type" => videos.OrderBy(v => Path.GetExtension(v.FileName), StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => videos.Reverse()
            };
        }

        private string GetSelectedSortKey()
        {
            try
            {
                if (SortByBox?.SelectedItem is ComboBoxItem item &&
                    item.Tag is string tag &&
                    !string.IsNullOrWhiteSpace(tag))
                {
                    return tag;
                }
            }
            catch { }

            return "recent";
        }
        private static bool VideoMatchesSearch(VideoItem item, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();

            if (terms.Length == 0)
                return true;

            var extension = Path.GetExtension(item.FileName);
            var type = extension.TrimStart('.');
            var searchable = new[]
            {
                item.Name,
                item.FileName,
                item.RelativePath,
                extension,
                type,
                item.DurationText
            };

            return terms.All(term =>
                searchable.Any(value =>
                    !string.IsNullOrWhiteSpace(value) &&
                    value.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        private void UpdateSearchResultText()
        {
            try
            {
                if (SearchResultText == null) return;

                var query = GetSearchQuery();
                if (string.IsNullOrWhiteSpace(query))
                {
                    var libraryCount = GetStableLibraryCount(_allVideos.Count);
                    SearchResultText.Text = libraryCount == 1
                        ? "1 film in your library"
                        : $"{libraryCount} films in your library";
                    return;
                }

                SearchResultText.Text = Videos.Count == 1
                    ? $"1 film found for \"{query}\""
                    : $"{Videos.Count} films found for \"{query}\"";
            }
            catch { }
        }

        private static int GetStableLibraryCount(int currentCount)
        {
            RememberLibraryCount(currentCount);
            return Math.Max(currentCount, _lastKnownLibraryCount);
        }

        private static void RememberLibraryCount(int count)
        {
            if (count > _lastKnownLibraryCount)
                _lastKnownLibraryCount = count;
        }
        private async Task AddFolderContentsAsync(StorageFolder folder, string folderToken)
        {
            BeginFilmLoading();
            try
            {
                var q = new QueryOptions(CommonFileQuery.DefaultQuery, _exts) { FolderDepth = FolderDepth.Deep };
                var result = folder.CreateFileQueryWithOptions(q);
                var files = await result.GetFilesAsync();
                var loadedFilms = 0;
                var totalFilms = files.Count;
                UpdateFilmLoadingProgress(loadedFilms, totalFilms);

                var existingKeys = _allVideos
                    .Select(v => BuildVideoKey(v.FolderToken, v.RelativePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var pendingItems = new List<VideoItem>();

                foreach (var file in files)
                {
                    var relPath = GetRelativePath(folder.Path, file.Path);
                    var key = BuildVideoKey(folderToken, relPath);

                    if (!existingKeys.Add(key))
                    {
                        loadedFilms++;
                        UpdateFilmLoadingProgress(loadedFilms, totalFilms);
                        continue;
                    }

                    var item = new VideoItem
                    {
                        Name = file.DisplayName,
                        FileName = file.Name,
                        FolderToken = folderToken,
                        RelativePath = relPath
                    };

                    await PopulateThumbnailAndDurationAsync(file, item);
                    pendingItems.Add(item);
                    loadedFilms++;
                    UpdateFilmLoadingProgress(loadedFilms, totalFilms);

                    if (pendingItems.Count >= 25)
                    {
                        AddVideoItems(pendingItems, refreshView: false);
                        pendingItems.Clear();
                    }
                }

                AddVideoItems(pendingItems, refreshView: false);
                ApplyVideoSearch();
                UpdateCachedVideos();
            }
            finally
            {
                EndFilmLoading();
            }
        }

        private static string GetRelativePath(string baseDir, string fullPath)
        {
            try { return Path.GetRelativePath(baseDir, fullPath); }
            catch { return Path.GetFileName(fullPath); }
        }

        private async Task PopulateThumbnailAndDurationAsync(StorageFile file, VideoItem item)
        {
            try
            {
                var hasLocalArtwork = await TrySetLocalMovieArtworkAsync(file, item);

                if (item.Thumbnail == null)
                {
                    using (var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 220, ThumbnailOptions.UseCurrentScale))
                    {
                        if (thumb != null)
                        {
                            var bmp = new BitmapImage();
                            await bmp.SetSourceAsync(thumb);
                            item.Thumbnail = bmp;
                        }
                    }
                }

                var props = await file.Properties.GetVideoPropertiesAsync();
                if (props != null && props.Duration != TimeSpan.Zero)
                    item.Duration = props.Duration;

                if (!hasLocalArtwork)
                    QueueMovieArtworkLoad(item);

                QueueReleaseDateLoad(item);
            }
            catch { }
        }

        private void QueueReleaseDateLoad(VideoItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.ReleaseDateText))
                return;

            var key = BuildVideoKey(item.FolderToken, item.RelativePath);
            if (!QueuedReleaseDateKeys.Add(key))
                return;

            _ = LoadReleaseDateInBackgroundAsync(item, key);
        }

        private void QueueReleaseDateLoads(IEnumerable<VideoItem> items)
        {
            foreach (var item in items)
                QueueReleaseDateLoad(item);
        }

        private async Task LoadReleaseDateInBackgroundAsync(VideoItem item, string key)
        {
            try
            {
                var info = await FilmInfoService.GetFilmInfoAsync(item.Name);
                if (info == null || string.IsNullOrWhiteSpace(info.ReleaseDateText))
                    return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    try { item.ReleaseDateText = info.ReleaseDateText; }
                    catch { }
                });
            }
            catch { }
            finally
            {
                QueuedReleaseDateKeys.Remove(key);
            }
        }

        private void QueueMovieArtworkLoad(VideoItem item)
        {
            var key = BuildVideoKey(item.FolderToken, item.RelativePath);
            if (!_queuedArtworkKeys.Add(key))
                return;

            BeginArtworkLoading();

            _ = LoadMovieArtworkInBackgroundAsync(item, key);
        }

        private void QueueMovieArtworkLoads(IEnumerable<VideoItem> items)
        {
            foreach (var item in items)
                QueueMovieArtworkLoad(item);
        }

        private async Task LoadMovieArtworkInBackgroundAsync(VideoItem item, string key)
        {
            try
            {
                await MovieArtworkLookupGate.WaitAsync();
                byte[]? posterBytes;

                try
                {
                    posterBytes = await TryFindMovieArtworkBytesAsync(item.Name);
                }
                finally
                {
                    MovieArtworkLookupGate.Release();
                }

                if (posterBytes == null || posterBytes.Length == 0)
                    return;

                DispatcherQueue.TryEnqueue(async () =>
                {
                    try { await SetMovieArtworkAsync(item, posterBytes); }
                    catch { }
                });
            }
            catch { }
            finally
            {
                _queuedArtworkKeys.Remove(key);
                DispatcherQueue.TryEnqueue(EndArtworkLoading);
            }
        }

        private static async Task SetMovieArtworkAsync(VideoItem item, byte[] posterBytes)
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(posterBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 440 };
            await bitmap.SetSourceAsync(stream);
            item.Thumbnail = bitmap;
        }

        private static async Task<bool> TrySetLocalMovieArtworkAsync(StorageFile file, VideoItem item)
        {
            try
            {
                var folder = await file.GetParentAsync();
                if (folder == null)
                    return false;

                var baseName = Path.GetFileNameWithoutExtension(file.Name);
                foreach (var artworkName in GetLocalMovieArtworkFileNames(baseName))
                {
                    if (await folder.TryGetItemAsync(artworkName) is StorageFile artworkFile)
                    {
                        using var stream = await artworkFile.OpenReadAsync();
                        var bmp = new BitmapImage { DecodePixelWidth = 440 };
                        await bmp.SetSourceAsync(stream);
                        item.Thumbnail = bmp;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static IEnumerable<string> GetLocalMovieArtworkFileNames(string baseName)
        {
            foreach (var ext in MovieArtworkExtensions)
                yield return baseName + ext;

            foreach (var name in MovieArtworkNames)
                foreach (var ext in MovieArtworkExtensions)
                    yield return name + ext;
        }

        private static async Task<byte[]?> TryFindMovieArtworkBytesAsync(string title)
        {
            var searchTitle = CleanMovieSearchTitle(title);
            if (string.IsNullOrWhiteSpace(searchTitle))
                return null;

            if (MovieArtworkCache.TryGetValue(searchTitle, out var cached))
                return cached;

            byte[]? result = null;

            try
            {
                var artworkUrl = await TryFindImdbMovieArtworkUrlAsync(searchTitle);
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                    result = await MovieArtworkHttpClient.GetByteArrayAsync(artworkUrl);
            }
            catch { }

            MovieArtworkCache[searchTitle] = result;
            return result;
        }

        private static async Task<string?> TryFindImdbMovieArtworkUrlAsync(string searchTitle)
        {
            var suggestionKey = BuildImdbSuggestionKey(searchTitle);
            if (string.IsNullOrWhiteSpace(suggestionKey))
                return null;

            var url = $"https://v3.sg.media-imdb.com/suggestion/{suggestionKey[0]}/{Uri.EscapeDataString(suggestionKey)}.json";
            using var response = await MovieArtworkHttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("d", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? fallbackUrl = null;
            var wanted = NormalizeMovieTitleForMatch(searchTitle);

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("l", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    !result.TryGetProperty("i", out var imageElement) ||
                    !imageElement.TryGetProperty("imageUrl", out var urlElement) ||
                    urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var imageUrl = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(imageUrl))
                    continue;

                fallbackUrl ??= imageUrl;

                var candidate = NormalizeMovieTitleForMatch(nameElement.GetString() ?? string.Empty);
                if (string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase))
                    return imageUrl;
            }

            return fallbackUrl;
        }

        private static string BuildImdbSuggestionKey(string title)
        {
            var key = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            return key;
        }

        private static string NormalizeMovieTitleForMatch(string title)
        {
            var normalized = title.Replace('&', ' ');
            normalized = Regex.Replace(normalized, @"[^a-zA-Z0-9]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static string CleanMovieSearchTitle(string title)
        {
            var cleaned = Path.GetFileNameWithoutExtension(title ?? string.Empty);
            cleaned = cleaned.Replace('.', ' ').Replace('_', ' ');
            cleaned = Regex.Replace(cleaned, @"\b(480p|720p|1080p|2160p|4k|bluray|brrip|web[- ]?dl|webrip|hdrip|dvdrip|x264|x265|h264|h265|hevc|aac|dts|yts|proper|repack)\b", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]*\]|\{[^\}]*\}", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return cleaned;
        }
        private async Task LoadPersistedAsync(bool showLoadingOverlay = true)
        {
            var loadingStarted = false;

            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(LibraryFileName) as StorageFile;
                if (file == null)
                {
                    UpdateSearchResultText();
                    return;
                }

                using var s = await file.OpenReadAsync();
                using var stream = s.AsStreamForRead();
                var saved = await JsonSerializer.DeserializeAsync<VideoLibrarySave>(stream);

                if (saved?.Items == null || saved.Items.Count == 0)
                {
                    ClearVideoLists();
                    UpdateCachedVideos();
                    return;
                }

                RememberLibraryCount(saved.Items.Count);

                var savedKeys = saved.Items
                    .Select(i => BuildVideoKey(i.FolderToken, i.RelativePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var removedStaleItems = _allVideos.RemoveAll(v =>
                    !savedKeys.Contains(BuildVideoKey(v.FolderToken, v.RelativePath))) > 0;

                var currentKeys = _allVideos
                    .Select(v => BuildVideoKey(v.FolderToken, v.RelativePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missingEntries = saved.Items
                    .Where(entry => !currentKeys.Contains(BuildVideoKey(entry.FolderToken, entry.RelativePath)))
                    .ToList();

                if (removedStaleItems)
                    ApplyVideoSearch();
                else
                    UpdateSearchResultText();

                if (showLoadingOverlay || missingEntries.Count > 0)
                {
                    BeginFilmLoading();
                    loadingStarted = true;
                }

                var pendingItems = new List<VideoItem>();
                var metadataItems = new List<(StorageFile File, VideoItem Item)>();
                var loadedFilms = 0;
                var totalFilms = missingEntries.Count;
                UpdateFilmLoadingProgress(loadedFilms, totalFilms);

                foreach (var entry in missingEntries)
                {
                    try
                    {
                        var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(entry.FolderToken);
                        var storageItem = await folder.TryGetItemAsync(entry.RelativePath);

                        if (storageItem is StorageFile f)
                        {
                            var item = new VideoItem
                            {
                                Name = entry.Name,
                                FileName = entry.FileName,
                                FolderToken = entry.FolderToken,
                                RelativePath = entry.RelativePath
                            };

                            pendingItems.Add(item);
                            metadataItems.Add((f, item));

                            if (pendingItems.Count >= 25)
                            {
                                AddVideoItems(pendingItems, refreshView: false);
                                pendingItems.Clear();
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        loadedFilms++;
                        UpdateFilmLoadingProgress(loadedFilms, totalFilms);
                    }
                }

                AddVideoItems(pendingItems, refreshView: false);
                ApplyVideoSearch();
                UpdateCachedVideos();

                _ = LoadVideoMetadataInBackgroundAsync(metadataItems);
            }
            catch { }
            finally
            {
                if (loadingStarted)
                    EndFilmLoading();
            }
        }

        private async Task LoadVideoMetadataInBackgroundAsync(List<(StorageFile File, VideoItem Item)> items)
        {
            if (items.Count == 0)
            {
                QueueMovieArtworkLoads(_allVideos.Where(v => v.Thumbnail == null));
                QueueReleaseDateLoads(_allVideos);
                return;
            }

            foreach (var (file, item) in items)
            {
                await PopulateThumbnailAndDurationAsync(file, item);
                await Task.Yield();
            }

            QueueMovieArtworkLoads(items.Select(i => i.Item).Where(v => v.Thumbnail == null));
            QueueReleaseDateLoads(items.Select(i => i.Item));
        }

        private async Task PersistAsync()
        {
            try
            {
                var save = new VideoLibrarySave
                {
                    FolderTokens = _allVideos.Select(v => v.FolderToken).Distinct().ToList(),
                    Items = _allVideos.Select(v => new VideoLibraryEntry
                    {
                        Name = v.Name,
                        FileName = v.FileName,
                        FolderToken = v.FolderToken,
                        RelativePath = v.RelativePath
                    }).ToList()
                };

                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(LibraryFileName, CreationCollisionOption.ReplaceExisting);
                using var s = await file.OpenStreamForWriteAsync();
                await JsonSerializer.SerializeAsync(s, save, new JsonSerializerOptions { WriteIndented = true });
                await s.FlushAsync();
            }
            catch { }
        }

        // Play button handler
        private async void PlayWithVideoPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not VideoItem item) return;
            await PlayInVideoPlayerAsync(item);
        }

        private void LearnAboutFilm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not VideoItem item) return;

            try
            {
                App.MainWindow.MainFrame.Navigate(typeof(FilmInfoPage), item);
            }
            catch { }
        }

        // Thumbnail click -> play in VideoPlayerPage
        private async void GridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is VideoItem item)
                await PlayInVideoPlayerAsync(item);
        }

        // Shared play logic
        private async Task PlayInVideoPlayerAsync(VideoItem item)
        {
            try
            {
                var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(item.FolderToken);
                var storageItem = await folder.TryGetItemAsync(item.RelativePath);
                if (storageItem is StorageFile file)
                    App.MainWindow.MainFrame.Navigate(typeof(VideoPlayerPage), file);
            }
            catch { }
        }
    }

    public sealed class VideoItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FolderToken { get; set; } = "";
        public string RelativePath { get; set; } = "";
        private TimeSpan _duration = TimeSpan.Zero;
        private BitmapImage? _thumbnail;
        private string _releaseDateText = "";

        public TimeSpan Duration
        {
            get => _duration;
            set
            {
                if (_duration == value)
                    return;

                _duration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationText));
            }
        }

        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value))
                    return;

                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        public string ReleaseDateText
        {
            get => _releaseDateText;
            set
            {
                if (string.Equals(_releaseDateText, value, StringComparison.Ordinal))
                    return;

                _releaseDateText = value ?? "";
                OnPropertyChanged();
            }
        }

        public string DurationText => Duration == TimeSpan.Zero ? "" : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:00}";
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class VideoLibrarySave
    {
        public List<string> FolderTokens { get; set; } = new();
        public List<VideoLibraryEntry> Items { get; set; } = new();
    }

    public sealed class VideoLibraryEntry
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FolderToken { get; set; } = "";
        public string RelativePath { get; set; } = "";
    }
}

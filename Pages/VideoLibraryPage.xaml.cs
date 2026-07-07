using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        private const string LibraryFileName = "video_library.json";
        private readonly string[] _exts = new[] { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".wmv" };
        private static readonly HttpClient MovieArtworkHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly Dictionary<string, byte[]?> MovieArtworkCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] MovieArtworkExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        private static readonly string[] MovieArtworkNames = new[] { "poster", "cover", "folder", "movie", "title" };

        // Prompt suppression (so you don't get nagged every time you visit the page)
        private const string EmptyPromptDismissedKey = "VideoLibrary_EmptyPromptDismissed";
        private bool _emptyPromptShownThisSession;

        // Prevent overlapping refreshes
        private bool _isRefreshing;

        // Status message auto-clear
        private DispatcherTimer? _statusClearTimer;

        // Dialog + scan info
        private bool _scanDialogShowing;
        private VideoLibraryScanResult? _lastScanResult;
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

            // Ensure persisted library is loaded first
            await LoadPersistedAsync();

            // Now prompt if still empty
            await PromptForFolderIfEmptyAsync();
        }

        private async Task PromptForFolderIfEmptyAsync()
        {
            if (_emptyPromptShownThisSession) return;
            _emptyPromptShownThisSession = true;

            // If there are items, no prompt needed
            if (Videos.Count > 0) return;

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
                CloseButtonText = "Don’t ask again",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await PickAndAddFolderAsync();
            }
            else if (result == ContentDialogResult.None)
            {
                // "Don’t ask again"
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

            Videos.Clear();
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
                    if (e.Added == 0 && e.Removed == 0)
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
                    if (e.Added == 0 && e.Removed == 0) return;

                    var now = DateTime.UtcNow;
                    if ((now - _lastDialogAtUtc).TotalSeconds < 20) return;
                    _lastDialogAtUtc = now;

                    await ShowLibraryInfoDialogAsync("Video Library Updated", BuildScanFinishedMessage(e));
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

                    Videos.Clear();
                    await LoadPersistedAsync();
                }
                catch { }
                finally
                {
                    _isRefreshing = false;
                }
            });
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

        private async Task AddFolderContentsAsync(StorageFolder folder, string folderToken)
        {
            var q = new QueryOptions(CommonFileQuery.DefaultQuery, _exts) { FolderDepth = FolderDepth.Deep };
            var result = folder.CreateFileQueryWithOptions(q);
            var files = await result.GetFilesAsync();

            foreach (var file in files)
            {
                var relPath = GetRelativePath(folder.Path, file.Path);

                if (Videos.Any(v => v.FolderToken == folderToken &&
                                   v.RelativePath.Equals(relPath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var item = new VideoItem
                {
                    Name = file.DisplayName,
                    FileName = file.Name,
                    FolderToken = folderToken,
                    RelativePath = relPath
                };

                await PopulateThumbnailAndDurationAsync(file, item);
                Videos.Add(item);
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
                await PopulateMovieArtworkAsync(file, item);

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
            }
            catch { }
        }

        private async Task PopulateMovieArtworkAsync(StorageFile file, VideoItem item)
        {
            if (await TrySetLocalMovieArtworkAsync(file, item))
                return;

            var posterBytes = await TryFindMovieArtworkBytesAsync(item.Name);
            if (posterBytes == null || posterBytes.Length == 0)
                return;

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

        private async Task LoadPersistedAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(LibraryFileName) as StorageFile;
                if (file == null) return;

                using var s = await file.OpenReadAsync();
                using var stream = s.AsStreamForRead();
                var saved = await JsonSerializer.DeserializeAsync<VideoLibrarySave>(stream);

                if (saved?.Items == null || saved.Items.Count == 0) return;

                foreach (var entry in saved.Items)
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

                            await PopulateThumbnailAndDurationAsync(f, item);
                            Videos.Add(item);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private async Task PersistAsync()
        {
            try
            {
                var save = new VideoLibrarySave
                {
                    FolderTokens = Videos.Select(v => v.FolderToken).Distinct().ToList(),
                    Items = Videos.Select(v => new VideoLibraryEntry
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

    public sealed class VideoItem
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FolderToken { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
        public BitmapImage? Thumbnail { get; set; }
        public string DurationText => Duration == TimeSpan.Zero ? "" : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:00}";
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

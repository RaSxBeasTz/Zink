using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.AccessCache;
using Windows.Storage.Search;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using TagLib;
using System.IO;
using System;
using Windows.Storage.Streams;
using System.Text.Json;

namespace Zink.Pages
{
    public sealed partial class MusicLibraryPage : Page, INotifyPropertyChanged
    {
        public ObservableCollection<SongInfo> Songs { get; set; } = new();

        private string _searchText = "";

        public IEnumerable<SongInfo> VisibleSongs => string.IsNullOrWhiteSpace(_searchText)
            ? Songs
            : Songs.Where(MatchesSearch).ToList();

        public string SearchResultsText => string.IsNullOrWhiteSpace(_searchText)
            ? ""
            : VisibleSongs.Any()
                ? $"{VisibleSongs.Count()} of {Songs.Count} songs"
                : "No songs match your search. Try another name or clear the search.";

        private bool MatchesSearch(SongInfo song)
        {
            var terms = _searchText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var fields = new[] { song.Title, song.Artist, song.Album, Path.GetFileName(song.FilePath) };
            return terms.All(term => fields.Any(field =>
                field?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void MusicSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            _searchText = sender.Text ?? "";
            UpdateSearchResults();
        }

        private void UpdateSearchResults()
        {
            OnPropertyChanged(nameof(VisibleSongs));
            OnPropertyChanged(nameof(SearchResultsText));
        }

        public string PickedFolderPath
        {
            get => _pickedFolderPath;
            set
            {
                _pickedFolderPath = value;
                OnPropertyChanged(nameof(PickedFolderPath));
            }
        }

        private string _pickedFolderToken;
        private string _pickedFolderPath;
        private StorageFolder _pickedFolder;
        private DispatcherTimer _watcherTimer;
        private int _musicLoadingDepth;
        private bool _checkingNewSongs;
        private bool _enrichmentRunning;
        private bool _updatingSongList;
        private readonly Queue<(StorageFile File, SongInfo Song, bool Verified, string Title, int Separator)> _pendingEnrichments = new();
        private static readonly object ScanLogLock = new();
        private static string ScanLogPath => Path.Combine(
            ApplicationData.Current.LocalFolder.Path, "MusicLibraryScan.txt");

        private static void LogMusicScan(string message)
        {
            try
            {
                lock (ScanLogLock)
                    System.IO.File.AppendAllText(ScanLogPath,
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private async void OpenScanLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LogMusicScan("Scan log opened.");
                await global::Windows.System.Launcher.LaunchFileAsync(
                    await StorageFile.GetFileFromPathAsync(ScanLogPath));
            }
            catch (Exception ex)
            {
                Zink.Services.NotificationService.Instance.RecordActivity(
                    "Music scan log", $"Log location: {ScanLogPath}\n{ex.Message}");
            }
        }

        // Prevents multiple prompts in the same page instance
        private bool _emptyLibraryPromptShown;

        // NEW: if we need to show the dialog but XamlRoot isn't ready yet, show it after Loaded
        private bool _pendingShowEmptyDialog;

        public event PropertyChangedEventHandler PropertyChanged;

        public MusicLibraryPage()
        {
            this.InitializeComponent();
            this.DataContext = this;
            Zink.Services.MusicLibraryMetadataLookup.Instance.EnableDiskCache(
                Path.Combine(ApplicationData.Current.LocalFolder.Path, "MusicMetadataCache.json"));
            Songs.CollectionChanged += (_, _) =>
            {
                if (!_updatingSongList && !string.IsNullOrWhiteSpace(_searchText))
                    UpdateSearchResults();
            };
            this.NavigationCacheMode = NavigationCacheMode.Required;

            // NEW: ensure we can show the dialog after the page is actually in the tree
            this.Loaded += MusicLibraryPage_Loaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LogMusicScan($"Library opened. Cached songs: {Songs.Count}; folder: {_pickedFolder?.Path ?? "not loaded"}.");

            if (_pickedFolder != null)
            {
                // Keep the existing cards and artwork when returning to this cached page.
                // The watcher imports only files that have not been added yet.
                StartWatcher();
                return;
            }

            bool loadedAnySongs = false;

            if (ApplicationData.Current.LocalSettings.Values.TryGetValue("MusicLibraryFolderToken", out object tokenObj))
            {
                _pickedFolderToken = tokenObj as string;
                if (!string.IsNullOrEmpty(_pickedFolderToken) &&
                    StorageApplicationPermissions.FutureAccessList.ContainsItem(_pickedFolderToken))
                {
                    _pickedFolder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(_pickedFolderToken);
                    if (_pickedFolder != null)
                    {
                        PickedFolderPath = _pickedFolder.Path;
                        await LoadSongsFromFolder(_pickedFolder);
                        StartWatcher();

                        loadedAnySongs = Songs.Count > 0;
                    }
                }
            }

            // If nothing loaded (no folder yet OR folder has no music), show the dialog once
            if (!loadedAnySongs && !_emptyLibraryPromptShown)
            {
                _emptyLibraryPromptShown = true;

                // NEW: If XamlRoot isn't ready yet, defer until Loaded event
                if (GetDialogXamlRoot() == null)
                {
                    _pendingShowEmptyDialog = true;
                }
                else
                {
                    await ShowEmptyLibraryDialogAsync();
                }
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopWatcher();
            base.OnNavigatedFrom(e);
        }

        // NEW: Loaded handler to show dialog when XamlRoot becomes available
        private async void MusicLibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_pendingShowEmptyDialog)
            {
                _pendingShowEmptyDialog = false;

                // Now the page is in the tree, XamlRoot should exist
                await ShowEmptyLibraryDialogAsync();
            }
        }

        // NEW: Safely resolve a XamlRoot for dialogs
        private Microsoft.UI.Xaml.XamlRoot GetDialogXamlRoot()
        {
            // Prefer the page XamlRoot if present
            if (this.XamlRoot != null)
                return this.XamlRoot;

            // Fallback to window content XamlRoot
            if (App.MainWindow?.Content is FrameworkElement fe && fe.XamlRoot != null)
                return fe.XamlRoot;

            return null;
        }

        // Dialog shown on page load when library is empty
        private async Task ShowEmptyLibraryDialogAsync()
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot == null)
            {
                // Should be rare now, but safe-guard: just don't show if still not available
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "No music found",
                Content = "Would you like to add the folder where your music is saved? Zink will show the music inside each folder before you add it.",
                CloseButtonText = "No thanks",          // left
                PrimaryButtonText = "Yes, okay",        // right (blue)
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await PickFolderAndLoadAsync(showSelectedDialog: false);
            }
        }

        private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await PickFolderAndLoadAsync(showSelectedDialog: true);
        }

        private async void RefreshLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshLibraryAsync();
        }

        private async Task RefreshLibraryAsync()
        {
            if (_pickedFolder == null || _musicLoadingDepth > 0 || _checkingNewSongs || _enrichmentRunning)
                return;

            await LoadSongsFromFolder(_pickedFolder);
        }

        // Shared picker logic (so the page-load dialog can trigger the same flow)
        private async Task PickFolderAndLoadAsync(bool showSelectedDialog)
        {
            StopWatcher();

            var folder = await ShowMusicFolderBrowserAsync();
            if (folder != null)
            {
                _pickedFolder = folder;

                _pickedFolderToken = StorageApplicationPermissions.FutureAccessList.Add(folder);
                ApplicationData.Current.LocalSettings.Values["MusicLibraryFolderToken"] = _pickedFolderToken;

                PickedFolderPath = folder.Path;

                if (showSelectedDialog)
                {
                    var xamlRoot = GetDialogXamlRoot();

                    // If for some reason it's still null, don't crash—just skip the dialog
                    if (xamlRoot != null)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Music Folder Selected",
                            Content = $"Zink will add the music from:\n{folder.Path}",
                            CloseButtonText = "OK",
                            XamlRoot = xamlRoot
                        };
                        await dialog.ShowAsync();
                    }
                }

                await LoadSongsFromFolder(folder);
                StartWatcher();
            }
            else if (_pickedFolder != null)
            {
                StartWatcher();
            }
        }

        private async Task<StorageFolder> ShowMusicFolderBrowserAsync()
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot == null) return null;

            StorageFolder currentFolder = _pickedFolder;
            if (currentFolder == null)
            {
                try
                {
                    var musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    if (!string.IsNullOrWhiteSpace(musicPath))
                        currentFolder = await StorageFolder.GetFolderFromPathAsync(musicPath);
                }
                catch { }
            }

            if (currentFolder == null)
            {
                var initialPicker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
                initialPicker.FileTypeFilter.Add("*");
                var initialHwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(initialPicker, initialHwnd);
                currentFolder = await initialPicker.PickSingleFolderAsync();
                if (currentFolder == null) return null;
            }

            var pathText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontSize = 12
            };
            var statusText = new TextBlock { Opacity = 0.7, FontSize = 12 };
            var entries = new ObservableCollection<MusicFolderBrowserEntry>();
            var list = new ListView
            {
                ItemsSource = entries,
                DisplayMemberPath = nameof(MusicFolderBrowserEntry.DisplayName),
                IsItemClickEnabled = true,
                SelectionMode = ListViewSelectionMode.None,
                MinHeight = 330,
                MaxHeight = 480
            };
            var upButton = new Button { Content = "Up" };
            var anotherLocationButton = new Button { Content = "Choose another location" };
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            toolbar.Children.Add(upButton);
            toolbar.Children.Add(anotherLocationButton);

            var content = new StackPanel { Spacing = 10, MinWidth = 620 };
            content.Children.Add(new TextBlock
            {
                Text = "Open a folder to preview its music, then choose Add this folder.",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(toolbar);
            content.Children.Add(pathText);
            content.Children.Add(list);
            content.Children.Add(statusText);

            var dialog = new ContentDialog
            {
                Title = "Add a music folder",
                Content = content,
                PrimaryButtonText = "Add this folder",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            async Task ShowFolderAsync(StorageFolder folder)
            {
                currentFolder = folder;
                pathText.Text = folder.Path;
                statusText.Text = "Loading folder...";
                entries.Clear();
                try
                {
                    var folders = await folder.GetFoldersAsync();
                    foreach (var childFolder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        entries.Add(new MusicFolderBrowserEntry
                        {
                            DisplayName = $"📁  {childFolder.Name}",
                            Folder = childFolder
                        });
                    }

                    var musicFiles = (await folder.GetFilesAsync())
                        .Where(IsSupportedMusicFile)
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (var file in musicFiles)
                        entries.Add(new MusicFolderBrowserEntry { DisplayName = $"♫  {file.Name}" });

                    statusText.Text = musicFiles.Count == 1
                        ? "1 music file in this folder"
                        : $"{musicFiles.Count} music files in this folder";
                }
                catch
                {
                    statusText.Text = "Zink could not open this folder.";
                }
            }

            list.ItemClick += async (_, args) =>
            {
                if (args.ClickedItem is MusicFolderBrowserEntry entry && entry.Folder != null)
                    await ShowFolderAsync(entry.Folder);
            };
            upButton.Click += async (_, __) =>
            {
                try
                {
                    var parent = await currentFolder.GetParentAsync();
                    if (parent != null) await ShowFolderAsync(parent);
                }
                catch { }
            };
            anotherLocationButton.Click += async (_, __) =>
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
                picker.FileTypeFilter.Add("*");
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var selected = await picker.PickSingleFolderAsync();
                if (selected != null) await ShowFolderAsync(selected);
            };

            await ShowFolderAsync(currentFolder);
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? currentFolder : null;
        }

        private static bool IsSupportedMusicFile(StorageFile file)
        {
            var supportedTypes = new[]
            {
                ".mp3", ".mp4", ".flac", ".wav", ".m4a", ".aac", ".wma",
                ".ogg", ".alac", ".aiff", ".opus"
            };
            return supportedTypes.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase);
        }

        private sealed class MusicFolderBrowserEntry
        {
            public string DisplayName { get; set; }
            public StorageFolder Folder { get; set; }
        }

        private async Task LoadSongsFromFolder(StorageFolder folder, bool showLoading = true)
        {
            var scanTimer = System.Diagnostics.Stopwatch.StartNew();
            if (showLoading)
                BeginMusicLoading();
            try
            {
                var files = await GetAllMusicFilesAsync(folder);
                LogMusicScan($"Full scan: {folder.Path}; files found: {files.Count}.");
                var previousSongs = Songs.ToDictionary(s => s.FilePath, StringComparer.OrdinalIgnoreCase);
                _updatingSongList = true;
                Songs.Clear();
                if (showLoading)
                    UpdateMusicLoadingProgress(0, files.Count);

                var loadedSongs = 0;
                foreach (var file in files)
                {
                    SongInfo song;
                    if (previousSongs.TryGetValue(file.Path, out var previous) &&
                        previous.Artwork != null && !string.IsNullOrWhiteSpace(previous.Artist) &&
                        Zink.Services.MusicFilenameMetadataMatcher.IsCanonicalFilename(file.Name,
                            $"{previous.Artist} - {previous.Title}{Path.GetExtension(file.Name)}") &&
                        await HasUnchangedSongFileAsync(file, previous))
                    {
                        song = previous;
                        LogMusicScan($"Scan reused identified song and artwork: {file.Path}");
                    }
                    else
                        song = await LoadSongInfoAsync(file);
                    if (song != null)
                        Songs.Add(song);

                    loadedSongs++;
                    if (showLoading)
                        UpdateMusicLoadingProgress(loadedSongs, files.Count);
                }
            }
            finally
            {
                LogMusicScan($"Local scan ready: {Songs.Count} songs; {scanTimer.Elapsed.TotalSeconds:0.00}s; background updates pending: {_pendingEnrichments.Count}.");
                _updatingSongList = false;
                UpdateSearchResults();
                if (showLoading)
                    EndMusicLoading();
                _ = ProcessPendingEnrichmentsAsync();
            }
        }

        private void BeginMusicLoading()
        {
            _musicLoadingDepth++;
            MusicLibraryLoadingOverlay.Visibility = Visibility.Visible;
            MusicLibraryLoadingRing.IsActive = true;
            LibraryStatusRing.Visibility = Visibility.Visible;
            LibraryStatusRing.IsActive = true;
            UpdateMusicLoadingProgress(0, 0);
        }

        private void EndMusicLoading()
        {
            if (_musicLoadingDepth > 0)
                _musicLoadingDepth--;

            if (_musicLoadingDepth != 0)
                return;

            MusicLibraryLoadingRing.IsActive = false;
            MusicLibraryLoadingOverlay.Visibility = Visibility.Collapsed;
            LibraryStatusRing.IsActive = false;
            LibraryStatusRing.Visibility = Visibility.Collapsed;
            LibraryStatusText.Text = Songs.Count > 0 ? $"{Songs.Count} songs in your library" : "";
            SetMusicProgressVisuals(0, false);
        }

        private void UpdateMusicLoadingProgress(int loaded, int total)
        {
            var message = total > 0
                ? $"Adding the music in your folder {loaded} out of {total}"
                : "Finding the music in your folder...";
            var percent = total > 0 ? Math.Clamp((double)loaded / total * 100, 0, 100) : 0;

            MusicLibraryLoadingText.Text = message;
            LibraryStatusText.Text = message;
            SetMusicProgressVisuals(percent, total > 0);
        }

        private void SetMusicProgressVisuals(double percent, bool show)
        {
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            var text = show ? $"{percent:0}%" : "";
            MusicLibraryLoadingProgressBar.Value = percent;
            MusicLibraryLoadingProgressBar.Visibility = visibility;
            MusicLibraryLoadingPercentage.Text = text;
            MusicLibraryLoadingPercentage.Visibility = visibility;
            LibraryStatusProgressBar.Value = percent;
            LibraryStatusProgressBar.Visibility = visibility;
            LibraryStatusProgressText.Text = text;
            LibraryStatusProgressText.Visibility = visibility;
        }

        private async Task<List<StorageFile>> GetAllMusicFilesAsync(StorageFolder folder)
        {
            var supportedTypes = new[]
            {
                ".mp3", ".mp4", ".flac", ".wav", ".m4a", ".aac", ".wma",
                ".ogg", ".alac", ".aiff", ".opus"
            };

            try
            {
                var options = new QueryOptions(CommonFileQuery.DefaultQuery, supportedTypes)
                {
                    FolderDepth = FolderDepth.Deep
                };
                var query = folder.CreateFileQueryWithOptions(options);
                return (await query.GetFilesAsync()).ToList();
            }
            catch
            {
                // Some folder providers do not support deep queries. Walk those
                // folders manually so their music can still be loaded.
            }

            var files = new List<StorageFile>();
            var queue = new Queue<StorageFolder>();
            queue.Enqueue(folder);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                try
                {
                    var fileList = await current.GetFilesAsync();
                    files.AddRange(fileList.Where(f => supportedTypes.Contains(
                        Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase)));
                    var subfolders = await current.GetFoldersAsync();
                    foreach (var sub in subfolders)
                        queue.Enqueue(sub);
                }
                catch { }
            }
            return files;
        }

        private async Task<SongInfo> LoadSongInfoAsync(StorageFile file)
        {
            LogMusicScan($"Import: {file.Path}");
            var hasMetadataTitle = false;
            var song = new SongInfo
            {
                Title = file.DisplayName,
                Artist = "",
                Album = "",
                Duration = "",
                FilePath = file.Path
            };

            try
            {
                var basicProperties = await file.GetBasicPropertiesAsync();
                song.FileModified = basicProperties.DateModified;
                song.FileSize = basicProperties.Size;
            }
            catch { }

            try
            {
                using (var stream = await file.OpenStreamForReadAsync())
                {
                    var metadata = await Task.Run(() =>
                    {
                        using var tagFile = TagLib.File.Create(new StreamFileAbstraction(file.Name, stream, stream));
                        var tag = tagFile.Tag;
                        return (Title: tag.Title, Artist: string.IsNullOrWhiteSpace(tag.FirstPerformer)
                            ? tag.FirstAlbumArtist : tag.FirstPerformer,
                            Album: tag.Album, Duration: tagFile.Properties.Duration,
                            Artwork: tag.Pictures?.FirstOrDefault()?.Data?.Data);
                    });
                    song.Title = metadata.Title?.Trim() ?? "";
                    hasMetadataTitle = !string.IsNullOrWhiteSpace(metadata.Title);
                    song.Artist = metadata.Artist?.Trim() ?? "";
                    song.Album = metadata.Album ?? "";
                    song.Duration = metadata.Duration.ToString(@"mm\:ss");
                    var artworkBytes = metadata.Artwork;
                    if (artworkBytes != null && artworkBytes.Length > 0)
                        song.Artwork = await CreateArtworkAsync(artworkBytes);
                }
            }
            catch (Exception ex)
            {
                // The file is still playable even when its embedded tags or artwork
                // cannot be read, so keep it in the library using its file name.
                LogMusicScan($"Embedded metadata failed: {file.Path}; {ex.Message}");
            }

            // Windows can read metadata from formats/providers that TagLib cannot.
            if (string.IsNullOrWhiteSpace(song.Artist) || string.IsNullOrWhiteSpace(song.Title) ||
                song.Title == file.DisplayName)
            {
                try
                {
                    var properties = await file.Properties.GetMusicPropertiesAsync();
                    if ((string.IsNullOrWhiteSpace(song.Title) || song.Title == file.DisplayName) &&
                        !string.IsNullOrWhiteSpace(properties.Title))
                    {
                        song.Title = properties.Title.Trim();
                        hasMetadataTitle = true;
                    }
                    if (string.IsNullOrWhiteSpace(song.Artist))
                        song.Artist = (string.IsNullOrWhiteSpace(properties.Artist)
                            ? properties.AlbumArtist : properties.Artist)?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(song.Album))
                        song.Album = properties.Album ?? "";
                }
                catch (Exception ex)
                {
                    LogMusicScan($"Windows metadata failed: {file.Path}; {ex.Message}");
                }
            }

            var hasVerifiedMetadata = !string.IsNullOrWhiteSpace(song.Artist) &&
                hasMetadataTitle;

            // Untagged downloads commonly use "Artist - Song" as their filename.
            // Only fill missing information; embedded metadata remains authoritative.
            var fileTitle = file.DisplayName;
            var separator = fileTitle.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0 && separator + 3 < fileTitle.Length)
            {
                var inferredFileTitle = fileTitle;
                var unnumberedTitle = Zink.Services.MusicFilenameMetadataMatcher.RemoveCollisionSuffix(fileTitle);
                if (!hasMetadataTitle && unnumberedTitle != fileTitle && System.IO.File.Exists(Path.Combine(
                    Path.GetDirectoryName(file.Path), unnumberedTitle + Path.GetExtension(file.Name))))
                {
                    // A collision number distinguishes files, not song versions.
                    inferredFileTitle = unnumberedTitle;
                    LogMusicScan($"Numbered duplicate recognized: {file.Path}");
                }
                if (string.IsNullOrWhiteSpace(song.Artist))
                    song.Artist = fileTitle.Substring(0, separator).Trim();
                if (string.IsNullOrWhiteSpace(song.Title) || song.Title == fileTitle)
                    song.Title = inferredFileTitle.Substring(separator + 3).Trim();
            }

            if (string.IsNullOrWhiteSpace(song.Title))
                song.Title = fileTitle;

            // Local import never waits for the internet. Identified files can be
            // renamed immediately; other songs are enriched after the list loads.
            if (hasVerifiedMetadata)
                await RenameIdentifiedSongAsync(file, song);
            _pendingEnrichments.Enqueue((file, song, hasVerifiedMetadata, fileTitle, separator));
            return song;
        }

        private async Task EnrichSongAsync(StorageFile file, SongInfo song,
            bool hasVerifiedMetadata, string fileTitle, int separator)
        {
            if (string.IsNullOrWhiteSpace(song.Artist) || (!hasVerifiedMetadata &&
                Zink.Services.MusicFilenameMetadataMatcher.PrepareSearchTitle(fileTitle) != fileTitle))
            {
                try
                {
                    hasVerifiedMetadata = await ResolveFilenameMetadataAsync(song, fileTitle);
                }
                catch (Exception ex)
                {
                    LogMusicScan($"Filename lookup failed: {file.Path}; {ex.Message}");
                }
            }

            LogMusicScan($"Identified: {file.Name}; artist='{song.Artist}'; title='{song.Title}'; verified={hasVerifiedMetadata}; artwork={song.Artwork != null}.");
            // Normalize the source filename as part of scanning/importing the file,
            // before starting the optional artwork download.
            if (hasVerifiedMetadata)
                await RenameIdentifiedSongAsync(file, song);
            else if (!string.IsNullOrWhiteSpace(song.Artist) && separator > 0 &&
                Zink.Services.MusicFilenameMetadataMatcher.IsCanonicalFilename(file.Name,
                    $"{song.Artist} - {song.Title}{Path.GetExtension(file.Name)}"))
                LogMusicScan($"Rename unnecessary: {file.Path}; artist/title filename already present.");
            else
                LogMusicScan($"Rename skipped: {file.Path}; no verified artist/title metadata or exact online match.");

            if (song.Artwork == null && !string.IsNullOrWhiteSpace(song.Artist))
            {
                try
                {
                    await ResolveLibraryArtworkAsync(song);
                }
                catch (Exception ex)
                {
                    // An unavailable artwork service must not prevent importing music.
                    LogMusicScan($"Artwork lookup failed: {file.Path}; {ex.Message}");
                }
            }

        }

        private async Task ProcessPendingEnrichmentsAsync()
        {
            if (_enrichmentRunning || _musicLoadingDepth > 0)
                return;
            _enrichmentRunning = true;
            var enrichmentTimer = System.Diagnostics.Stopwatch.StartNew();
            var completed = 0;
            try
            {
                while (_pendingEnrichments.Count > 0)
                {
                    var item = _pendingEnrichments.Dequeue();
                    if (!Songs.Contains(item.Song))
                        continue;
                    LibraryStatusRing.Visibility = Visibility.Visible;
                    LibraryStatusRing.IsActive = true;
                    LibraryStatusText.Text = $"{Songs.Count} songs ready · Updating song info and artwork {completed + 1} of {completed + _pendingEnrichments.Count + 1}";
                    try
                    {
                        await EnrichSongAsync(item.File, item.Song, item.Verified, item.Title, item.Separator);
                        LibraryData.NotifySongUpdated(item.Song);
                        if (!string.IsNullOrWhiteSpace(_searchText))
                            UpdateSearchResults();
                    }
                    catch (Exception ex)
                    {
                        LogMusicScan($"Background song update failed: {item.Song.FilePath}; {ex.Message}");
                    }
                    completed++;
                    if (completed % 8 == 0)
                        await Task.Yield();
                }
            }
            finally
            {
                LogMusicScan($"Background updates finished: {completed} songs; {enrichmentTimer.Elapsed.TotalSeconds:0.00}s.");
                _enrichmentRunning = false;
                if (_musicLoadingDepth == 0)
                {
                    LibraryStatusRing.IsActive = false;
                    LibraryStatusRing.Visibility = Visibility.Collapsed;
                    LibraryStatusText.Text = $"{Songs.Count} songs in your library";
                }
            }
        }

        private static async Task<bool> HasUnchangedSongFileAsync(StorageFile file, SongInfo song)
        {
            if (song.FileModified == default)
                return false;
            try
            {
                var properties = await file.GetBasicPropertiesAsync();
                return properties.DateModified == song.FileModified && properties.Size == song.FileSize;
            }
            catch { return false; }
        }

        private static async Task RenameIdentifiedSongAsync(StorageFile file, SongInfo song)
        {
            static string SafePart(string value) => new string(value
                .Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');

            var artist = SafePart(song.Artist);
            var title = SafePart(song.Title);
            if (artist.Length == 0 || title.Length == 0)
                return;

            var name = $"{artist} - {title}{Path.GetExtension(file.Name)}";
            if (Zink.Services.MusicFilenameMetadataMatcher.IsCanonicalFilename(file.Name, name))
            {
                LogMusicScan($"Rename unnecessary: {file.Path}; already named correctly.");
                return;
            }

            try
            {
                // Never replace another song, and keep playback pointed at the renamed file.
                var previousName = file.Name;
                var previousPath = song.FilePath;
                LogMusicScan($"Rename attempt: {file.Path} → {name}");
                await file.RenameAsync(name, NameCollisionOption.GenerateUniqueName);
                song.FilePath = file.Path;
                LibraryData.NotifySongRenamed(previousPath, song.FilePath);
                LogMusicScan($"Rename succeeded: {previousName} → {file.Path}");
                Zink.Services.NotificationService.Instance.RecordActivity(
                    "Song renamed", $"{previousName} → {file.Name}\nFolder: {Path.GetDirectoryName(file.Path)}");
            }
            catch (Exception ex)
            {
                // Read-only folders, playing files and name collisions must not block imports.
                LogMusicScan($"Rename failed: {song.FilePath} → {name}; HRESULT=0x{ex.HResult:X8}; {ex.Message}");
                Zink.Services.NotificationService.Instance.RecordActivity(
                    "Song rename failed", $"{file.Name} → {name}\nFolder: {Path.GetDirectoryName(song.FilePath)}\n{ex.Message}");
            }
        }

        private static async Task<bool> ResolveFilenameMetadataAsync(SongInfo song, string filenameTitle)
        {
            var searchTitle = Zink.Services.MusicFilenameMetadataMatcher.PrepareSearchTitle(filenameTitle);
            if (string.IsNullOrWhiteSpace(searchTitle))
                return false;
            using var document = JsonDocument.Parse(await
                Zink.Services.MusicLibraryMetadataLookup.Instance.SearchAsync(searchTitle, LogMusicScan));
            if (!document.RootElement.TryGetProperty("results", out var results))
                return false;

            LogMusicScan($"Filename lookup: '{filenameTitle}'; cleaned search: '{searchTitle}'; results: {results.GetArrayLength()}.");
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("artistName", out var artistProperty) ||
                    !result.TryGetProperty("trackName", out var titleProperty))
                    continue;

                var artist = artistProperty.GetString();
                var title = titleProperty.GetString();
                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
                    continue;

                LogMusicScan($"Lookup candidate: artist='{artist}'; title='{title}'.");

                // A title alone is ambiguous. Require both names to match the
                // filename before assigning metadata from an online result.
                if (!Zink.Services.MusicFilenameMetadataMatcher.Matches(searchTitle, artist, title))
                    continue;

                song.Artist = artist;
                song.Title = title;
                if (string.IsNullOrWhiteSpace(song.Album) &&
                    result.TryGetProperty("collectionName", out var albumProperty))
                    song.Album = albumProperty.GetString() ?? "";
                if (song.Artwork == null && result.TryGetProperty("artworkUrl100", out var artworkProperty) &&
                    Uri.TryCreate(artworkProperty.GetString()?.Replace("100x100", "300x300"),
                        UriKind.Absolute, out var artworkUri))
                    song.Artwork = new BitmapImage(artworkUri) { DecodePixelWidth = 440 };
                return true;
            }
            return false;
        }

        private static async Task ResolveLibraryArtworkAsync(SongInfo song)
        {
            var term = $"{song.Artist} {song.Title}";
            using var document = JsonDocument.Parse(await
                Zink.Services.MusicLibraryMetadataLookup.Instance.SearchAsync(term, LogMusicScan));
            if (!document.RootElement.TryGetProperty("results", out var results))
                return;

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("artistName", out var artist) ||
                    !result.TryGetProperty("trackName", out var title) ||
                    !Zink.Services.MusicFilenameMetadataMatcher.Matches(
                        Zink.Services.MusicFilenameMetadataMatcher.PrepareSearchTitle(term),
                        artist.GetString() ?? "", title.GetString() ?? ""))
                    continue;
                if (result.TryGetProperty("artworkUrl100", out var artwork) &&
                    Uri.TryCreate(artwork.GetString()?.Replace("100x100", "300x300"),
                        UriKind.Absolute, out var uri))
                    song.Artwork = new BitmapImage(uri) { DecodePixelWidth = 440 };
                return;
            }
        }

        private static async Task<BitmapImage> CreateArtworkAsync(byte[] artworkBytes)
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(artworkBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 440 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }

        private void StartWatcher()
        {
            StopWatcher();
            if (_pickedFolder == null)
                return;

            _watcherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _watcherTimer.Tick += WatcherTimer_Tick;
            _watcherTimer.Start();
        }

        private async void WatcherTimer_Tick(object sender, object e)
        {
            if (_checkingNewSongs || _musicLoadingDepth > 0 || _pickedFolder == null)
                return;

            _checkingNewSongs = true;
            var folder = _pickedFolder;
            try
            {
                var files = await GetAllMusicFilesAsync(folder);
                if (_watcherTimer == null || folder != _pickedFolder || _musicLoadingDepth > 0)
                    return;

                var knownPaths = new HashSet<string>(Songs.Select(s => s.FilePath),
                    StringComparer.OrdinalIgnoreCase);
                var newFileCount = files.Count(f => !knownPaths.Contains(f.Path));
                if (newFileCount > 0)
                    LogMusicScan($"Incremental scan: {folder.Path}; new files: {newFileCount}.");
                foreach (var file in files.Where(f => !knownPaths.Contains(f.Path)))
                {
                    if (_watcherTimer == null || folder != _pickedFolder)
                        break;
                    var song = await LoadSongInfoAsync(file);
                    if (_watcherTimer == null || folder != _pickedFolder)
                        break;
                    Songs.Add(song);
                    knownPaths.Add(song.FilePath);
                }
                if (_musicLoadingDepth == 0)
                    LibraryStatusText.Text = $"{Songs.Count} songs in your library";
            }
            catch
            {
                // A temporarily unavailable folder can be retried on the next tick.
            }
            finally
            {
                _checkingNewSongs = false;
                _ = ProcessPendingEnrichmentsAsync();
            }
        }

        private void StopWatcher()
        {
            if (_watcherTimer != null)
            {
                _watcherTimer.Stop();
                _watcherTimer = null;
            }
        }

        private async void PlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowPlayAllOptionsAsync();
        }

        private void PlaySongButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is SongInfo song)
                PlaySong(song);
        }

        private void SongArtwork_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is Border artwork && artwork.DataContext is SongInfo song)
                PlaySong(song);
        }

        private void SongArtwork_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Border artwork)
            {
                artwork.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    global::Windows.UI.Color.FromArgb(255, 17, 216, 232));
                artwork.BorderThickness = new Thickness(3);
            }
        }

        private void SongArtwork_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Border artwork)
            {
                artwork.BorderBrush = null;
                artwork.BorderThickness = new Thickness(0);
            }
        }

        private static void PlaySong(SongInfo song)
        {
            LibraryData.PlaySongList(new List<SongInfo> { song });

            var main = (MainWindow)App.MainWindow;
            main.ContentFrame.Navigate(typeof(MusicPlayerPage), "autoplay");

            var playerItem = main.SidebarNav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => (string)i.Tag == "MusicPlayer");
            if (playerItem != null)
                main.SidebarNav.SelectedItem = playerItem;
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public class SongInfo : INotifyPropertyChanged
        {
            internal DateTimeOffset FileModified { get; set; }
            internal ulong FileSize { get; set; }
            private string _title, _artist, _album, _duration, _filePath;
            private BitmapImage _artwork;
            public event PropertyChangedEventHandler PropertyChanged;
            public string Title { get => _title; set => SetValue(ref _title, value, nameof(Title)); }
            public string Artist { get => _artist; set => SetValue(ref _artist, value, nameof(Artist)); }
            public string Album { get => _album; set => SetValue(ref _album, value, nameof(Album)); }
            public string Duration { get => _duration; set => SetValue(ref _duration, value, nameof(Duration)); }
            public string FilePath { get => _filePath; set => SetValue(ref _filePath, value, nameof(FilePath)); }
            public BitmapImage Artwork { get => _artwork; set => SetValue(ref _artwork, value, nameof(Artwork)); }

            private void SetValue<T>(ref T field, T value, string property)
            {
                if (EqualityComparer<T>.Default.Equals(field, value)) return;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
        }
    }

    public class StreamFileAbstraction : TagLib.File.IFileAbstraction
    {
        public StreamFileAbstraction(string name, Stream readStream, Stream writeStream)
        {
            Name = name;
            _readStream = readStream;
            _writeStream = writeStream;
        }

        public string Name { get; }

        private readonly Stream _readStream;
        private readonly Stream _writeStream;

        public Stream ReadStream => _readStream;
        public Stream WriteStream => _writeStream;

        public void CloseStream(Stream stream)
        {
            // Handled externally
        }
    }

    public static class LibraryData
    {
        public static List<MusicLibraryPage.SongInfo> CurrentQueue { get; private set; } = new();

        public static event EventHandler<List<MusicLibraryPage.SongInfo>> QueueOverrideRequested;
        public static event Action<string, string> SongRenamed;
        public static event Action<MusicLibraryPage.SongInfo> SongUpdated;

        public static void NotifySongUpdated(MusicLibraryPage.SongInfo song) => SongUpdated?.Invoke(song);

        public static void NotifySongRenamed(string previousPath, string newPath) =>
            SongRenamed?.Invoke(previousPath, newPath);

        public static void PlaySongList(List<MusicLibraryPage.SongInfo> songs)
        {
            CurrentQueue = songs;
            QueueOverrideRequested?.Invoke(null, songs);
        }

        public static bool HasSongs()
        {
            return CurrentQueue?.Any() == true;
        }
    }
}

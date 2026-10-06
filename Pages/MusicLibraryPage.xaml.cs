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

namespace Zink.Pages
{
    public sealed partial class MusicLibraryPage : Page, INotifyPropertyChanged
    {
        public ObservableCollection<SongInfo> Songs { get; set; } = new();

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

        // Prevents multiple prompts in the same page instance
        private bool _emptyLibraryPromptShown;

        // NEW: if we need to show the dialog but XamlRoot isn't ready yet, show it after Loaded
        private bool _pendingShowEmptyDialog;

        public event PropertyChangedEventHandler PropertyChanged;

        public MusicLibraryPage()
        {
            this.InitializeComponent();
            this.DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Required;

            // NEW: ensure we can show the dialog after the page is actually in the tree
            this.Loaded += MusicLibraryPage_Loaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (_pickedFolder != null)
            {
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
            if (showLoading)
                BeginMusicLoading();
            try
            {
                var files = await GetAllMusicFilesAsync(folder);
                Songs.Clear();
                if (showLoading)
                    UpdateMusicLoadingProgress(0, files.Count);

                var loadedSongs = 0;
                foreach (var file in files)
                {
                    var song = await LoadSongInfoAsync(file);
                    if (song != null)
                        Songs.Add(song);

                    loadedSongs++;
                    if (showLoading)
                        UpdateMusicLoadingProgress(loadedSongs, files.Count);
                }
            }
            finally
            {
                if (showLoading)
                    EndMusicLoading();
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
                using (var stream = await file.OpenStreamForReadAsync())
                {
                    using var tagFile = TagLib.File.Create(new StreamFileAbstraction(file.Name, stream, stream));
                    var tag = tagFile.Tag;
                    var props = tagFile.Properties;

                    song.Title = string.IsNullOrEmpty(tag.Title) ? file.DisplayName : tag.Title;
                    song.Artist = tag.FirstPerformer ?? "";
                    song.Album = tag.Album ?? "";
                    song.Duration = props.Duration.ToString(@"mm\:ss");

                    var picture = tag.Pictures?.FirstOrDefault();
                    var artworkBytes = picture?.Data?.Data;
                    if (artworkBytes != null && artworkBytes.Length > 0)
                        song.Artwork = await CreateArtworkAsync(artworkBytes);
                }
            }
            catch
            {
                // The file is still playable even when its embedded tags or artwork
                // cannot be read, so keep it in the library using its file name.
            }

            return song;
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
        }

        private void StopWatcher()
        {
            if (_watcherTimer != null)
            {
                _watcherTimer.Stop();
                _watcherTimer = null;
            }
        }

        private void PlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (Songs.Count == 0) return;

            LibraryData.PlaySongList(Songs.ToList());

            var main = (MainWindow)App.MainWindow;
            main.ContentFrame.Navigate(typeof(MusicPlayerPage), "autoplay");

            var playerItem = main.SidebarNav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => (string)i.Tag == "MusicPlayer");
            if (playerItem != null)
                main.SidebarNav.SelectedItem = playerItem;
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

        public class SongInfo
        {
            public string Title { get; set; }
            public string Artist { get; set; }
            public string Album { get; set; }
            public string Duration { get; set; }
            public string FilePath { get; set; }
            public BitmapImage Artwork { get; set; }
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

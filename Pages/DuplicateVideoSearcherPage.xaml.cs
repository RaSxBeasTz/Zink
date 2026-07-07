using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.System;

namespace Zink.Pages
{
    public sealed partial class DuplicateVideoSearcherPage : Page
    {
        private const string LibraryFileName = "video_library.json";

        public ObservableCollection<DuplicateVideoGroup> DuplicateGroups { get; } = new();

        private DuplicateVideoItem? _pendingDeleteItem;
        private bool _zinkDialogIsDeleteConfirmation;

        public DuplicateVideoSearcherPage()
        {
            InitializeComponent();
            Loaded += DuplicateVideoSearcherPage_Loaded;
        }

        private async void DuplicateVideoSearcherPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadDuplicatesAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadDuplicatesAsync();
        }

        private async Task LoadDuplicatesAsync()
        {
            DuplicateGroups.Clear();
            ShowStatus("Searching your video library for duplicates...", InfoBarSeverity.Informational, true);

            try
            {
                var candidates = await LoadVideoCandidatesAsync();
                var groups = candidates
                    .GroupBy(v => $"{v.NormalizedName}|{v.SizeKey}", StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => new DuplicateVideoGroup
                    {
                        Title = g.First().DisplayName,
                        Summary = $"{g.Count()} copies found - {FormatBytes(g.First().Size)}",
                        Items = new ObservableCollection<DuplicateVideoItem>(g.OrderBy(v => v.FullPath))
                    })
                    .OrderBy(g => g.Title)
                    .ToList();

                foreach (var group in groups)
                    DuplicateGroups.Add(group);

                if (DuplicateGroups.Count == 0)
                    ShowStatus("No duplicate videos found in your Video Library.", InfoBarSeverity.Success, true);
                else
                    ShowStatus($"{DuplicateGroups.Count} duplicate video group{(DuplicateGroups.Count == 1 ? "" : "s")} found.", InfoBarSeverity.Warning, true);
            }
            catch (Exception ex)
            {
                ShowStatus("Could not search for duplicate videos: " + ex.Message, InfoBarSeverity.Error, true);
            }
        }

        private static async Task<List<DuplicateVideoItem>> LoadVideoCandidatesAsync()
        {
            var libraryFile = await ApplicationData.Current.LocalFolder.TryGetItemAsync(LibraryFileName) as StorageFile;
            if (libraryFile == null)
                return new List<DuplicateVideoItem>();

            var json = await FileIO.ReadTextAsync(libraryFile);
            if (string.IsNullOrWhiteSpace(json))
                return new List<DuplicateVideoItem>();

            var save = JsonSerializer.Deserialize<VideoLibrarySaveDto>(json);
            if (save?.Items == null || save.Items.Count == 0)
                return new List<DuplicateVideoItem>();

            var folderCache = new Dictionary<string, StorageFolder>(StringComparer.OrdinalIgnoreCase);
            var videos = new List<DuplicateVideoItem>();

            foreach (var entry in save.Items)
            {
                if (entry == null ||
                    string.IsNullOrWhiteSpace(entry.FolderToken) ||
                    string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    continue;
                }

                try
                {
                    if (!folderCache.TryGetValue(entry.FolderToken, out var folder))
                    {
                        if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(entry.FolderToken))
                            continue;

                        folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(entry.FolderToken);
                        folderCache[entry.FolderToken] = folder;
                    }

                    var fullPath = Path.Combine(folder.Path, entry.RelativePath);
                    if (!File.Exists(fullPath))
                        continue;

                    var file = new FileInfo(fullPath);
                    var displayName = string.IsNullOrWhiteSpace(entry.Name)
                        ? Path.GetFileNameWithoutExtension(fullPath)
                        : entry.Name;

                    videos.Add(new DuplicateVideoItem
                    {
                        DisplayName = displayName,
                        FileName = file.Name,
                        FullPath = fullPath,
                        FolderPath = file.DirectoryName ?? folder.Path,
                        Size = file.Length,
                        ModifiedAt = file.LastWriteTime,
                        NormalizedName = NormalizeVideoName(displayName),
                    });
                }
                catch { }
            }

            return videos;
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not DuplicateVideoItem item)
                return;

            try
            {
                if (!File.Exists(item.FullPath))
                {
                    ShowStatus("That video no longer exists. Refreshing the duplicate search.", InfoBarSeverity.Warning, true);
                    await LoadDuplicatesAsync();
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
                App.MainWindow.MainFrame.Navigate(typeof(VideoPlayerPage), file);
            }
            catch (Exception ex)
            {
                ShowStatus("Could not play that video: " + ex.Message, InfoBarSeverity.Error, true);
            }
        }

        private async void OpenFileLocationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not DuplicateVideoItem item)
                return;

            try
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(item.FolderPath);
                var options = new FolderLauncherOptions();

                try
                {
                    if (File.Exists(item.FullPath))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
                        options.ItemsToSelect.Add(file);
                    }
                }
                catch { }

                await Launcher.LaunchFolderAsync(folder, options);
            }
            catch (Exception ex)
            {
                ShowStatus("Could not open that file location: " + ex.Message, InfoBarSeverity.Error, true);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not DuplicateVideoItem item)
                return;

            _pendingDeleteItem = item;
            ShowZinkDialog(
                "Delete this video?",
                $"{item.FileName}\n\nThis will remove the file from your computer.",
                "Delete",
                true);
        }

        private void ZinkDialogCancelButton_Click(object sender, RoutedEventArgs e)
        {
            HideZinkDialog();
        }

        private async void ZinkDialogPrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_zinkDialogIsDeleteConfirmation)
            {
                HideZinkDialog();
                return;
            }

            var item = _pendingDeleteItem;
            if (item == null)
            {
                HideZinkDialog();
                return;
            }

            try
            {
                if (!File.Exists(item.FullPath))
                {
                    HideZinkDialog();
                    ShowStatus("That video was already gone. Refreshing the duplicate search.", InfoBarSeverity.Warning, true);
                    await LoadDuplicatesAsync();
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
                await file.DeleteAsync(StorageDeleteOption.Default);

                _pendingDeleteItem = null;
                await LoadDuplicatesAsync();
                ShowZinkDialog(
                    "Video deleted",
                    $"{item.FileName}\n\nThe file has been deleted from your computer.",
                    "OK",
                    false);
            }
            catch (Exception ex)
            {
                HideZinkDialog();
                ShowStatus("Could not delete that video: " + ex.Message, InfoBarSeverity.Error, true);
            }
        }

        private void ShowZinkDialog(string title, string message, string primaryButtonText, bool isDeleteConfirmation)
        {
            _zinkDialogIsDeleteConfirmation = isDeleteConfirmation;

            ZinkDialogTitle.Text = title;
            ZinkDialogMessage.Text = message;
            ZinkDialogPrimaryButton.Content = primaryButtonText;
            ZinkDialogCancelButton.Visibility = isDeleteConfirmation ? Visibility.Visible : Visibility.Collapsed;
            ZinkDialogIcon.Glyph = isDeleteConfirmation ? "\uE74D" : "\uE73E";
            ZinkDialogOverlay.Visibility = Visibility.Visible;
        }

        private void HideZinkDialog()
        {
            ZinkDialogOverlay.Visibility = Visibility.Collapsed;
            _pendingDeleteItem = null;
            _zinkDialogIsDeleteConfirmation = false;
        }

        private void ShowStatus(string message, InfoBarSeverity severity, bool isOpen)
        {
            StatusBar.Message = message;
            StatusBar.Severity = severity;
            StatusBar.IsOpen = isOpen;
        }

        private static string NormalizeVideoName(string value)
        {
            var name = Path.GetFileNameWithoutExtension(value ?? string.Empty).ToLowerInvariant();
            name = Regex.Replace(name, @"\s*\((copy|\d+)\)\s*$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s*-\s*copy\s*$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s+", " ").Trim();
            return name;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }

        private sealed class VideoLibrarySaveDto
        {
            public List<string>? FolderTokens { get; set; }
            public List<VideoLibraryEntryDto>? Items { get; set; }
        }

        private sealed class VideoLibraryEntryDto
        {
            public string? Name { get; set; }
            public string? FileName { get; set; }
            public string? FolderToken { get; set; }
            public string? RelativePath { get; set; }
        }
    }

    public sealed class DuplicateVideoGroup
    {
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public ObservableCollection<DuplicateVideoItem> Items { get; set; } = new();
    }

    public sealed class DuplicateVideoItem
    {
        public string DisplayName { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public long Size { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string NormalizedName { get; set; } = "";
        public string SizeKey => Size.ToString();
        public string Details => $"{FormatBytes(Size)} - Modified {ModifiedAt:g} - {FullPath}";

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }
}

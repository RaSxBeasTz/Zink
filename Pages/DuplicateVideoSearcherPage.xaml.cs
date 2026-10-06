using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Search;
using Windows.System;

namespace Zink.Pages
{
    public sealed partial class DuplicateVideoSearcherPage : Page
    {
        private const string LibraryFileName = "video_library.json";

        public ObservableCollection<DuplicateVideoGroup> DuplicateGroups { get; } = new();

        private DuplicateVideoItem? _pendingDeleteItem;
        private bool _zinkDialogIsDeleteConfirmation;
        private bool _zinkDialogIsBulkDeleteConfirmation;

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
                await CalculateContentHashesAsync(candidates);

                var groups = candidates
                    .Where(v => !string.IsNullOrEmpty(v.ContentHash))
                    .GroupBy(v => $"{v.SizeKey}|{v.ContentHash}", StringComparer.Ordinal)
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
            if (save == null)
                return new List<DuplicateVideoItem>();

            var videos = new List<DuplicateVideoItem>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folderTokens = (save.FolderTokens ?? new List<string>())
                .Concat(save.Items?.Select(item => item.FolderToken) ?? Enumerable.Empty<string?>())
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Select(token => token!)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var folderToken in folderTokens)
            {
                if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(folderToken))
                    continue;

                try
                {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(folderToken);
                    var queryOptions = new QueryOptions(CommonFileQuery.DefaultQuery, new[]
                    {
                        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv",
                        ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".3g2", ".ogv",
                        ".vob", ".divx", ".asf"
                    })
                    {
                        FolderDepth = FolderDepth.Deep
                    };

                    var files = await folder.CreateFileQueryWithOptions(queryOptions).GetFilesAsync();
                    foreach (var storageFile in files)
                    {
                        if (!addedPaths.Add(storageFile.Path))
                            continue;

                        var file = new FileInfo(storageFile.Path);
                        var displayName = storageFile.DisplayName;

                        videos.Add(new DuplicateVideoItem
                        {
                            DisplayName = displayName,
                            FileName = file.Name,
                            FullPath = storageFile.Path,
                            FolderPath = file.DirectoryName ?? folder.Path,
                            Size = file.Length,
                            ModifiedAt = file.LastWriteTime,
                            NormalizedName = NormalizeVideoName(displayName),
                        });
                    }
                }
                catch { }
            }

            return videos;
        }

        private static async Task CalculateContentHashesAsync(List<DuplicateVideoItem> videos)
        {
            // Identical files must have the same size, so files with a unique size can
            // be skipped. The remaining files are fully hashed to compare their actual
            // contents, regardless of their names or locations.
            var possibleDuplicates = videos
                .GroupBy(video => video.Size)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group);

            foreach (var video in possibleDuplicates)
            {
                try
                {
                    await using var stream = new FileStream(
                        video.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var sha256 = SHA256.Create();
                    var hash = await sha256.ComputeHashAsync(stream);
                    video.ContentHash = Convert.ToHexString(hash);
                }
                catch
                {
                    // A file that disappears or cannot be read during the scan is
                    // omitted instead of preventing the rest of the library scan.
                    video.ContentHash = string.Empty;
                }
            }
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

        private void DeleteAllDuplicatesButton_Click(object sender, RoutedEventArgs e)
        {
            var drives = DuplicateGroups
                .SelectMany(group => group.Items)
                .Select(item => Path.GetPathRoot(item.FullPath) ?? string.Empty)
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(root => root)
                .Select(root => new DuplicateDriveOption { RootPath = root, DisplayName = root })
                .ToList();

            var eligibleDrives = drives
                .Where(drive => DuplicateGroups.Any(group =>
                    group.Items.Any(item => IsOnDrive(item, drive.RootPath)) &&
                    group.Items.Any(item => !IsOnDrive(item, drive.RootPath))))
                .ToList();

            if (eligibleDrives.Count < 2)
            {
                ShowStatus("Duplicates must be on at least two different drives before they can be deleted by drive.", InfoBarSeverity.Warning, true);
                return;
            }

            _pendingDeleteItem = null;
            _zinkDialogIsDeleteConfirmation = true;
            _zinkDialogIsBulkDeleteConfirmation = true;
            BulkDriveComboBox.ItemsSource = eligibleDrives;
            BulkDriveComboBox.SelectedIndex = -1;
            BulkDriveComboBox.Visibility = Visibility.Visible;
            ZinkDialogTitle.Text = "Delete all duplicates from one drive?";
            ZinkDialogMessage.Text = "Choose the drive whose duplicate film copies should be deleted. A copy on the other drive will be kept for every film.";
            ZinkDialogPrimaryButton.Content = "Delete all";
            ZinkDialogCancelButton.Visibility = Visibility.Visible;
            ZinkDialogIcon.Glyph = "\uE74D";
            ZinkDialogOverlay.Visibility = Visibility.Visible;
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

            if (_zinkDialogIsBulkDeleteConfirmation)
            {
                if (BulkDriveComboBox.SelectedItem is not DuplicateDriveOption selectedDrive)
                {
                    ShowStatus("Select the drive you want the duplicate copies deleted from.", InfoBarSeverity.Warning, true);
                    return;
                }

                await DeleteDuplicatesFromDriveAsync(selectedDrive.RootPath);
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

        private async Task DeleteDuplicatesFromDriveAsync(string driveRoot)
        {
            var filesToDelete = DuplicateGroups
                .Where(group =>
                    group.Items.Any(item => IsOnDrive(item, driveRoot)) &&
                    group.Items.Any(item => !IsOnDrive(item, driveRoot)))
                .SelectMany(group => group.Items.Where(item => IsOnDrive(item, driveRoot)))
                .ToList();

            int deletedCount = 0;
            int failedCount = 0;

            foreach (var item in filesToDelete)
            {
                try
                {
                    if (!File.Exists(item.FullPath))
                        continue;

                    var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
                    await file.DeleteAsync(StorageDeleteOption.Default);
                    deletedCount++;
                }
                catch
                {
                    failedCount++;
                }
            }

            HideZinkDialog();
            await LoadDuplicatesAsync();

            var result = $"Deleted {deletedCount} duplicate film cop{(deletedCount == 1 ? "y" : "ies")} from {driveRoot}. Copies on the other drive were kept.";
            if (failedCount > 0)
                result += $"\n\n{failedCount} file{(failedCount == 1 ? "" : "s")} could not be deleted.";

            ShowZinkDialog("Duplicate films deleted", result, "OK", false);
        }

        private static bool IsOnDrive(DuplicateVideoItem item, string driveRoot)
        {
            return string.Equals(Path.GetPathRoot(item.FullPath), driveRoot, StringComparison.OrdinalIgnoreCase);
        }

        private void ShowZinkDialog(string title, string message, string primaryButtonText, bool isDeleteConfirmation)
        {
            _zinkDialogIsDeleteConfirmation = isDeleteConfirmation;
            _zinkDialogIsBulkDeleteConfirmation = false;
            BulkDriveComboBox.Visibility = Visibility.Collapsed;
            BulkDriveComboBox.ItemsSource = null;

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
            _zinkDialogIsBulkDeleteConfirmation = false;
            BulkDriveComboBox.Visibility = Visibility.Collapsed;
            BulkDriveComboBox.ItemsSource = null;
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
        public string ContentHash { get; set; } = "";
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

    public sealed class DuplicateDriveOption
    {
        public string RootPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }
}

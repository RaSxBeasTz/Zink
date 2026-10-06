using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Zink.Services;
using Zink.Services.Capture;

namespace Zink.Pages
{
    public sealed partial class ToolsPage : Page
    {
        private readonly ZinkAreaCaptureService _capture = ZinkAreaCaptureService.Instance;
        private int _videoWidth = 3840;
        private int _videoHeight = 2160;
        private int _pictureWidth = 3840;
        private int _pictureHeight = 2160;
        private readonly bool[] _fileMoverSlotBusy = new bool[4];
        private readonly HashSet<string> _fileMoverReservedPaths = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".ts", ".webm", ".wmv"
        };

        public ToolsPage()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshRecordingState();
            _capture.RecordingStateChanged += Capture_RecordingStateChanged;
            _capture.StatusChanged += Capture_StatusChanged;
            Unloaded += ToolsPage_Unloaded;
        }

        private async void ScreenshotAreaButton_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Drag over the area to screenshot…";
            var region = await AreaSelectionWindow.SelectRegionAsync();
            if (region is null)
            {
                StatusText.Text = "Screenshot cancelled.";
                return;
            }

            await _capture.CaptureScreenshotAsync(region.Value, _pictureWidth, _pictureHeight);
        }

        private async void RecordAreaButton_Click(object sender, RoutedEventArgs e)
        {
            if (_capture.IsRecording)
            {
                StatusText.Text = "A recording is already running.";
                return;
            }

            StatusText.Text = "Drag over the area to record…";
            var region = await AreaSelectionWindow.SelectRegionAsync();
            if (region is null)
            {
                StatusText.Text = "Recording cancelled.";
                return;
            }

            await _capture.StartRecordingAsync(
                region.Value,
                _videoWidth,
                _videoHeight,
                SystemAudioToggle.IsOn);
        }

        private async void StopRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            await _capture.StopRecordingAsync();
        }

        private async void OpenCapturesButton_Click(object sender, RoutedEventArgs e)
        {
            await _capture.OpenCapturesFolderAsync();
        }

        private async void FileMoverButton_Click(object sender, RoutedEventArgs e)
        {
            var slotIndex = sender is Button { Tag: string tag } && int.TryParse(tag, out var parsedSlot)
                ? parsedSlot
                : 0;
            var (slotButton, slotStatus, slotProgress, slotFiles) = GetFileMoverSlotControls(slotIndex);
            if (_fileMoverSlotBusy[slotIndex])
                return;

            _fileMoverSlotBusy[slotIndex] = true;
            slotButton.IsEnabled = false;
            slotButton.Content = "Setting up…";
            var reservedByThisSlot = Array.Empty<string>();
            try
            {
                slotStatus.Text = "Reading completed torrents from µTorrent…";
                var completedTorrents = UTorrentResumeService.ReadCompleted()
                    .Where(torrent =>
                    {
                        var files = UTorrentResumeService.FindVideoFiles(torrent, VideoExtensions);
                        return files.Count > 0 && files.All(path => !_fileMoverReservedPaths.Contains(path));
                    })
                    .ToArray();
                if (completedTorrents.Length == 0)
                {
                    slotStatus.Text = "No completed torrents were found in µTorrent.";
                    return;
                }

                var torrentList = new ListView
                {
                    ItemsSource = completedTorrents
                        .OrderByDescending(item => item.ReleaseYear)
                        .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray(),
                    SelectionMode = ListViewSelectionMode.Multiple,
                    MaxHeight = 430,
                    MinWidth = 620
                };
                var sortByDate = new ComboBox
                {
                    Header = "Sort by movie release year",
                    MinWidth = 250,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                sortByDate.Items.Add("Newest release year first");
                sortByDate.Items.Add("Oldest release year first");
                sortByDate.SelectedIndex = 0;
                sortByDate.SelectionChanged += (_, _) =>
                {
                    var selected = torrentList.SelectedItems.Cast<UTorrentItem>().ToArray();
                    torrentList.ItemsSource = sortByDate.SelectedIndex == 1
                        ? completedTorrents.OrderBy(item => item.ReleaseYear == 0 ? int.MaxValue : item.ReleaseYear)
                            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                            .ToArray()
                        : completedTorrents.OrderByDescending(item => item.ReleaseYear)
                            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                            .ToArray();
                    foreach (var torrent in selected)
                        torrentList.SelectedItems.Add(torrent);
                };
                var shownCountText = new TextBlock
                {
                    Text = $"{completedTorrents.Length:N0} completed torrent(s) shown",
                    Opacity = 0.72
                };
                var selectedCountText = new TextBlock
                {
                    Text = "0 selected",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        global::Windows.UI.Color.FromArgb(255, 56, 255, 102))
                };
                var countRow = new Grid { ColumnSpacing = 12 };
                countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                countRow.Children.Add(shownCountText);
                Grid.SetColumn(selectedCountText, 1);
                countRow.Children.Add(selectedCountText);

                var torrentContent = new StackPanel { Spacing = 10 };
                torrentContent.Children.Add(new TextBlock
                {
                    Text = "Seeding",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                torrentContent.Children.Add(new TextBlock
                {
                    Text = "Completed torrents available from µTorrent",
                    Opacity = 0.72
                });
                torrentContent.Children.Add(sortByDate);
                torrentContent.Children.Add(countRow);
                torrentContent.Children.Add(torrentList);

                var selectDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Select completed µTorrent downloads",
                    Content = torrentContent,
                    PrimaryButtonText = "Choose destination",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    IsPrimaryButtonEnabled = false
                };
                torrentList.SelectionChanged += (_, _) =>
                {
                    selectDialog.IsPrimaryButtonEnabled = torrentList.SelectedItems.Count > 0;
                    selectedCountText.Text = $"{torrentList.SelectedItems.Count:N0} selected";
                };
                if (await selectDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    slotStatus.Text = "No torrents selected.";
                    return;
                }

                var selectedTorrents = torrentList.SelectedItems.Cast<UTorrentItem>().ToArray();
                var sourcePathsByTorrent = selectedTorrents.ToDictionary(
                    torrent => torrent,
                    torrent => UTorrentResumeService.FindVideoFiles(torrent, VideoExtensions));
                var sourcePaths = sourcePathsByTorrent
                    .SelectMany(pair => pair.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (sourcePaths.Length == 0)
                {
                    slotStatus.Text = "No supported video files were found for the selected torrents.";
                    return;
                }

                reservedByThisSlot = sourcePaths;
                foreach (var sourcePath in reservedByThisSlot)
                    _fileMoverReservedPaths.Add(sourcePath);

                slotFiles.Text = string.Join("\n", sourcePaths.Select(path => "• " + Path.GetFileName(path)));
                slotStatus.Text = $"{sourcePaths.Length:N0} file(s) selected. Choose a destination.";

                var folderPicker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.VideosLibrary
                };
                InitializeWithWindow.Initialize(folderPicker, WindowNative.GetWindowHandle(App.MainWindow));
                folderPicker.FileTypeFilter.Add("*");
                var destination = await folderPicker.PickSingleFolderAsync();
                if (destination is null)
                {
                    slotStatus.Text = "Move cancelled before any files were changed.";
                    return;
                }

                var firstName = Path.GetFileName(sourcePaths[0]);
                var firstNewName = BuildCleanMovieFileName(firstName);
                var confirmation = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Move completed torrent files?",
                    Content = $"{sourcePaths.Length:N0} video file(s) from {selectedTorrents.Length:N0} selected torrent(s) will be moved to:\n{destination.Path}\n\nExample rename:\n{firstName}\n→ {firstNewName}\n\nExisting files will never be overwritten. Moving a file will stop µTorrent from seeding that file from its old location.",
                    PrimaryButtonText = "Move files",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                {
                    slotStatus.Text = "Move cancelled before any files were changed.";
                    return;
                }

                slotButton.Content = "Moving…";
                slotStatus.Text = $"0% • 0 of {sourcePaths.Length:N0} files • {destination.Path}";
                FileMoverStatusText.Text = $"Move {slotIndex + 1} is running. Other slots are available.";

                var moved = 0;
                var failed = new List<string>();
                var movedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < sourcePaths.Length; index++)
                {
                    var sourcePath = sourcePaths[index];
                    slotFiles.Text = $"Moving now: {Path.GetFileName(sourcePath)}\n\n" +
                                     string.Join("\n", sourcePaths.Select(path => "• " + Path.GetFileName(path)));
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
                        var targetName = await GetAvailableFileNameAsync(destination, BuildCleanMovieFileName(file.Name));
                        void UpdateFileProgress(double percentage)
                        {
                            var overallPercentage = ((index + percentage / 100d) / sourcePaths.Length) * 100d;
                            slotProgress.Value = overallPercentage;
                            slotStatus.Text = $"{overallPercentage:0}% • file {index + 1:N0} of {sourcePaths.Length:N0} • {destination.Path}";
                        }
                        await MoveFileWithProgressAsync(file, destination, targetName, UpdateFileProgress);
                        moved++;
                        movedSourcePaths.Add(sourcePath);
                        FileMoverStatusText.Text = $"Move {slotIndex + 1}: {moved:N0} of {sourcePaths.Length:N0} file(s) moved.";
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{Path.GetFileName(sourcePath)}: {ex.Message}");
                    }

                }

                var completelyMovedTorrents = sourcePathsByTorrent
                    .Where(pair => pair.Value.Count > 0 &&
                                   pair.Value.All(path => movedSourcePaths.Contains(path)))
                    .Select(pair => pair.Key)
                    .ToArray();
                string? historyWarning = null;
                try
                {
                    UTorrentResumeService.MarkMoved(completelyMovedTorrents);
                }
                catch (Exception ex)
                {
                    historyWarning = $"The files moved, but their moved-history could not be saved: {ex.Message}";
                }

                slotProgress.Value = 100;
                slotStatus.Text = historyWarning is not null
                    ? historyWarning
                    : failed.Count == 0
                    ? $"Complete • {moved:N0} file(s) moved to {destination.Path}"
                    : $"Finished • {moved:N0} moved, {failed.Count:N0} failed";
                slotFiles.Text = failed.Count == 0
                    ? string.Join("\n", sourcePaths.Select(path => "✓ " + Path.GetFileName(path)))
                    : string.Join("\n", failed.Take(6));
                FileMoverStatusText.Text = $"Move {slotIndex + 1} finished. This slot can be used again.";
            }
            catch (Exception ex)
            {
                slotStatus.Text = $"File mover could not start: {ex.Message}";
            }
            finally
            {
                foreach (var sourcePath in reservedByThisSlot)
                    _fileMoverReservedPaths.Remove(sourcePath);
                _fileMoverSlotBusy[slotIndex] = false;
                slotButton.IsEnabled = true;
                slotButton.Content = "Move more";
            }
        }

        private (Button Button, TextBlock Status, ProgressBar Progress, TextBlock Files) GetFileMoverSlotControls(int slotIndex) =>
            slotIndex switch
            {
                1 => (FileMoverSlot2Button, FileMoverSlot2Status, FileMoverSlot2Progress, FileMoverSlot2Files),
                2 => (FileMoverSlot3Button, FileMoverSlot3Status, FileMoverSlot3Progress, FileMoverSlot3Files),
                3 => (FileMoverSlot4Button, FileMoverSlot4Status, FileMoverSlot4Progress, FileMoverSlot4Files),
                _ => (FileMoverSlot1Button, FileMoverSlot1Status, FileMoverSlot1Progress, FileMoverSlot1Files)
            };

        internal static string BuildCleanMovieFileName(string originalName)
        {
            var extension = Path.GetExtension(originalName);
            var title = Path.GetFileNameWithoutExtension(originalName);

            title = Regex.Replace(title, @"[\._]+", " ");
            title = Regex.Replace(title, @"^\s*(?:www\.[^\s]+\s*[-–—]\s*|[\[\(][^\]\)]*[\]\)]\s*)+", "", RegexOptions.IgnoreCase);

            var releaseMarker = Regex.Match(
                title,
                @"(?ix)
                  \s*[\[\(\-]?\s*
                  (?:
                    (?:19|20)\d{2} |
                    \d{3,4}p |
                    4k | uhd | hdr(?:10)? | dv |
                    blu[\s-]?ray | b[rd]rip | web[\s-]?(?:dl|rip) | hdtv | dvd(?:rip)? |
                    remux | proper | repack |
                    x26[45] | h[\s.-]?26[45] | hevc | av1 |
                    aac(?:\d(?:\.\d)?)? | ac3 | eac3 | dts(?:[\s-]?hd)? | atmos |
                    multi | dual[\s-]?audio |
                    extended | unrated | directors?[\s-]?cut |
                    \d+(?:\.\d+)?gb
                  )\b");
            if (releaseMarker.Success && releaseMarker.Index > 0)
                title = title[..releaseMarker.Index];

            title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '.', '_');
            foreach (var invalid in Path.GetInvalidFileNameChars())
                title = title.Replace(invalid, ' ');
            title = Regex.Replace(title, @"\s+", " ").Trim();

            return string.IsNullOrWhiteSpace(title)
                ? originalName
                : title + extension.ToLowerInvariant();
        }

        private static async Task<string> GetAvailableFileNameAsync(StorageFolder folder, string requestedName)
        {
            if (await folder.TryGetItemAsync(requestedName) is null)
                return requestedName;

            var extension = Path.GetExtension(requestedName);
            var title = Path.GetFileNameWithoutExtension(requestedName);
            for (var number = 2; ; number++)
            {
                var candidate = $"{title} ({number}){extension}";
                if (await folder.TryGetItemAsync(candidate) is null)
                    return candidate;
            }
        }

        private static async Task MoveFileWithProgressAsync(
            StorageFile source,
            StorageFolder destination,
            string targetName,
            Action<double> reportProgress)
        {
            StorageFile? target = null;
            try
            {
                target = await destination.CreateFileAsync(targetName, CreationCollisionOption.FailIfExists);
                {
                    await using var input = await source.OpenStreamForReadAsync();
                    await using var output = await target.OpenStreamForWriteAsync();
                    var buffer = new byte[8 * 1024 * 1024];
                    long bytesMoved = 0;
                    var progressTimer = System.Diagnostics.Stopwatch.StartNew();
                    long lastProgressUpdate = -1;
                    int bytesRead;

                    while (true)
                    {
                        using var readTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try
                        {
                            bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                        }
                        catch (OperationCanceledException) when (readTimeout.IsCancellationRequested)
                        {
                            throw new TimeoutException("The source file stopped responding for 30 seconds.");
                        }

                        if (bytesRead == 0)
                            break;

                        using var writeTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try
                        {
                            await output.WriteAsync(buffer.AsMemory(0, bytesRead), writeTimeout.Token);
                        }
                        catch (OperationCanceledException) when (writeTimeout.IsCancellationRequested)
                        {
                            throw new TimeoutException("The destination stopped responding for 30 seconds.");
                        }

                        bytesMoved += bytesRead;
                        var percentage = input.Length == 0 ? 100 : bytesMoved * 100d / input.Length;
                        if (lastProgressUpdate < 0 || progressTimer.ElapsedMilliseconds - lastProgressUpdate >= 200 || percentage >= 100)
                        {
                            reportProgress(percentage);
                            lastProgressUpdate = progressTimer.ElapsedMilliseconds;
                        }
                    }

                    using var flushTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await output.FlushAsync(flushTimeout.Token);
                    }
                    catch (OperationCanceledException) when (flushTimeout.IsCancellationRequested)
                    {
                        throw new TimeoutException("The destination stopped responding while finalising the file.");
                    }
                }

                reportProgress(100);
                await source.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
                if (target is not null)
                {
                    try
                    {
                        await target.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                        // Keep the original move error; a partial target may be locked externally.
                    }
                }

                throw;
            }
        }

        private void VideoQualityPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item)
                return;
            (_videoWidth, _videoHeight) = ParsePreset(item.Tag?.ToString());
            VideoQualityButton.Content = $"Video recording quality preset: {PresetLabel(_videoWidth)}";
        }

        private void PictureQualityPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item)
                return;
            (_pictureWidth, _pictureHeight) = ParsePreset(item.Tag?.ToString());
            PictureQualityButton.Content = $"Picture quality preset: {PresetLabel(_pictureWidth)}";
        }

        private static (int Width, int Height) ParsePreset(string? value)
        {
            var parts = value?.Split('x');
            return parts is { Length: 2 } &&
                   int.TryParse(parts[0], out var width) &&
                   int.TryParse(parts[1], out var height)
                ? (width, height)
                : (3840, 2160);
        }

        private static string PresetLabel(int width) => width switch
        {
            1280 => "720p",
            1920 => "1080p",
            2560 => "1440p",
            _ => "4K"
        };

        private void Capture_StatusChanged(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() => StatusText.Text = message);
        }

        private void Capture_RecordingStateChanged(object? sender, bool isRecording)
        {
            DispatcherQueue.TryEnqueue(RefreshRecordingState);
        }

        private void RefreshRecordingState()
        {
            StopRecordingButton.IsEnabled = _capture.IsRecording;
            if (_capture.IsRecording)
                StatusText.Text = "Recording selected area in 4K UHD…";
        }

        private void ToolsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _capture.RecordingStateChanged -= Capture_RecordingStateChanged;
            _capture.StatusChanged -= Capture_StatusChanged;
        }
    }
}

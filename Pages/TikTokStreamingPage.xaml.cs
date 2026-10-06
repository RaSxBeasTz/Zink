using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Zink.Services;
using Zink.Services.NativeCalling;
using Zink.Services.Recording;
using Zink.Services.Streaming;

namespace Zink.Pages
{
    public sealed partial class TikTokStreamingPage : Page
    {
        private readonly NativeTwitchStreamingService _streamingService = NativeTwitchStreamingService.TikTokInstance;
        private readonly StreamingVideoCapturePipeline _obsCaptureService = StreamingVideoCapturePipeline.Instance;
        private const string StreamKeySettingKey = "Zink.Streaming.TikTokStreamKey";
        private DateTimeOffset _lastPreviewUiUpdateUtc = DateTimeOffset.MinValue;
        private byte[]? _lastPreviewFrameData;
        private readonly object _previewFrameSync = new();
        private NativeScreenFrameEventArgs? _pendingPreviewFrame;
        private bool _isPreviewUiPumpRunning;
        private long _previewFramesReceived;
        private long _previewFramesDisplayed;
        private long _previewFramesCoalesced;
        private double _lastPreviewDecodeMilliseconds;
        private double _maxPreviewFrameAgeMilliseconds;
        private int _lastPreviewBytes;
        private DateTimeOffset _lastPreviewPerfLogUtc = DateTimeOffset.MinValue;

        public TikTokStreamingPage()
        {
            InitializeComponent();
            Loaded += TikTokStreamingPage_Loaded;
            Unloaded += TikTokStreamingPage_Unloaded;
        }

        private void TikTokStreamingPage_Loaded(object sender, RoutedEventArgs e)
        {
            _streamingService.StatusChanged += StreamingService_StatusChanged;
            _streamingService.StreamingStateChanged += StreamingService_StreamingStateChanged;
            _streamingService.StatsChanged += StreamingService_StatsChanged;
            _obsCaptureService.FrameReady += ObsCaptureService_FrameReady;
            _obsCaptureService.StreamingFailed += ObsCaptureService_StreamingFailed;

            LoadStreamingSettings();
            StatusText.Text = _streamingService.LastStatus;
            LoadAudioDevicesAsync();
            UpdateStats(_streamingService.CurrentStats);
            UpdateStreamingState(_streamingService.IsStreaming);
            CaptureBackendText.Text = "No source selected";
            CanvasValueText.Text = GetSelectedQualityText();
            PreviewSubtitleText.Text = "Choose a window or screen to start the preview.";
            UpdateQualitySummary();
        }

        private void TikTokStreamingPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _streamingService.StatusChanged -= StreamingService_StatusChanged;
            _streamingService.StreamingStateChanged -= StreamingService_StreamingStateChanged;
            _streamingService.StatsChanged -= StreamingService_StatsChanged;
            _obsCaptureService.FrameReady -= ObsCaptureService_FrameReady;
            _obsCaptureService.StreamingFailed -= ObsCaptureService_StreamingFailed;
            _ = _obsCaptureService.StopAsync();
        }

        private async void StartStreamButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartStreamButton.IsEnabled = false;
                StatusText.Text = "Starting native TikTok stream...";
                SaveStreamingSettings();
                await _streamingService.StartAsync(
                    StreamKeyBox.Password,
                    ServerTextBox.Text,
                    GetSelectedQualityPreset(),
                    GetComboBoxText(DesktopAudioComboBox),
                    DesktopAudioVolumeSlider.Value / 100.0,
                    MuteDesktopAudioButton.IsChecked == true,
                    GetComboBoxText(MicrophoneComboBox),
                    MicrophoneVolumeSlider.Value / 100.0,
                    MuteMicrophoneButton.IsChecked == true,
                    LowLatencyToggle.IsOn);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Streaming start failed: {ex.Message}";
            }
            finally
            {
                UpdateStreamingState(_streamingService.IsStreaming);
            }
        }

        private async void StopStreamButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopStreamButton.IsEnabled = false;
                await _streamingService.StopAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Streaming stop failed: {ex.Message}";
            }
            finally
            {
                UpdateStreamingState(_streamingService.IsStreaming);
            }
        }

        private async void StreamingService_StatusChanged(object? sender, string e)
        {
            await DispatcherQueue.EnqueueAsync(() => StatusText.Text = e);
        }

        private async void StreamingService_StreamingStateChanged(object? sender, bool isStreaming)
        {
            await DispatcherQueue.EnqueueAsync(() => UpdateStreamingState(isStreaming));
        }

        private async void StreamingService_StatsChanged(object? sender, NativeStreamingStats stats)
        {
            await DispatcherQueue.EnqueueAsync(() => UpdateStats(stats));
        }

        private void UpdateStreamingState(bool isStreaming)
        {
            LiveStateText.Text = isStreaming ? "LIVE" : "OFFLINE";
            LiveIndicator.Fill = new SolidColorBrush(isStreaming
                ? Microsoft.UI.Colors.Red
                : global::Windows.UI.Color.FromArgb(255, 102, 115, 126));

            StartStreamButton.IsEnabled = !isStreaming;
            StopStreamButton.IsEnabled = isStreaming;
            StreamKeyBox.IsEnabled = !isStreaming;
            ServerTextBox.IsEnabled = !isStreaming;
            StreamQualityComboBox.IsEnabled = !isStreaming;
            DesktopAudioComboBox.IsEnabled = !isStreaming;
            MicrophoneComboBox.IsEnabled = !isStreaming;
            SelectCaptureSourceButton.IsEnabled = !isStreaming;

            try
            {
                DiscordPresenceService.Instance.SetStreamingPresence("TikTok", isStreaming);
            }
            catch
            {
            }
        }

        private async void SelectCaptureSourceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_streamingService.IsStreaming)
            {
                StatusText.Text = "Stop streaming before changing the capture source.";
                return;
            }

            try
            {
                SelectCaptureSourceButton.IsEnabled = false;
                SelectCaptureSourceButton.Content = "Selecting...";

                if (_obsCaptureService.IsRunning)
                    await _obsCaptureService.StopAsync();

                CaptureSourceHelper.ClearCachedSelection();
                await StartObsCapturePreviewAsync();
            }
            finally
            {
                SelectCaptureSourceButton.IsEnabled = !_streamingService.IsStreaming;
                SelectCaptureSourceButton.Content = _obsCaptureService.IsRunning
                    ? "Change Window / Screen"
                    : "Select Window / Screen";
            }
        }

        private void RefreshSourcesButton_Click(object sender, RoutedEventArgs e)
        {
            if (SourcesListView is null)
                return;

            SourcesListView.Items.Clear();
            SourcesListView.Items.Add(new ListViewItem { Content = "Windows Graphics Capture" });
            SourcesListView.SelectedIndex = 0;
        }

        private void RefreshAudioDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            LoadAudioDevicesAsync();
        }

        private void DesktopAudioVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (DesktopAudioLevelText is null)
                return;

            var level = (int)Math.Round(e.NewValue);
            DesktopAudioLevelText.Text = MuteDesktopAudioButton?.IsChecked == true ? "Muted" : $"{level}%";

            if (_streamingService.IsStreaming)
            {
                StatusText.Text = "Desktop audio volume change will apply when you restart the stream.";
            }
        }

        private void MicrophoneVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (MicLevelText is null)
                return;

            var level = (int)Math.Round(e.NewValue);
            MicLevelText.Text = MuteMicrophoneButton?.IsChecked == true ? "Muted" : $"{level}%";

            if (_streamingService.IsStreaming)
            {
                StatusText.Text = "Mic volume change will apply when you restart the stream.";
            }
        }

        private void MuteMicrophoneButton_Checked(object sender, RoutedEventArgs e)
        {
            MicLevelText.Text = "Muted";
            MuteMicrophoneButton.Content = "Mute your microphone";
            StatusText.Text = _streamingService.IsStreaming
                ? "Mic mute will apply when you restart the stream."
                : "Microphone muted.";
            RefreshSourcesButton_Click(sender, e);
        }

        private void MuteDesktopAudioButton_Checked(object sender, RoutedEventArgs e)
        {
            DesktopAudioLevelText.Text = "Muted";
            MuteDesktopAudioButton.Content = "Mute the desktop sound";
            StatusText.Text = _streamingService.IsStreaming
                ? "Desktop audio mute will apply when you restart the stream."
                : "Desktop audio muted.";
            RefreshSourcesButton_Click(sender, e);
        }

        private void MuteDesktopAudioButton_Unchecked(object sender, RoutedEventArgs e)
        {
            DesktopAudioLevelText.Text = $"{(int)Math.Round(DesktopAudioVolumeSlider.Value)}%";
            MuteDesktopAudioButton.Content = "Mute the desktop sound";
            StatusText.Text = _streamingService.IsStreaming
                ? "Desktop audio unmute will apply when you restart the stream."
                : "Desktop audio unmuted.";
            RefreshSourcesButton_Click(sender, e);
        }

        private void AudioDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatusText is null)
                return;

            RefreshSourcesButton_Click(sender, new RoutedEventArgs());
        }

        private void MuteMicrophoneButton_Unchecked(object sender, RoutedEventArgs e)
        {
            MicLevelText.Text = $"{(int)Math.Round(MicrophoneVolumeSlider.Value)}%";
            MuteMicrophoneButton.Content = "Mute your microphone";
            StatusText.Text = _streamingService.IsStreaming
                ? "Mic unmute will apply when you restart the stream."
                : "Microphone unmuted.";
            RefreshSourcesButton_Click(sender, e);
        }

        private void LowLatencyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (StatusText is null || LowLatencyToggle is null)
                return;

            StatusText.Text = LowLatencyToggle.IsOn
                ? "Low-latency H.264 enabled."
                : "Low-latency tune disabled for the next stream.";
        }

        private void StreamQualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatusText is null)
                return;

            UpdateQualitySummary();
            StatusText.Text = _streamingService.IsStreaming
                ? "Stream quality changes apply when you restart the stream."
                : $"{GetSelectedQualityText()} selected.";
        }

        private static string GetSelectedListViewText(ListView listView, string fallback)
        {
            return (listView.SelectedItem as ListViewItem)?.Content?.ToString() ?? fallback;
        }

        private async void LoadAudioDevicesAsync()
        {
            var devices = await NativeTwitchStreamingService.GetDirectShowAudioDevicesAsync();
            await DispatcherQueue.EnqueueAsync(() =>
            {
                DesktopAudioComboBox.Items.Clear();
                MicrophoneComboBox.Items.Clear();

                DesktopAudioComboBox.Items.Add("None");
                DesktopAudioComboBox.Items.Add(NativeTwitchStreamingService.WindowsLoopbackAudioDeviceName);
                MicrophoneComboBox.Items.Add("None");

                foreach (var device in devices)
                {
                    DesktopAudioComboBox.Items.Add(device);
                    MicrophoneComboBox.Items.Add(device);
                }

                DesktopAudioComboBox.SelectedIndex = 1;
                MicrophoneComboBox.SelectedIndex = devices.Count > 0 ? 1 : 0;
                AudioDeviceStatusText.Text = devices.Count > 0
                    ? $"Desktop sound uses Windows loopback. Found {devices.Count} microphone or virtual audio input device(s)."
                    : "Desktop sound uses Windows loopback. No microphone input devices were found.";
                StatusText.Text = devices.Count > 0
                    ? $"Loaded {devices.Count} audio input device(s)."
                    : "No microphone audio input devices found.";
                RefreshSourcesButton_Click(this, new RoutedEventArgs());
            });
        }

        private void UpdateStats(NativeStreamingStats stats)
        {
            if (PanelCaptureFpsText is null)
                return;

            PanelCaptureFpsText.Text = $"{stats.CaptureFps:0.0}";
            PanelEncodeFpsText.Text = $"{stats.EncodedFps:0.0}";
            PanelSendFpsText.Text = $"{stats.SendFps:0.0}";
            PanelBitrateText.Text = stats.Bitrate;
            PanelEncodeMsText.Text = $"{stats.EncodeMilliseconds:0.0} ms";
            PanelStreamTimeText.Text = stats.OutputTime;
            PanelQualityText.Text = $"{GetSelectedQualityText()} / {stats.Bitrate}";
            PanelHealthText.Text = $"Dropped {stats.DroppedFrames} / Repeated {stats.DuplicatedFrames} / Frames {stats.Frame}";
            PanelStreamStatusText.Text = _streamingService.IsStreaming
                ? $"Live output sending at {stats.SendFps:0.0} fps."
                : "Ready to stream.";
            CanvasValueText.Text = $"{GetSelectedQualityText()} / {stats.Bitrate}";
            CaptureBackendText.Text = string.IsNullOrWhiteSpace(stats.OutputTime) || stats.OutputTime == "--"
                ? CaptureBackendText.Text
                : $"{NativeTwitchStreamingService.EncoderName}";
            SceneTransitionValueText.Text = _streamingService.IsStreaming
                ? $"Live {stats.SendFps:0.0} fps"
                : "Cut / Fade 300 ms";
        }

        private static string GetComboBoxText(ComboBox comboBox)
        {
            var text = comboBox.SelectedItem?.ToString() ?? string.Empty;
            return string.Equals(text, "None", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : text.Trim();
        }

        private ScreenShareQualityPreset GetSelectedQualityPreset()
        {
            var selected = (StreamQualityComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return string.Equals(selected, "1080p", StringComparison.OrdinalIgnoreCase)
                ? ScreenShareQualityPreset.FullHd1080p
                : ScreenShareQualityPreset.Hd720p;
        }

        private string GetSelectedQualityText()
        {
            var profile = ScreenShareQualityProfile.FromPreset(GetSelectedQualityPreset());
            return $"{profile.Name} / {NativeTwitchStreamingService.OutputFps} FPS";
        }

        private int GetSelectedBitrateKbps()
        {
            return GetSelectedQualityPreset() == ScreenShareQualityPreset.FullHd1080p
                ? NativeTwitchStreamingService.FullHdVideoBitrateKbps
                : NativeTwitchStreamingService.VideoBitrateKbps;
        }

        private void UpdateQualitySummary()
        {
            var bitrate = GetSelectedBitrateKbps();
            if (PanelQualityText is not null && !_streamingService.IsStreaming)
                PanelQualityText.Text = $"{GetSelectedQualityText()} / {bitrate}k";
            if (PanelBitrateText is not null && !_streamingService.IsStreaming)
                PanelBitrateText.Text = $"{bitrate}k";
            if (CanvasValueText is not null && !_streamingService.IsStreaming)
                CanvasValueText.Text = $"{GetSelectedQualityText()} / {bitrate}k";
        }

        private void LoadStreamingSettings()
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(StreamKeySettingKey, out var value) &&
                    value is string streamKey &&
                    !string.IsNullOrWhiteSpace(streamKey))
                {
                    StreamKeyBox.Password = streamKey;
                }
            }
            catch
            {
            }
        }

        private void SaveStreamingSettings()
        {
            try
            {
                var streamKey = StreamKeyBox.Password.Trim();
                if (string.IsNullOrWhiteSpace(streamKey))
                    ApplicationData.Current.LocalSettings.Values.Remove(StreamKeySettingKey);
                else
                    ApplicationData.Current.LocalSettings.Values[StreamKeySettingKey] = streamKey;
            }
            catch
            {
            }
        }

        private async Task StartObsCapturePreviewAsync()
        {
            try
            {
                _obsCaptureService.SetQuality(GetSelectedQualityPreset());
                _obsCaptureService.SetBitrateOverride(GetSelectedBitrateKbps() * 1000);
                _obsCaptureService.SetAdaptiveLatencyMode(false);
                _obsCaptureService.EnablePreviewFrames = true;
                _obsCaptureService.PrioritizeStreamingPerformance = false;
                _obsCaptureService.PreferredCaptureSourceMode = NativeCaptureSourceMode.GameOrWindow;
                _obsCaptureService.RequireHardwareEncoder = true;
                _obsCaptureService.RequireDirectX12CapturePath = true;
                CaptureBackendText.Text = "Selecting source";
                StatusText.Text = "Choose a window or screen to preview.";
                DiagnosticLogService.WriteLine("[StreamingPreview:UI] Starting live preview diagnostics. Select a source and leave it running for a few seconds to capture receive/display/decode timings.");

                await _obsCaptureService.StartAsync();
                if (_obsCaptureService.IsRunning)
                {
                    SelectCaptureSourceButton.Content = "Change Window / Screen";
                    StatusText.Text = $"{GetSelectedQualityText()} capture preview started.";
                }
                else
                {
                    CaptureBackendText.Text = "No source selected";
                    StatusText.Text = "No capture source selected.";
                }
            }
            catch (Exception ex)
            {
                CaptureBackendText.Text = "Capture unavailable";
                StatusText.Text = $"OBS-style capture could not start: {ex.Message}";
            }
        }

        private async void ObsCaptureService_FrameReady(object? sender, NativeScreenFrameEventArgs e)
        {
            var now = DateTimeOffset.UtcNow;
            if (ReferenceEquals(e.PreviewFrameData, _lastPreviewFrameData) ||
                e.PreviewFrameData.Length == 0 ||
                now - _lastPreviewUiUpdateUtc < TimeSpan.FromMilliseconds(33))
                return;

            _lastPreviewUiUpdateUtc = now;
            _lastPreviewFrameData = e.PreviewFrameData;

            lock (_previewFrameSync)
            {
                _previewFramesReceived++;
                if (_isPreviewUiPumpRunning && _pendingPreviewFrame is not null)
                    _previewFramesCoalesced++;

                _pendingPreviewFrame = e;
                if (_isPreviewUiPumpRunning)
                    return;

                _isPreviewUiPumpRunning = true;
            }

            if (!DispatcherQueue.TryEnqueue(ProcessLatestPreviewFrameAsync))
            {
                lock (_previewFrameSync)
                    _isPreviewUiPumpRunning = false;
            }
        }

        private async void ProcessLatestPreviewFrameAsync()
        {
            while (true)
            {
                NativeScreenFrameEventArgs? frame;
                lock (_previewFrameSync)
                {
                    frame = _pendingPreviewFrame;
                    _pendingPreviewFrame = null;
                    if (frame is null)
                    {
                        _isPreviewUiPumpRunning = false;
                        return;
                    }
                }

                var decodeTimer = Stopwatch.StartNew();
                await SetDesktopPreviewAsync(frame.PreviewFrameData);
                decodeTimer.Stop();

                _previewFramesDisplayed++;
                _lastPreviewDecodeMilliseconds = decodeTimer.Elapsed.TotalMilliseconds;
                _lastPreviewBytes = frame.PreviewFrameData.Length;
                var frameAgeMilliseconds = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.PreviewTimestamp);
                _maxPreviewFrameAgeMilliseconds = Math.Max(_maxPreviewFrameAgeMilliseconds, frameAgeMilliseconds);

                CaptureBackendText.Text = _obsCaptureService.EncoderMode;
                PreviewSubtitleText.Text = $"{frame.Width} x {frame.Height} {frame.Codec.ToUpperInvariant()} - live preview {frameAgeMilliseconds:0}ms behind";
                CanvasValueText.Text = $"{frame.QualityName} / {_obsCaptureService.CurrentTargetFps} FPS / {_obsCaptureService.CurrentBitrate / 1000}k";
                SceneTransitionValueText.Text = $"Preview {_obsCaptureService.EncodedFps:0.0} fps";

                if (!_streamingService.IsStreaming)
                {
                    PanelCaptureFpsText.Text = $"{_obsCaptureService.CaptureFps:0.0}";
                    PanelEncodeFpsText.Text = $"{_obsCaptureService.EncodedFps:0.0}";
                    PanelSendFpsText.Text = "0.0";
                    PanelBitrateText.Text = $"{_obsCaptureService.CurrentBitrate / 1000}k";
                    PanelEncodeMsText.Text = $"{_obsCaptureService.LastEncodeMilliseconds:0.0} ms";
                    PanelStreamTimeText.Text = "Preview";
                    PanelQualityText.Text = $"{frame.QualityName} / {_obsCaptureService.CurrentTargetFps} FPS";
                    PanelHealthText.Text = $"Preview age {frameAgeMilliseconds:0} ms / Auto {_obsCaptureService.AutoDowngradeCount}";
                    PanelStreamStatusText.Text = "Live preview is running.";
                }

                WritePreviewPerformanceLogIfDue();
            }
        }

        private void WritePreviewPerformanceLogIfDue()
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastPreviewPerfLogUtc != DateTimeOffset.MinValue &&
                now - _lastPreviewPerfLogUtc < TimeSpan.FromSeconds(1))
            {
                return;
            }

            var elapsedSeconds = _lastPreviewPerfLogUtc == DateTimeOffset.MinValue
                ? 1.0
                : Math.Max(0.001, (now - _lastPreviewPerfLogUtc).TotalSeconds);
            _lastPreviewPerfLogUtc = now;

            var received = _previewFramesReceived;
            var displayed = _previewFramesDisplayed;
            var coalesced = _previewFramesCoalesced;
            var displayFps = displayed / elapsedSeconds;
            var receiveFps = received / elapsedSeconds;

            DiagnosticLogService.WriteLine(
                $"[StreamingPreview:UI] receive={receiveFps:0.0}fps display={displayFps:0.0}fps coalesced={coalesced} decodeMs={_lastPreviewDecodeMilliseconds:0.0} maxAgeMs={_maxPreviewFrameAgeMilliseconds:0} bytes={_lastPreviewBytes} capture={_obsCaptureService.CaptureFps:0.0}fps encode={_obsCaptureService.EncodedFps:0.0}fps previewEncodeMs={_obsCaptureService.LastPreviewMilliseconds:0.0}.");

            _previewFramesReceived = 0;
            _previewFramesDisplayed = 0;
            _previewFramesCoalesced = 0;
            _maxPreviewFrameAgeMilliseconds = 0;
        }

        private async void ObsCaptureService_StreamingFailed(object? sender, string e)
        {
            await DispatcherQueue.EnqueueAsync(() =>
            {
                CaptureBackendText.Text = "Capture warning";
                StatusText.Text = e;
            });
        }

        private async Task SetDesktopPreviewAsync(byte[] frameBytes)
        {
            using var randomAccessStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(frameBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            randomAccessStream.Seek(0);
            var bitmapImage = new BitmapImage();
            if (DesktopPreviewImage.ActualWidth > 0)
            {
                var scale = XamlRoot?.RasterizationScale ?? 1.0;
                bitmapImage.DecodePixelWidth = Math.Max(1, (int)Math.Round(DesktopPreviewImage.ActualWidth * scale));
            }
            await bitmapImage.SetSourceAsync(randomAccessStream);
            DesktopPreviewImage.Source = bitmapImage;
        }
    }
}

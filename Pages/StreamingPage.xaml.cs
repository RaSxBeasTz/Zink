using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Zink.Models;
using Zink.Services;
using Zink.Services.NativeCalling;
using Zink.Services.Recording;
using Zink.Services.Streaming;
using Zink.Windows;

namespace Zink.Pages
{
    public sealed partial class StreamingPage : Page
    {
        private readonly NativeTwitchStreamingService _streamingService = NativeTwitchStreamingService.Instance;
        private readonly StreamingVideoCapturePipeline _obsCaptureService = StreamingVideoCapturePipeline.Instance;
        private const string StreamKeySettingKey = "Zink.Streaming.TwitchStreamKey";
        private const string EncoderFamilySettingKey = "Zink.Streaming.TwitchEncoderFamily";
        private const string QualitySettingKey = "Zink.Streaming.TwitchQuality";
        private const string IncludeDiscordSoundSettingKey = "Zink.Streaming.TwitchIncludeDiscordSound";
        private const string IncludeSpotifySoundSettingKey = "Zink.Streaming.TwitchIncludeSpotifySound";
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
        private bool? _lastObservedStreamingState;
        private string _activeStreamingProvider = "Twitch";

        public StreamingPage()
        {
            InitializeComponent();
            Loaded += StreamingPage_Loaded;
            Unloaded += StreamingPage_Unloaded;
        }

        private void StreamingPage_Loaded(object sender, RoutedEventArgs e)
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
            UpdateStreamingState(_streamingService.IsStreaming, notify: false);
            CaptureBackendText.Text = "No source selected";
            CanvasValueText.Text = $"{GetSelectedEncoderFamilyText()} / {GetSelectedQualityText()}";
            PreviewSubtitleText.Text = "Choose a window or screen to start the preview.";
            UpdateQualitySummary();
        }

        private void StreamingPage_Unloaded(object sender, RoutedEventArgs e)
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
                _activeStreamingProvider = GetSelectedStreamingProviderName();
                StatusText.Text = $"Starting native stream to {_activeStreamingProvider}...";
                SaveStreamingSettings();
                ApplySelectedEncoderFamily();
                await _streamingService.StartAsync(
                    StreamKeyBox.Password,
                    ServerTextBox.Text,
                    GetSelectedQualityPreset(),
                    GetSelectedAudioDeviceId(DesktopAudioComboBox),
                    DesktopAudioVolumeSlider.Value / 100.0,
                    MuteDesktopAudioButton.IsChecked == true,
                    GetSelectedAudioDeviceId(MicrophoneComboBox),
                    MicrophoneVolumeSlider.Value / 100.0,
                    MuteMicrophoneButton.IsChecked == true,
                    LowLatencyToggle.IsOn,
                    IncludeDiscordSoundToggle.IsOn,
                    IncludeSpotifySoundToggle.IsOn);
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

        private void UpdateStreamingState(bool isStreaming, bool notify = true)
        {
            LiveStateText.Text = isStreaming ? "LIVE" : "OFFLINE";
            LiveIndicator.Fill = new SolidColorBrush(isStreaming
                ? Microsoft.UI.Colors.Red
                : global::Windows.UI.Color.FromArgb(255, 102, 115, 126));

            StartStreamButton.IsEnabled = !isStreaming;
            StopStreamButton.IsEnabled = isStreaming;
            StreamKeyBox.IsEnabled = !isStreaming;
            ServerTextBox.IsEnabled = !isStreaming;
            EncoderFamilyComboBox.IsEnabled = !isStreaming;
            StreamQualityComboBox.IsEnabled = !isStreaming;
            DesktopAudioComboBox.IsEnabled = !isStreaming;
            MicrophoneComboBox.IsEnabled = !isStreaming;
            SelectCaptureSourceButton.IsEnabled = !isStreaming;
            IncludeDiscordSoundToggle.IsEnabled = !isStreaming;
            IncludeSpotifySoundToggle.IsEnabled = !isStreaming;

            try
            {
                var provider = isStreaming
                    ? (!string.IsNullOrWhiteSpace(_activeStreamingProvider) ? _activeStreamingProvider : GetSelectedStreamingProviderName())
                    : (!string.IsNullOrWhiteSpace(_activeStreamingProvider) ? _activeStreamingProvider : GetSelectedStreamingProviderName());

                DiscordPresenceService.Instance.SetStreamingPresence(provider, isStreaming);

                if (notify &&
                    _lastObservedStreamingState.HasValue &&
                    _lastObservedStreamingState.Value != isStreaming)
                {
                    if (isStreaming)
                    {
                        NotificationService.Instance.Show(
                            "The stream has started",
                            $"The stream has started and you are now live streaming on {provider}.");
                    }
                    else
                    {
                        NotificationService.Instance.Show(
                            "Live stream ended",
                            $"Your live stream on {provider} has ended so you are no longer live.");
                    }
                }

                _lastObservedStreamingState = isStreaming;
            }
            catch
            {
            }
        }

        private string GetSelectedStreamingProviderName()
        {
            try
            {
                var server = ServerTextBox?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(server))
                    return "Twitch";

                if (server.Contains("twitch", StringComparison.OrdinalIgnoreCase))
                    return "Twitch";

                if (server.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                    server.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                    return "YouTube";

                if (server.Contains("facebook", StringComparison.OrdinalIgnoreCase) ||
                    server.Contains("fbcdn", StringComparison.OrdinalIgnoreCase))
                    return "Facebook Live";

                if (server.Contains("kick", StringComparison.OrdinalIgnoreCase))
                    return "Kick";

                if (server.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
                    return "TikTok Live";

                if (server.Contains("instagram", StringComparison.OrdinalIgnoreCase))
                    return "Instagram Live";

                if (server.Contains("trovo", StringComparison.OrdinalIgnoreCase))
                    return "Trovo";

                if (server.Contains("restream", StringComparison.OrdinalIgnoreCase))
                    return "Restream";

                if (server.Contains("vimeo", StringComparison.OrdinalIgnoreCase))
                    return "Vimeo";

                if (server.Contains("rumble", StringComparison.OrdinalIgnoreCase))
                    return "Rumble";

                return "custom RTMP";
            }
            catch
            {
                return "Twitch";
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

        private void IncludeDiscordSoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (StatusText is null || IncludeDiscordSoundToggle is null)
                return;

            if (!IncludeDiscordSoundToggle.IsOn && IncludeSpotifySoundToggle is not null && !IncludeSpotifySoundToggle.IsOn)
                IncludeSpotifySoundToggle.IsOn = true;

            ApplicationData.Current.LocalSettings.Values[IncludeDiscordSoundSettingKey] = IncludeDiscordSoundToggle.IsOn;
            StatusText.Text = IncludeDiscordSoundToggle.IsOn
                ? "Discord sound will be included in the next stream."
                : "Discord sound will be excluded from the next stream.";
        }

        private void IncludeSpotifySoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (StatusText is null || IncludeSpotifySoundToggle is null)
                return;

            if (!IncludeSpotifySoundToggle.IsOn && IncludeDiscordSoundToggle is not null && !IncludeDiscordSoundToggle.IsOn)
                IncludeDiscordSoundToggle.IsOn = true;

            ApplicationData.Current.LocalSettings.Values[IncludeSpotifySoundSettingKey] = IncludeSpotifySoundToggle.IsOn;
            StatusText.Text = IncludeSpotifySoundToggle.IsOn
                ? "Spotify sound will be included in the next stream."
                : "Spotify sound will be excluded from the next stream.";
        }

        private void StreamQualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatusText is null)
                return;

            UpdateQualitySummary();
            StatusText.Text = _streamingService.IsStreaming
                ? "Stream quality changes apply when you restart the stream."
                : $"{GetSelectedEncoderFamilyText()} / {GetSelectedQualityText()} selected.";
        }

        private void EncoderFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatusText is null)
                return;

            ApplySelectedEncoderFamily();
            UpdateQualitySummary();
            StatusText.Text = _streamingService.IsStreaming
                ? "Encoder changes apply when you restart the stream."
                : $"{GetSelectedEncoderFamilyText()} selected.";
        }

        private static string GetSelectedListViewText(ListView listView, string fallback)
        {
            return (listView.SelectedItem as ListViewItem)?.Content?.ToString() ?? fallback;
        }

        private async void LoadAudioDevicesAsync()
        {
            var renderDevices = await AudioDeviceService.GetRenderDevicesAsync();
            var microphoneDevices = await AudioDeviceService.GetCaptureDevicesAsync();
            await DispatcherQueue.EnqueueAsync(() =>
            {
                DesktopAudioComboBox.Items.Clear();
                MicrophoneComboBox.Items.Clear();

                DesktopAudioComboBox.Items.Add("None");
                MicrophoneComboBox.Items.Add("None");

                foreach (var device in renderDevices)
                    DesktopAudioComboBox.Items.Add(device);

                foreach (var device in microphoneDevices)
                    MicrophoneComboBox.Items.Add(device);

                DesktopAudioComboBox.SelectedIndex = renderDevices.Count > 0 ? 1 : 0;
                MicrophoneComboBox.SelectedIndex = microphoneDevices.Count > 0 ? 1 : 0;
                AudioDeviceStatusText.Text = renderDevices.Count > 0 || microphoneDevices.Count > 0
                    ? $"Found {renderDevices.Count} desktop/system audio output device(s) and {microphoneDevices.Count} microphone input device(s)."
                    : "No desktop/system audio or microphone devices were found.";
                StatusText.Text = renderDevices.Count > 0 || microphoneDevices.Count > 0
                    ? "Loaded real Windows audio devices."
                    : "No audio devices found.";
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
            var selectedProfile = $"{GetSelectedEncoderFamilyText()} / {GetSelectedQualityText()}";
            PanelQualityText.Text = $"{selectedProfile} / {stats.Bitrate}";
            PanelHealthText.Text = $"Dropped {stats.DroppedFrames} / Repeated {stats.DuplicatedFrames} / Frames {stats.Frame}";
            PanelStreamStatusText.Text = _streamingService.IsStreaming
                ? $"Live output sending at {stats.SendFps:0.0} fps."
                : "Ready to stream.";
            CanvasValueText.Text = $"{selectedProfile} / {stats.Bitrate}";
            CaptureBackendText.Text = string.IsNullOrWhiteSpace(stats.OutputTime) || stats.OutputTime == "--"
                ? CaptureBackendText.Text
                : $"{GetSelectedEncoderFamilyText()} / {NativeTwitchStreamingService.EncoderName}";
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

        private static string GetSelectedAudioDeviceId(ComboBox comboBox)
        {
            if (comboBox.SelectedItem is RecorderDeviceItem device)
                return device.Id;

            return GetComboBoxText(comboBox);
        }

        private ScreenShareQualityPreset GetSelectedQualityPreset()
        {
            var selected = (StreamQualityComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return string.Equals(selected, "1080p", StringComparison.OrdinalIgnoreCase)
                ? ScreenShareQualityPreset.FullHd1080p
                : ScreenShareQualityPreset.Hd720p;
        }

        private ScreenShareH264EncoderFamily GetSelectedEncoderFamily()
        {
            var selected = (EncoderFamilyComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return string.Equals(selected, "Intel", StringComparison.OrdinalIgnoreCase)
                ? ScreenShareH264EncoderFamily.Intel
                : ScreenShareH264EncoderFamily.Nvidia;
        }

        private string GetSelectedEncoderFamilyText()
        {
            return GetSelectedEncoderFamily() == ScreenShareH264EncoderFamily.Intel
                ? "Intel"
                : "Nvidia";
        }

        private string GetSelectedQualityText()
        {
            var profile = ScreenShareQualityProfile.FromPreset(GetSelectedQualityPreset());
            return profile.Name;
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
            var selectedProfile = $"{GetSelectedEncoderFamilyText()} / {GetSelectedQualityText()}";
            if (PanelQualityText is not null && !_streamingService.IsStreaming)
                PanelQualityText.Text = $"{selectedProfile} / {bitrate}k";
            if (PanelBitrateText is not null && !_streamingService.IsStreaming)
                PanelBitrateText.Text = $"{bitrate}k";
            if (CanvasValueText is not null && !_streamingService.IsStreaming)
                CanvasValueText.Text = $"{selectedProfile} / {bitrate}k";
        }

        private void ApplySelectedEncoderFamily()
        {
            _obsCaptureService.PreferredH264EncoderFamily = GetSelectedEncoderFamily();
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

                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(EncoderFamilySettingKey, out var encoderValue) &&
                    encoderValue is string encoderFamily)
                {
                    EncoderFamilyComboBox.SelectedIndex = string.Equals(encoderFamily, "Intel", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                }

                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(QualitySettingKey, out var qualityValue) &&
                    qualityValue is string quality)
                {
                    StreamQualityComboBox.SelectedIndex = string.Equals(quality, "1080p", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                }

                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(IncludeDiscordSoundSettingKey, out var discordSoundValue) &&
                    discordSoundValue is bool includeDiscordSound)
                {
                    IncludeDiscordSoundToggle.IsOn = includeDiscordSound;
                }

                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(IncludeSpotifySoundSettingKey, out var spotifySoundValue) &&
                    spotifySoundValue is bool includeSpotifySound)
                {
                    IncludeSpotifySoundToggle.IsOn = includeSpotifySound;
                }

                if (!IncludeDiscordSoundToggle.IsOn && !IncludeSpotifySoundToggle.IsOn)
                    IncludeSpotifySoundToggle.IsOn = true;

                ApplySelectedEncoderFamily();

                UpdateTwitchViewerStatus();
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

                ApplicationData.Current.LocalSettings.Values[EncoderFamilySettingKey] =
                    GetSelectedEncoderFamily() == ScreenShareH264EncoderFamily.Intel ? "Intel" : "Nvidia";
                ApplicationData.Current.LocalSettings.Values[QualitySettingKey] =
                    GetSelectedQualityPreset() == ScreenShareQualityPreset.FullHd1080p ? "1080p" : "720p";
                ApplicationData.Current.LocalSettings.Values[IncludeDiscordSoundSettingKey] = IncludeDiscordSoundToggle.IsOn;
                ApplicationData.Current.LocalSettings.Values[IncludeSpotifySoundSettingKey] = IncludeSpotifySoundToggle.IsOn;
            }
            catch
            {
            }
        }

        private async void ConnectTwitchButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TwitchViewerCountService.HasConfiguredClientId)
            {
                StatusText.Text = "Add a Twitch Client ID to connect your streaming account.";
                await ShowTwitchViewerSettingsAsync("Add the Twitch Client ID, then Connect account can sign in to your Twitch account.");
                if (!TwitchViewerCountService.HasConfiguredClientId)
                {
                    UpdateTwitchViewerStatus();
                    StatusText.Text = "A Twitch Client ID is needed before the account can connect.";
                    return;
                }
            }

            StatusText.Text = "Connecting streaming account...";
            var result = await TwitchConnectWindow.ConnectAsync();
            if (result.Success && !string.IsNullOrWhiteSpace(result.StreamKey))
            {
                StreamKeyBox.Password = result.StreamKey;
                SaveStreamingSettings();
            }

            UpdateTwitchViewerStatus();
            StatusText.Text = result.Status;
            if (result.Success)
                TwitchViewerOverlayWindow.ShowSingleton();
        }

        private void DisconnectTwitchButton_Click(object sender, RoutedEventArgs e)
        {
            TwitchViewerCountService.Instance.Disconnect();
            TwitchViewerOverlayWindow.CloseSingleton();
            UpdateTwitchViewerStatus();
            StatusText.Text = "Streaming account disconnected.";
        }

        private void ShowTwitchViewersButton_Click(object sender, RoutedEventArgs e)
        {
            SaveStreamingSettings();
            TwitchViewerOverlayWindow.ShowSingleton();
            StatusText.Text = "Twitch viewer overlay is open.";
        }

        private void HideTwitchViewersButton_Click(object sender, RoutedEventArgs e)
        {
            TwitchViewerOverlayWindow.CloseSingleton();
            StatusText.Text = "Twitch viewer overlay hidden.";
        }

        private async void ResetTwitchButton_Click(object sender, RoutedEventArgs e)
        {
            TwitchViewerOverlayWindow.CloseSingleton();
            StatusText.Text = "Resetting streaming account sign-in...";
            await TwitchConnectWindow.ResetSavedSignInAsync();
            UpdateTwitchViewerStatus();
            StatusText.Text = "Streaming account sign-in reset. Connect the account again.";
        }

        private async void TwitchSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowTwitchViewerSettingsAsync();
        }

        private async Task ShowTwitchViewerSettingsAsync(string? helpText = null)
        {
            var channelBox = new TextBox
            {
                Header = "Twitch channel login",
                PlaceholderText = "yourchannel",
                Text = TwitchViewerCountService.ChannelLogin
            };
            var clientIdBox = new TextBox
            {
                Header = "Client ID",
                PlaceholderText = "Twitch app Client ID",
                Text = TwitchViewerCountService.ClientId
            };
            var accessTokenBox = new PasswordBox
            {
                Header = "Access token",
                PlaceholderText = "Paste OAuth access token"
            };
            accessTokenBox.Password = TwitchViewerCountService.AccessToken;

            var testStatus = new TextBlock
            {
                Text = helpText ?? "Use this if Connect Twitch is blocked by SMS. The token must belong to the same Client ID.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            };

            var testButton = new Button
            {
                Content = "Test settings",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            testButton.Click += async (_, _) =>
            {
                SaveManualTwitchViewerSettings(channelBox.Text, clientIdBox.Text, accessTokenBox.Password);
                testStatus.Text = "Testing Twitch viewer settings...";
                var snapshot = await TwitchViewerCountService.Instance.RefreshAsync();
                testStatus.Text = snapshot.ViewerCount.HasValue
                    ? $"{snapshot.Status}: {snapshot.ViewerCount.Value} viewer(s)"
                    : snapshot.Status;
                UpdateTwitchViewerStatus();
            };

            var panel = new StackPanel
            {
                Spacing = 10
            };
            panel.Children.Add(channelBox);
            panel.Children.Add(clientIdBox);
            panel.Children.Add(accessTokenBox);
            panel.Children.Add(testButton);
            panel.Children.Add(testStatus);

            var dialog = new ContentDialog
            {
                Title = "Twitch viewer settings",
                Content = panel,
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Cancel",
                CloseButtonText = "Clear",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                SaveManualTwitchViewerSettings(channelBox.Text, clientIdBox.Text, accessTokenBox.Password);
                UpdateTwitchViewerStatus();
                StatusText.Text = "Twitch viewer settings saved.";
            }
            else if (result == ContentDialogResult.None)
            {
                TwitchViewerCountService.Instance.Disconnect();
                TwitchViewerCountService.ClientId = string.Empty;
                TwitchViewerOverlayWindow.CloseSingleton();
                UpdateTwitchViewerStatus();
                StatusText.Text = "Twitch viewer settings cleared.";
            }
        }

        private static void SaveManualTwitchViewerSettings(string channelLogin, string clientId, string accessToken)
        {
            TwitchViewerCountService.ChannelLogin = channelLogin.Trim().TrimStart('@');
            TwitchViewerCountService.ClientId = clientId.Trim();
            TwitchViewerCountService.AccessToken = accessToken.Trim();
        }

        private void UpdateTwitchViewerStatus()
        {
            if (TwitchViewerStatusText is null)
                return;

            if (!TwitchViewerCountService.HasConfiguredClientId)
            {
                TwitchViewerStatusText.Text = "Account connect is not configured in this build.";
                return;
            }

            var channel = TwitchViewerCountService.ChannelLogin;
            TwitchViewerStatusText.Text = string.IsNullOrWhiteSpace(channel)
                ? "No streaming account connected"
                : $"Connected account: {channel}";
        }

        private async Task StartObsCapturePreviewAsync()
        {
            try
            {
                _obsCaptureService.SetQuality(GetSelectedQualityPreset());
                ApplySelectedEncoderFamily();
                _obsCaptureService.SetBitrateOverride(GetSelectedBitrateKbps() * 1000);
                _obsCaptureService.SetAdaptiveLatencyMode(false);
                _obsCaptureService.EnablePreviewFrames = true;
                _obsCaptureService.PublishPreviewOnlyFrames = true;
                _obsCaptureService.PrioritizeStreamingPerformance = false;
                _obsCaptureService.PreferredVideoCodec = ScreenShareVideoCodec.H264;
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
                    StatusText.Text = $"{GetSelectedEncoderFamilyText()} / {GetSelectedQualityText()} capture preview started.";
                }
                else
                {
                    CaptureBackendText.Text = "No source selected";
                    StatusText.Text = _obsCaptureService.LastFailureMessage ?? "No capture source selected.";
                }
            }
            catch (Exception ex)
            {
                CaptureBackendText.Text = "Capture unavailable";
                StatusText.Text = $"Capture could not start: {ex.Message}";
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

                var isPreviewOnlyFrame = string.Equals(frame.Codec, "preview", StringComparison.OrdinalIgnoreCase);
                var previewEncodeFps = _obsCaptureService.EncodedFps > 0
                    ? _obsCaptureService.EncodedFps
                    : _obsCaptureService.CaptureFps;
                CaptureBackendText.Text = _obsCaptureService.EncoderMode;
                PreviewSubtitleText.Text = $"{frame.Width} x {frame.Height} {(isPreviewOnlyFrame ? "capture preview" : frame.Codec.ToUpperInvariant())} - live preview {frameAgeMilliseconds:0}ms behind";
                var previewProfile = $"{GetSelectedEncoderFamilyText()} / {frame.QualityName}";
                CanvasValueText.Text = $"{previewProfile} / {_obsCaptureService.CurrentTargetFps} FPS / {_obsCaptureService.CurrentBitrate / 1000}k";
                SceneTransitionValueText.Text = $"Preview {previewEncodeFps:0.0} fps";

                if (!_streamingService.IsStreaming)
                {
                    PanelCaptureFpsText.Text = $"{_obsCaptureService.CaptureFps:0.0}";
                    PanelEncodeFpsText.Text = $"{previewEncodeFps:0.0}";
                    PanelSendFpsText.Text = "0.0";
                    PanelBitrateText.Text = $"{_obsCaptureService.CurrentBitrate / 1000}k";
                    PanelEncodeMsText.Text = $"{_obsCaptureService.LastEncodeMilliseconds:0.0} ms";
                    PanelStreamTimeText.Text = "Preview";
                    PanelQualityText.Text = $"{previewProfile} / {_obsCaptureService.CurrentTargetFps} FPS";
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
                if (!_streamingService.IsStreaming)
                {
                    SelectCaptureSourceButton.IsEnabled = true;
                    SelectCaptureSourceButton.Content = _obsCaptureService.IsRunning
                        ? "Change Window / Screen"
                        : "Select Window / Screen";
                }
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

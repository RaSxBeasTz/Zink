using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;

using global::Windows.Media.Core;
using global::Windows.Media.Playback;

using WStorage = global::Windows.Storage;
using WPickers = global::Windows.Storage.Pickers;
using WSystem = global::Windows.System;
using WAppModel = global::Windows.ApplicationModel;
using WDeployment = global::Windows.Management.Deployment;

using WinRT.Interop;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;
using Zink.Services;
using VlcCore = LibVLCSharp.Shared.Core;
using VlcLibVLC = LibVLCSharp.Shared.LibVLC;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Zink
{
    public sealed partial class SecondsToTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is double seconds)
                {
                    var t = TimeSpan.FromSeconds(seconds);
                    return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
                }
            }
            catch { }
            return "00:00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => 0d;
    }

    public sealed partial class VideoPlayerPage : Page
    {
        private bool isFullScreen;
        private DispatcherTimer hideControlsTimer;
        private DispatcherTimer _videoBadgeHideTimer;
        private DispatcherTimer _nvidiaOverlaySuppressTimer;

        private WStorage.StorageFile _currentFile;

        private DispatcherTimer _positionTimer;
        private bool _isUserSeeking = false;
        private bool _ignoreSliderChange = false;
        private bool _mediaReadyForSeek = false;
        private DateTime _lastLiveSeekUtc = DateTime.MinValue;

        private DispatcherTimer _discordPresenceTimer;
        private bool _userPausedDiscordPresence = false;
        private bool _suppressDiscordPresenceRefresh = false;
        private int _lastDiscordPushedSecond = -1;
        private bool _forceStartFromBeginningOnNextLoad = false;
        private DateTime _discordPlaybackStartUtc = DateTime.MinValue;
        private TimeSpan _discordPlaybackDuration = TimeSpan.Zero;
        private bool _discordClockReady = false;
        private DateTime _lastDiscordPresencePushUtc = DateTime.MinValue;
        private const double DiscordPresencePushIntervalSeconds = 4.2;

        private bool _nativeSubtitlesEnabled = false;
        private MediaPlaybackItem _currentPlaybackItem;
        private WStorage.StorageFile _currentSubtitleFile;
        private readonly List<SubtitleCue> _subtitleCues = new List<SubtitleCue>();

        private const string VIDEO_POS_PREFIX = "Zink_VideoPos_";
        private const string VIDEO_CODEC_STATE_PREFIX = "Zink_VideoCodecState_";
        private double _pendingResumeSeconds = 0;
        private double _lastSavedPosSeconds = -1;
        private DateTime _lastPosSaveUtc = DateTime.MinValue;

        private const string DASH_LastKind = "HomeDash_LastKind";
        private const string DASH_LastPath = "HomeDash_LastPath";
        private const string DASH_LastTitle = "HomeDash_LastTitle";
        private const string DASH_LastSubtitle = "HomeDash_LastSubtitle";

        private bool _codecPromptAlreadyShownForCurrentFile = false;
        private string _lastCodecPromptedPath = null;
        private bool _videoSupportPromptAlreadyShownForCurrentFile = false;
        private string _lastVideoSupportPromptedPath = null;

        private bool _waitingForCodecInstallReturn = false;
        private bool _isHandlingCodecReturnReload = false;
        private bool _suppressCodecPromptOnce = false;
        private string _pendingReloadVideoPath = null;
        private double _pendingReloadResumeSeconds = 0;

        private const string CodecInstallerFolderName = "CodecInstallers";
        private const string ToolsFolderName = "Tools";
        private const string FfprobeExeName = "ffprobe.exe";
        private const string FfmpegExeName = "ffmpeg.exe";
        private const string AudioFallbackFolderName = "AudioFallback";
        private const string AudioTranslationFolderName = "AudioTranslations";
        private const int AudioTranslationSegmentSeconds = 600;
        private const string OpenAiAudioTranslationsEndpoint = "https://api.openai.com/v1/audio/translations";
        private const string OpenAiAudioSpeechEndpoint = "https://api.openai.com/v1/audio/speech";
        private const int AudioDubMaxGroupCharacters = 700;

        private const string DolbyDigitalPlusPrefix = "DolbyLaboratories.DolbyDigitalPlusDecoderOEM_";
        private const string DolbyAC4Prefix = "DolbyLaboratories.DolbyAC4DecoderOEM_";
        private const string MicrosoftHevcVideoExtensionPrefix = "Microsoft.HEVCVideoExtension";
        private const string MicrosoftHevcVideoExtensionsPrefix = "Microsoft.HEVCVideoExtensions";
        private const string MicrosoftHevcDeviceExtensionPrefix = "Microsoft.HEVCVideoExtensionsFromDeviceManufacturer";
        private const string DolbyAccessPrefix = "DolbyLaboratories.DolbyAccess";
        private const string DolbyVisionAccessPrefix = "DolbyLaboratories.DolbyVisionAccess";

        private const string CodecStateNotNeeded = "not_needed";
        private const string CodecStateInstalledDdp = "installed_ddp";
        private const string CodecStateInstalledAc4 = "installed_ac4";
        private const string CodecStatePendingDdp = "pending_ddp";
        private const string CodecStatePendingAc4 = "pending_ac4";

        private const string VIDEO_VOLUME_KEY = "Zink_VideoPlayer_Volume";
        private const string VIDEO_SURROUND_MODE_KEY = "Zink_VideoPlayer_SurroundMode";
        private double _lastNonZeroVolume = 1.0;
        private bool _volumeUiReady = false;
        private IReadOnlyList<AudioStreamInfo> _detectedAudioStreams = Array.Empty<AudioStreamInfo>();
        private AudioStreamInfo _selectedAudioStream;
        private string _audioInfoStatus = "No audio information detected yet.";
        private VideoMetadataInfo _detectedVideoMetadata = VideoMetadataInfo.Empty;
        private string _videoInfoStatus = "No video metadata detected yet.";
        private string _preferredSurroundMode = SurroundModeAuto;
        private MediaPlaybackState? _lastLoggedPlaybackState = null;
        private WStorage.StorageFile _effectivePlaybackFile;
        private bool _usingAudioFallbackFile = false;
        private bool _isTranslatingAudio = false;
        private VlcLibVLC _libVLC;
        private VlcMediaPlayer _vlcMediaPlayer;
        private VlcMedia _vlcAudioMedia;
        private long _vlcAudioBaseOffsetMilliseconds = 0;
        private bool _useCompatibilityPlaybackEngine = false;
        private bool _vlcReadyForSeek = false;
        private bool _vlcPlaybackEnded = false;
        private DateTime _lastCompatibilityAudioSyncUtc = DateTime.MinValue;
        private DateTime _lastCompatibilityAudioRestartUtc = DateTime.MinValue;
        private bool _compatibilityAudioRestartInProgress = false;

        private Flyout _soundFlyout;
        private Slider _flyoutVolumeSlider;
        private TextBlock _flyoutVolumeText;
        private Button _flyoutMuteButton;
        private ComboBox _surroundModeComboBox;
        private ComboBox _audioTrackComboBox;
        private TextBlock _surroundModeStatusText;
        private bool _audioTrackUiReady = false;

        private const string SurroundModeAuto = "auto";
        private const string SurroundModeAtmos = "atmos";
        private const string SurroundMode21 = "2.1";
        private const string SurroundMode51 = "5.1";
        private const string SurroundMode71 = "7.1";
        private const string SurroundMode72 = "7.2";
        private const string SurroundMode512 = "5.1.2";
        private const string SurroundMode712 = "7.1.2";
        private const string SurroundMode714 = "7.1.4";
        private const string SurroundMode914 = "9.1.4";
        private const string SurroundMode916 = "9.1.6";
        private const string SurroundMode24110 = "24.1.10";
        private static readonly string[] SupportedVideoFileExtensions =
        {
            ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv",
            ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".3g2", ".ogv",
            ".vob", ".divx", ".asf"
        };

        private sealed class AudioStreamInfo
        {
            public int StreamIndex { get; set; }
            public int AudioTrackNumber { get; set; }
            public string Codec { get; set; }
            public string CodecLongName { get; set; }
            public string Profile { get; set; }
            public int Channels { get; set; }
            public string ChannelLayout { get; set; }
            public string Language { get; set; }
            public string Title { get; set; }
            public bool IsDolbyAtmos { get; set; }
            public string SurroundLayout { get; set; }

            public override string ToString() => FormatAudioStreamSummary(this);
        }

        private sealed class VideoMetadataInfo
        {
            public static VideoMetadataInfo Empty => new VideoMetadataInfo
            {
                DynamicRange = "SDR",
                Badge = "SDR",
                PlaybackPath = "Waiting for video metadata."
            };

            public string FileName { get; set; }
            public string Codec { get; set; }
            public string CodecLongName { get; set; }
            public string Profile { get; set; }
            public bool IsAvc { get; set; }
            public bool IsHevc { get; set; }
            public string PixelFormat { get; set; }
            public int BitDepth { get; set; }
            public string ColorSpace { get; set; }
            public string ColorTransfer { get; set; }
            public string ColorPrimaries { get; set; }
            public string ChromaSubsampling { get; set; }
            public string DynamicRange { get; set; }
            public string Badge { get; set; }
            public bool IsHdr10 { get; set; }
            public bool IsHdr10Plus { get; set; }
            public bool IsDolbyVision { get; set; }
            public bool IsHlg { get; set; }
            public string DolbyVisionProfile { get; set; }
            public bool WindowsHdrEnabled { get; set; }
            public bool DisplayHdrSupported { get; set; }
            public string DisplayAdvancedColorKind { get; set; }
            public bool HevcExtensionInstalled { get; set; }
            public bool DolbyAccessInstalled { get; set; }
            public bool DolbyVisionAccessInstalled { get; set; }
            public string NativeCodecPath { get; set; }
            public string PlaybackPath { get; set; }
            public string DetectionSource { get; set; }
            public string Notes { get; set; }
        }

        private sealed class SurroundModeOption
        {
            public string Mode { get; set; }
            public string Label { get; set; }

            public override string ToString() => Label;
        }

        private sealed class SubtitleCue
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public string Text { get; set; }
        }

        public VideoPlayerPage()
        {
            InitializeComponent();

            InitializeSoundFlyout();
            _volumeUiReady = true;

            hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            hideControlsTimer.Tick += (_, _) =>
            {
                ControlPanel.Visibility = Visibility.Collapsed;
                hideControlsTimer.Stop();
            };
            hideControlsTimer.Start();

            _videoBadgeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _videoBadgeHideTimer.Tick += (_, _) =>
            {
                try
                {
                    VideoFormatBadge.Visibility = Visibility.Collapsed;
                    _videoBadgeHideTimer.Stop();
                }
                catch { }
            };

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += (_, _) => UpdateSeekUI();
            _positionTimer.Start();

            _nvidiaOverlaySuppressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _nvidiaOverlaySuppressTimer.Tick += (_, _) => SuppressNvidiaOverlayWindows();

            _discordPresenceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _discordPresenceTimer.Tick += (_, _) =>
            {
                try
                {
                    if (_useCompatibilityPlaybackEngine)
                    {
                        if (!(_vlcMediaPlayer?.IsPlaying ?? false))
                            return;

                        if (!_discordClockReady)
                            SyncDiscordPlaybackClockFromSession(force: true);

                        var vlcElapsed = GetDiscordLiveElapsed();
                        int vlcCurrentSecond = (int)Math.Floor(vlcElapsed.TotalSeconds);

                        if (vlcCurrentSecond != _lastDiscordPushedSecond)
                            _lastDiscordPushedSecond = vlcCurrentSecond;

                        var vlcNowUtc = DateTime.UtcNow;
                        if ((vlcNowUtc - _lastDiscordPresencePushUtc).TotalSeconds >= DiscordPresencePushIntervalSeconds)
                            RefreshDiscordVideoPresence(forcePlaying: true, forcePush: false);

                        return;
                    }

                    var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                    if (session == null)
                        return;

                    if (session.PlaybackState != MediaPlaybackState.Playing)
                        return;

                    if (!_discordClockReady)
                        SyncDiscordPlaybackClockFromSession(force: true);

                    var elapsed = GetDiscordLiveElapsed();
                    int currentSecond = (int)Math.Floor(elapsed.TotalSeconds);

                    if (currentSecond != _lastDiscordPushedSecond)
                    {
                        _lastDiscordPushedSecond = currentSecond;
                    }

                    var nowUtc = DateTime.UtcNow;
                    if ((nowUtc - _lastDiscordPresencePushUtc).TotalSeconds >= DiscordPresencePushIntervalSeconds)
                    {
                        RefreshDiscordVideoPresence(forcePlaying: true, forcePush: false);
                    }
                }
                catch { }
            };

            mediaPlayerElement.MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            mediaPlayerElement.MediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
            mediaPlayerElement.MediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            mediaPlayerElement.MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
            SeekSlider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SeekSlider_PointerMoved), true);
            SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
            SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerCaptureLost), true);

            try
            {
                if (mediaPlayerElement?.MediaPlayer != null)
                {
                    ApplySavedVolume();
                }
            }
            catch { }

            try
            {
                if (App.MainWindow != null)
                {
                    App.MainWindow.Activated += MainWindow_Activated;
                }
            }
            catch { }
        }

        private void InitializeSoundFlyout()
        {
            _preferredSurroundMode = LoadSavedSurroundMode();

            _flyoutVolumeText = new TextBlock
            {
                Text = "100%",
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _flyoutVolumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                StepFrequency = 1,
                SmallChange = 1,
                LargeChange = 10,
                Width = 220
            };
            _flyoutVolumeSlider.ValueChanged += FlyoutVolumeSlider_ValueChanged;

            _flyoutMuteButton = new Button
            {
                Content = "Mute",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _flyoutMuteButton.Click += FlyoutMuteButton_Click;

            _surroundModeStatusText = new TextBlock
            {
                Text = "Surround mode: Auto",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _surroundModeComboBox = new ComboBox
            {
                Header = "Surround",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new List<SurroundModeOption>
                {
                    new SurroundModeOption { Mode = SurroundModeAuto, Label = "Auto / best available" },
                    new SurroundModeOption { Mode = SurroundModeAtmos, Label = "Dolby Atmos" },
                    new SurroundModeOption { Mode = SurroundMode21, Label = "Surround 2.1" },
                    new SurroundModeOption { Mode = SurroundMode51, Label = "Surround 5.1" },
                    new SurroundModeOption { Mode = SurroundMode71, Label = "Surround 7.1" },
                    new SurroundModeOption { Mode = SurroundMode72, Label = "Surround 7.2" },
                    new SurroundModeOption { Mode = SurroundMode512, Label = "Dolby Atmos 5.1.2" },
                    new SurroundModeOption { Mode = SurroundMode712, Label = "Dolby Atmos 7.1.2" },
                    new SurroundModeOption { Mode = SurroundMode714, Label = "Surround 7.1.4 / Atmos" },
                    new SurroundModeOption { Mode = SurroundMode914, Label = "Surround 9.1.4 / Atmos" },
                    new SurroundModeOption { Mode = SurroundMode916, Label = "Surround 9.1.6 / Atmos" },
                    new SurroundModeOption { Mode = SurroundMode24110, Label = "Dolby Atmos 24.1.10" }
                },
                SelectedValuePath = "Mode",
                SelectedValue = _preferredSurroundMode
            };
            _surroundModeComboBox.SelectionChanged += SurroundModeComboBox_SelectionChanged;

            _audioTrackComboBox = new ComboBox
            {
                Header = "Audio track",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Detecting tracks..."
            };
            _audioTrackComboBox.SelectionChanged += AudioTrackComboBox_SelectionChanged;

            var titleText = new TextBlock
            {
                Text = "Sound",
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var panel = new StackPanel
            {
                Spacing = 10,
                Width = 240
            };

            panel.Children.Add(titleText);
            panel.Children.Add(_flyoutVolumeSlider);
            panel.Children.Add(_flyoutVolumeText);
            panel.Children.Add(_flyoutMuteButton);
            panel.Children.Add(_surroundModeComboBox);
            panel.Children.Add(_audioTrackComboBox);
            panel.Children.Add(_surroundModeStatusText);

            _soundFlyout = new Flyout
            {
                Content = panel,
                Placement = FlyoutPlacementMode.Top
            };

            if (SoundButton != null)
            {
                SoundButton.Flyout = _soundFlyout;
            }
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            try
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated)
                    return;

                if (!_waitingForCodecInstallReturn)
                    return;

                if (_isHandlingCodecReturnReload)
                    return;

                _isHandlingCodecReturnReload = true;

                try
                {
                    await AutoReloadVideoAfterCodecInstallAsync();
                }
                finally
                {
                    _isHandlingCodecReturnReload = false;
                }
            }
            catch { }
        }

        private void SaveDashboardResumeCard_Video()
        {
            try
            {
                var path = _currentFile?.Path ?? "";
                if (string.IsNullOrWhiteSpace(path)) return;

                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastKind] = "video";
                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastPath] = path;
                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastTitle] = _currentFile?.Name ?? Path.GetFileName(path);
                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastSubtitle] = "Video";
            }
            catch { }
        }

        private void ForceSaveResumePositionNow_Video()
        {
            try
            {
                if (_currentFile == null || string.IsNullOrWhiteSpace(_currentFile.Path)) return;

                if (_useCompatibilityPlaybackEngine)
                {
                    var durationSeconds = Math.Max(0, (_vlcMediaPlayer?.Length ?? 0) / 1000.0);
                    var vlcPos = GetCurrentPlaybackPositionSeconds();
                    if (durationSeconds <= 0 || vlcPos < 1 || (durationSeconds - vlcPos) < 2.0)
                        return;

                    SavePositionSeconds(_currentFile.Path, vlcPos);
                    return;
                }

                var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null) return;

                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                var pos = session.Position.TotalSeconds;
                if (pos < 1) return;

                if ((dur.TotalSeconds - pos) < 2.0)
                    return;

                SavePositionSeconds(_currentFile.Path, pos);
            }
            catch { }
        }

        private double GetCurrentPlaybackPositionSeconds()
        {
            try
            {
                if (_useCompatibilityPlaybackEngine)
                {
                    var vlcSeconds = (_vlcMediaPlayer?.Time ?? 0) / 1000.0;
                    return vlcSeconds < 0 ? 0 : vlcSeconds;
                }

                var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null) return 0;

                var seconds = session.Position.TotalSeconds;
                if (seconds < 0) return 0;

                return seconds;
            }
            catch
            {
                return 0;
            }
        }

        private void ResetDiscordSecondPushTracking()
        {
            try
            {
                _lastDiscordPushedSecond = -1;
            }
            catch { }
        }

        private void ResetDiscordPlaybackClock()
        {
            try
            {
                _discordPlaybackStartUtc = DateTime.MinValue;
                _discordPlaybackDuration = TimeSpan.Zero;
                _discordClockReady = false;
                _lastDiscordPushedSecond = -1;
                _lastDiscordPresencePushUtc = DateTime.MinValue;
            }
            catch { }
        }

        private void SyncDiscordPlaybackClockFromSession(bool force = false)
        {
            try
            {
                if (_useCompatibilityPlaybackEngine)
                {
                    var vlcDuration = TimeSpan.FromMilliseconds(Math.Max(0, _vlcMediaPlayer?.Length ?? 0));
                    if (vlcDuration.TotalSeconds <= 0)
                        return;

                    var vlcPosition = TimeSpan.FromSeconds(GetCurrentPlaybackPositionSeconds());
                    if (vlcPosition > vlcDuration)
                        vlcPosition = vlcDuration;

                    if (force || !_discordClockReady)
                    {
                        _discordPlaybackDuration = vlcDuration;
                        _discordPlaybackStartUtc = DateTime.UtcNow - vlcPosition;
                        _discordClockReady = true;
                    }
                    return;
                }

                var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null)
                    return;

                var duration = session.NaturalDuration;
                if (duration.TotalSeconds <= 0)
                    return;

                var position = session.Position;
                if (position < TimeSpan.Zero)
                    position = TimeSpan.Zero;
                if (position > duration)
                    position = duration;

                if (force || !_discordClockReady)
                {
                    _discordPlaybackDuration = duration;
                    _discordPlaybackStartUtc = DateTime.UtcNow - position;
                    _discordClockReady = true;
                    return;
                }

                var calculatedElapsed = DateTime.UtcNow - _discordPlaybackStartUtc;
                var drift = Math.Abs((calculatedElapsed - position).TotalSeconds);

                if (drift >= 1.5)
                {
                    _discordPlaybackDuration = duration;
                    _discordPlaybackStartUtc = DateTime.UtcNow - position;
                }
            }
            catch { }
        }

        private TimeSpan GetDiscordLiveElapsed()
        {
            try
            {
                if (!_discordClockReady)
                    return TimeSpan.Zero;

                var elapsed = DateTime.UtcNow - _discordPlaybackStartUtc;

                if (elapsed < TimeSpan.Zero)
                    elapsed = TimeSpan.Zero;

                if (_discordPlaybackDuration > TimeSpan.Zero && elapsed > _discordPlaybackDuration)
                    elapsed = _discordPlaybackDuration;

                return elapsed;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private void Page_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Visible;
            hideControlsTimer.Stop();
            hideControlsTimer.Start();
        }

        private void Play_Click(object s, RoutedEventArgs e)
        {
            _userPausedDiscordPresence = false;
            if (_useCompatibilityPlaybackEngine)
            {
                if (_vlcPlaybackEnded)
                {
                    try { _vlcMediaPlayer.Time = 0; } catch { }
                    _vlcPlaybackEnded = false;
                }

                mediaPlayerElement.MediaPlayer.Play();
                _vlcMediaPlayer?.Play();
                ScheduleCompatibilityAudioResync(250);
            }
            else
            {
                mediaPlayerElement.MediaPlayer.Play();
            }
            TryPushNowPlaying(true);

            SyncDiscordPlaybackClockFromSession(force: true);
            ResetDiscordSecondPushTracking();

            try { _discordPresenceTimer?.Start(); } catch { }

            RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
        }

        private void Pause_Click(object s, RoutedEventArgs e)
        {
            _userPausedDiscordPresence = true;
            if (_useCompatibilityPlaybackEngine)
            {
                mediaPlayerElement.MediaPlayer.Pause();
                _vlcMediaPlayer?.Pause();
            }
            else
                mediaPlayerElement.MediaPlayer.Pause();
            TryPushNowPlaying(false);

            SyncDiscordPlaybackClockFromSession(force: true);

            try { _discordPresenceTimer?.Stop(); } catch { }

            RefreshDiscordPausedPresence(forcePush: true);
        }

        private void Rewind_Click(object s, RoutedEventArgs e)
        {
            if (_useCompatibilityPlaybackEngine)
            {
                var compatSession = mediaPlayerElement.MediaPlayer.PlaybackSession;
                if (compatSession.CanSeek)
                {
                    _userPausedDiscordPresence = false;
                    compatSession.Position -= TimeSpan.FromSeconds(10);
                    if (_vlcMediaPlayer != null)
                        _vlcMediaPlayer.Time = (long)Math.Max(0, compatSession.Position.TotalMilliseconds);

                    SyncDiscordPlaybackClockFromSession(force: true);
                    ResetDiscordSecondPushTracking();
                    RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                    ScheduleCompatibilityAudioResync(250);
                    ScheduleCompatibilityAudioResync(900, forceRestart: true);
                }
                return;
            }

            var session = mediaPlayerElement.MediaPlayer.PlaybackSession;
            if (session.CanSeek)
            {
                _userPausedDiscordPresence = false;
                session.Position -= TimeSpan.FromSeconds(10);

                SyncDiscordPlaybackClockFromSession(force: true);
                ResetDiscordSecondPushTracking();
                RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
            }
        }

        private void Forward_Click(object s, RoutedEventArgs e)
        {
            if (_useCompatibilityPlaybackEngine)
            {
                var compatSession = mediaPlayerElement.MediaPlayer.PlaybackSession;
                if (compatSession.CanSeek)
                {
                    _userPausedDiscordPresence = false;
                    compatSession.Position += TimeSpan.FromSeconds(10);
                    if (_vlcMediaPlayer != null)
                        _vlcMediaPlayer.Time = (long)Math.Max(0, compatSession.Position.TotalMilliseconds);

                    SyncDiscordPlaybackClockFromSession(force: true);
                    ResetDiscordSecondPushTracking();
                    RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                    ScheduleCompatibilityAudioResync(250);
                    ScheduleCompatibilityAudioResync(900, forceRestart: true);
                }
                return;
            }

            var session = mediaPlayerElement.MediaPlayer.PlaybackSession;
            if (session.CanSeek)
            {
                _userPausedDiscordPresence = false;
                session.Position += TimeSpan.FromSeconds(10);

                SyncDiscordPlaybackClockFromSession(force: true);
                ResetDiscordSecondPushTracking();
                RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
            }
        }

        private void Browse_Click(object s, RoutedEventArgs e) => PickVideoFile();

        private async void PickVideoFile()
        {
            var picker = new WPickers.FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            picker.SuggestedStartLocation = WPickers.PickerLocationId.VideosLibrary;

            foreach (var extension in SupportedVideoFileExtensions)
                picker.FileTypeFilter.Add(extension);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _forceStartFromBeginningOnNextLoad = true;
                await LoadAndPlayAsync(file);
            }
        }

        private async System.Threading.Tasks.Task LoadAndPlayAsync(WStorage.StorageFile file)
        {
            DiagnosticLogService.EnsureLogFile("Video playback diagnostics requested.");
            _currentFile = file;
            _codecPromptAlreadyShownForCurrentFile = false;
            _lastCodecPromptedPath = null;
            _videoSupportPromptAlreadyShownForCurrentFile = false;
            _lastVideoSupportPromptedPath = null;
            _userPausedDiscordPresence = false;
            _detectedAudioStreams = Array.Empty<AudioStreamInfo>();
            _selectedAudioStream = null;
            _audioInfoStatus = "Detecting audio format...";
            _detectedVideoMetadata = VideoMetadataInfo.Empty;
            _videoInfoStatus = "Detecting video metadata...";
            _lastLoggedPlaybackState = null;
            _effectivePlaybackFile = file;
            _usingAudioFallbackFile = false;
            UpdateVideoMetadataUI(_detectedVideoMetadata, showBadge: false);
            ResetDiscordPlaybackClock();
            WriteVideoAudioDiagnostics("Load requested for " + FormatVideoFileForLog(file));

            if (_suppressCodecPromptOnce)
            {
                _suppressCodecPromptOnce = false;
            }
            else
            {
                await PromptForMissingCodecIfNeededAsync(file);
            }

            await DetectAndPrepareAudioInfoAsync(file);
            await DetectAndPrepareVideoMetadataAsync(file);
            WriteVideoAudioDiagnostics("Probe complete.\n" + BuildAudioDiagnosticsSnapshot());
            await ConfigureDirectAudioSupportForCurrentFileAsync(file);

            if (_forceStartFromBeginningOnNextLoad)
            {
                _pendingResumeSeconds = 0;
                try { SavePositionSeconds(file.Path, 0); } catch { }
                _forceStartFromBeginningOnNextLoad = false;
            }
            else
            {
                if (_pendingResumeSeconds <= 0)
                    _pendingResumeSeconds = GetSavedPositionSeconds(file.Path);
            }

            _mediaReadyForSeek = false;
            SeekSlider.IsEnabled = false;
            _ignoreSliderChange = true;
            SeekSlider.Minimum = 0;
            SeekSlider.Maximum = 1;
            SeekSlider.Value = 0;
            _ignoreSliderChange = false;
            CurrentTimeText.Text = "00:00";
            TotalTimeText.Text = "00:00";
            await LoadSidecarSubtitlesForCurrentVideoAsync(file);

            _currentPlaybackItem = await BuildPlaybackItemWithNativeSubtitlesAsync(_currentFile);
            mediaPlayerElement.Source = _currentPlaybackItem;
            LogVideoPlaybackPath("MediaPlaybackItem assigned to WinUI MediaPlayerElement / Windows Media Foundation path.");
            WriteVideoAudioDiagnostics("Playback item assigned.\n" + BuildAudioDiagnosticsSnapshot());

            ApplyNativeSubtitleTrackState(_nativeSubtitlesEnabled);
            UpdateSubtitlesButtonState();
            UpdateSubtitleOverlay(TimeSpan.Zero);

            try
            {
                ApplySavedVolume();
            }
            catch { }

            if (_useCompatibilityPlaybackEngine)
                mediaPlayerElement.MediaPlayer.Volume = 0;

            mediaPlayerElement.MediaPlayer.Play();
            WriteVideoAudioDiagnostics("Play invoked.\n" + BuildPlaybackSettingsSnapshot(mediaPlayerElement.MediaPlayer));

            if (_useCompatibilityPlaybackEngine)
                await LoadAndPlayWithCompatibilityEngineAsync(file);

            SyncDiscordPlaybackClockFromSession(force: true);

            SaveDashboardResumeCard_Video();

            try
            {
                ActivityHub.Record(
                    ActivityHub.ActivityKind.Video,
                    title: file?.Name ?? "",
                    subtitle: "Video opened",
                    payload: file?.Path ?? "",
                    listenedSeconds: 0
                );
            }
            catch { }

            TryPushNowPlaying(true);

            try { _discordPresenceTimer?.Start(); } catch { }
            RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
        }

        private async System.Threading.Tasks.Task PromptForMissingCodecIfNeededAsync(WStorage.StorageFile file)
        {
            try
            {
                if (file == null || XamlRoot == null)
                    return;

                var path = file.Path ?? "";
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (_codecPromptAlreadyShownForCurrentFile &&
                    string.Equals(_lastCodecPromptedPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                var savedState = GetSavedCodecState(path);

                if (string.Equals(savedState, CodecStateNotNeeded, StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.Equals(savedState, CodecStateInstalledDdp, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyDigitalPlusPrefix))
                        return;

                    ClearSavedCodecState(path);
                }

                if (string.Equals(savedState, CodecStateInstalledAc4, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyAC4Prefix))
                        return;

                    ClearSavedCodecState(path);
                }

                if (string.Equals(savedState, CodecStatePendingDdp, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyDigitalPlusPrefix))
                    {
                        SaveCodecState(path, CodecStateInstalledDdp);
                    }

                    return;
                }

                if (string.Equals(savedState, CodecStatePendingAc4, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyAC4Prefix))
                    {
                        SaveCodecState(path, CodecStateInstalledAc4);
                    }

                    return;
                }

                var codec = await DetectPrimaryAudioCodecAsync(file);

                if (string.IsNullOrWhiteSpace(codec))
                {
                    SaveCodecState(path, CodecStateNotNeeded);
                    return;
                }

                codec = codec.Trim().ToLowerInvariant();

                string friendlyCodec = null;
                string installerPrefix = null;
                string installerName = null;
                string pendingState = null;
                string installedState = null;

                switch (codec)
                {
                    case "eac3":
                    case "ac3":
                        friendlyCodec = codec == "eac3"
                            ? "EAC3 / Dolby Digital Plus"
                            : "AC3 / Dolby Digital";
                        installerPrefix = DolbyDigitalPlusPrefix;
                        installerName = "Dolby Digital Plus Decoder";
                        pendingState = CodecStatePendingDdp;
                        installedState = CodecStateInstalledDdp;
                        break;

                    case "ac4":
                        friendlyCodec = "AC4 / Dolby AC-4";
                        installerPrefix = DolbyAC4Prefix;
                        installerName = "Dolby AC-4 Decoder";
                        pendingState = CodecStatePendingAc4;
                        installedState = CodecStateInstalledAc4;
                        break;

                    default:
                        SaveCodecState(path, CodecStateNotNeeded);
                        return;
                }

                if (await IsCodecExtensionInstalledAsync(installerPrefix))
                {
                    SaveCodecState(path, installedState);
                    return;
                }

                _codecPromptAlreadyShownForCurrentFile = true;
                _lastCodecPromptedPath = path;

                var dialog = new ContentDialog
                {
                    Title = "Missing audio codec support",
                    Content =
                        $"This video uses {friendlyCodec} audio.\n\n" +
                        "You can still play the video, but without installing the required extension you may have no sound.",
                    PrimaryButtonText = $"Install {installerName}",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();

                SaveCodecState(path, pendingState);

                if (result == ContentDialogResult.Primary)
                {
                    await TryLaunchCodecInstallerByPrefixAsync(installerPrefix, installerName);
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<string> DetectPrimaryAudioCodecAsync(WStorage.StorageFile file)
        {
            try
            {
                if (file == null || string.IsNullOrWhiteSpace(file.Path))
                    return null;

                var ffprobePath = await GetBundledFfprobePathAsync();
                if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
                    return null;

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-v");
                startInfo.ArgumentList.Add("error");
                startInfo.ArgumentList.Add("-select_streams");
                startInfo.ArgumentList.Add("a:0");
                startInfo.ArgumentList.Add("-show_entries");
                startInfo.ArgumentList.Add("stream=codec_name");
                startInfo.ArgumentList.Add("-of");
                startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
                startInfo.ArgumentList.Add(file.Path);

                using var process = new Process();
                process.StartInfo = startInfo;

                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var waitTask = process.WaitForExitAsync();
                var completed = await System.Threading.Tasks.Task.WhenAny(waitTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8)));
                if (completed != waitTask)
                {
                    try { process.Kill(true); } catch { }
                    return null;
                }

                string stdout = await stdoutTask;
                _ = await stderrTask;

                if (!string.IsNullOrWhiteSpace(stdout))
                    return stdout.Trim();
            }
            catch { }

            return null;
        }

        private async System.Threading.Tasks.Task DetectAndPrepareAudioInfoAsync(WStorage.StorageFile file)
        {
            try
            {
                var streams = await DetectAudioStreamsAsync(file);
                _detectedAudioStreams = streams;
                _selectedAudioStream = GetPreferredAudioStream(streams, _preferredSurroundMode);

                if (_selectedAudioStream != null)
                {
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} selected: {FormatAudioStreamSummary(_selectedAudioStream)}";
                }
                else
                {
                    _audioInfoStatus = "No Dolby Atmos, 2.1, 5.1, 7.1, 7.2, DTS, or newer surround audio track detected.";
                }

                UpdateSurroundModeStatusText();
                RefreshAudioTrackComboBox();
                WriteVideoAudioDiagnostics("Audio stream selection prepared.\n" + BuildAudioDiagnosticsSnapshot());
            }
            catch (Exception ex)
            {
                _detectedAudioStreams = Array.Empty<AudioStreamInfo>();
                _selectedAudioStream = null;
                _audioInfoStatus = "Audio information could not be detected for this film.";
                UpdateSurroundModeStatusText();
                RefreshAudioTrackComboBox();
                WriteVideoAudioDiagnostics("Audio stream detection failed: " + ex.Message);
            }
        }

        private void EnsureCompatibilityPlaybackEngine()
        {
            try
            {
                if (_vlcMediaPlayer != null)
                    return;

                VlcCore.Initialize();
                _libVLC = new VlcLibVLC("--no-video-title-show", "--quiet");
                _vlcMediaPlayer = new VlcMediaPlayer(_libVLC)
                {
                    EnableHardwareDecoding = true
                };

                _vlcMediaPlayer.LengthChanged += VlcMediaPlayer_LengthChanged;
                _vlcMediaPlayer.TimeChanged += VlcMediaPlayer_TimeChanged;
                _vlcMediaPlayer.Playing += VlcMediaPlayer_Playing;
                _vlcMediaPlayer.Paused += VlcMediaPlayer_Paused;
                _vlcMediaPlayer.EndReached += VlcMediaPlayer_EndReached;
                _vlcMediaPlayer.EncounteredError += VlcMediaPlayer_EncounteredError;

                WriteVideoAudioDiagnostics("Compatibility audio engine initialized.");
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Compatibility playback engine initialization failed: " + ex.Message);
            }
        }

        private void UseNativePlaybackSurface()
        {
            try
            {
                _useCompatibilityPlaybackEngine = false;
                _vlcReadyForSeek = false;
                _vlcPlaybackEnded = false;
                _lastCompatibilityAudioSyncUtc = DateTime.MinValue;
                _lastCompatibilityAudioRestartUtc = DateTime.MinValue;
                _compatibilityAudioRestartInProgress = false;
                try { _vlcMediaPlayer?.Stop(); } catch { }

                if (mediaPlayerElement != null)
                    mediaPlayerElement.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void UseCompatibilityPlaybackSurface()
        {
            try
            {
                _useCompatibilityPlaybackEngine = true;
                _vlcReadyForSeek = false;
                _vlcPlaybackEnded = false;
                _lastCompatibilityAudioSyncUtc = DateTime.MinValue;

                if (mediaPlayerElement != null)
                    mediaPlayerElement.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<IReadOnlyList<AudioStreamInfo>> DetectAudioStreamsAsync(WStorage.StorageFile file)
        {
            var results = new List<AudioStreamInfo>();

            try
            {
                if (file == null || string.IsNullOrWhiteSpace(file.Path))
                {
                    WriteVideoAudioDiagnostics("ffprobe audio stream detection skipped because the file path is blank.");
                    return results;
                }

                var ffprobePath = await GetBundledFfprobePathAsync();
                if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
                {
                    WriteVideoAudioDiagnostics("ffprobe audio stream detection skipped because ffprobe.exe was not found.");
                    return results;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-v");
                startInfo.ArgumentList.Add("error");
                startInfo.ArgumentList.Add("-select_streams");
                startInfo.ArgumentList.Add("a");
                startInfo.ArgumentList.Add("-show_entries");
                startInfo.ArgumentList.Add("stream=index,codec_name,codec_long_name,profile,channels,channel_layout:stream_tags=language,title");
                startInfo.ArgumentList.Add("-of");
                startInfo.ArgumentList.Add("json");
                startInfo.ArgumentList.Add(file.Path);

                using var process = new Process();
                process.StartInfo = startInfo;
                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var waitTask = process.WaitForExitAsync();
                var completed = await System.Threading.Tasks.Task.WhenAny(waitTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8)));
                if (completed != waitTask)
                {
                    try { process.Kill(true); } catch { }
                    WriteVideoAudioDiagnostics("ffprobe audio stream detection timed out for " + FormatVideoFileForLog(file));
                    return results;
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    WriteVideoAudioDiagnostics("ffprobe audio stream detection exited with " + process.ExitCode + ": " + TrimForLog(stderr, 800));
                }

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    WriteVideoAudioDiagnostics("ffprobe audio stream detection returned no JSON. stderr: " + TrimForLog(stderr, 800));
                    return results;
                }

                using var doc = JsonDocument.Parse(stdout);
                if (!doc.RootElement.TryGetProperty("streams", out var streams) ||
                    streams.ValueKind != JsonValueKind.Array)
                {
                    WriteVideoAudioDiagnostics("ffprobe audio stream JSON did not contain a streams array.");
                    return results;
                }

                int audioTrackNumber = 0;

                foreach (var stream in streams.EnumerateArray())
                {
                    string language = null;
                    string title = null;

                    if (stream.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("language", out var languageElement))
                            language = languageElement.GetString();

                        if (tags.TryGetProperty("title", out var titleElement))
                            title = titleElement.GetString();
                    }

                    results.Add(new AudioStreamInfo
                    {
                        StreamIndex = TryGetJsonInt(stream, "index"),
                        AudioTrackNumber = audioTrackNumber,
                        Codec = TryGetJsonString(stream, "codec_name"),
                        CodecLongName = TryGetJsonString(stream, "codec_long_name"),
                        Profile = TryGetJsonString(stream, "profile"),
                        Channels = TryGetJsonInt(stream, "channels"),
                        ChannelLayout = TryGetJsonString(stream, "channel_layout"),
                        Language = language,
                        Title = title,
                        IsDolbyAtmos = IsDolbyAtmosStream(
                            TryGetJsonString(stream, "codec_name"),
                            TryGetJsonString(stream, "codec_long_name"),
                            TryGetJsonString(stream, "profile"),
                            TryGetJsonString(stream, "channel_layout"),
                            title),
                        SurroundLayout = DetectSurroundLayout(
                            TryGetJsonInt(stream, "channels"),
                            TryGetJsonString(stream, "channel_layout"),
                            TryGetJsonString(stream, "codec_long_name"),
                            TryGetJsonString(stream, "profile"),
                            title)
                    });

                    audioTrackNumber++;
                }

                WriteVideoAudioDiagnostics("ffprobe detected " + results.Count + " audio stream(s).\n" + FormatAudioStreamsForLog(results));
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("ffprobe audio stream detection failed: " + ex.Message);
            }

            return results;
        }

        private async System.Threading.Tasks.Task DetectAndPrepareVideoMetadataAsync(WStorage.StorageFile file)
        {
            try
            {
                var metadata = await DetectVideoMetadataAsync(file);
                _detectedVideoMetadata = metadata ?? VideoMetadataInfo.Empty;
                await ApplyNativeVideoCodecCapabilityAsync(file, _detectedVideoMetadata);
                _videoInfoStatus = FormatVideoMetadataInfo(_detectedVideoMetadata);
                UpdateVideoMetadataUI(_detectedVideoMetadata);
                LogVideoPlaybackPath(_detectedVideoMetadata.PlaybackPath);
            }
            catch (Exception ex)
            {
                _detectedVideoMetadata = VideoMetadataInfo.Empty;
                _detectedVideoMetadata.FileName = file?.Name ?? "";
                _detectedVideoMetadata.Notes = "Video metadata detection failed: " + ex.Message;
                _videoInfoStatus = FormatVideoMetadataInfo(_detectedVideoMetadata);
                UpdateVideoMetadataUI(_detectedVideoMetadata);
                LogVideoPlaybackPath("Video metadata detection failed. Continuing with native Windows playback and SDR fallback if required.");
            }
        }

        private async System.Threading.Tasks.Task<VideoMetadataInfo> DetectVideoMetadataAsync(WStorage.StorageFile file)
        {
            var info = VideoMetadataInfo.Empty;
            info.FileName = file?.Name ?? "";
            info.DetectionSource = "Windows display APIs";

            ApplyDisplayHdrInfo(info);

            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                info.Notes = "No local video file path was available for metadata probing.";
                ChoosePlaybackPath(info);
                return info;
            }

            var ffprobePath = await GetBundledFfprobePathAsync();
            if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
            {
                info.Notes = "ffprobe.exe was not found, so only Windows display HDR status is available.";
                ChoosePlaybackPath(info);
                return info;
            }

            string json = await RunFfprobeForVideoJsonAsync(ffprobePath, file.Path);
            if (string.IsNullOrWhiteSpace(json))
            {
                info.Notes = "ffprobe returned no metadata. Native playback will still be used.";
                ChoosePlaybackPath(info);
                return info;
            }

            info.DetectionSource = "ffprobe metadata + Windows display APIs";

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("streams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    string type = TryGetJsonString(stream, "codec_type");
                    if (!string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                        continue;

                    ApplyVideoStreamMetadata(info, stream);
                    break;
                }
            }

            ChooseVideoBadge(info);
            ChoosePlaybackPath(info);
            return info;
        }

        private static async System.Threading.Tasks.Task<string> RunFfprobeForVideoJsonAsync(string ffprobePath, string videoPath)
        {
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = ffprobePath;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.ArgumentList.Add("-v");
                process.StartInfo.ArgumentList.Add("error");
                process.StartInfo.ArgumentList.Add("-print_format");
                process.StartInfo.ArgumentList.Add("json");
                process.StartInfo.ArgumentList.Add("-show_streams");
                process.StartInfo.ArgumentList.Add("-show_format");
                process.StartInfo.ArgumentList.Add(videoPath);

                if (!process.Start())
                    return null;

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var waitTask = process.WaitForExitAsync();
                var completed = await System.Threading.Tasks.Task.WhenAny(waitTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8)));
                if (completed != waitTask)
                {
                    try { process.Kill(true); } catch { }
                    DiagnosticLogService.WriteLine("Video metadata probe failed: ffprobe timed out.");
                    return null;
                }

                string output = await outputTask;
                string error = await errorTask;

                if (process.ExitCode != 0)
                {
                    DiagnosticLogService.WriteLine($"Video metadata probe failed: ffprobe exit {process.ExitCode}: {error}");
                    return null;
                }

                return output;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Video metadata probe failed: " + ex.Message);
                return null;
            }
        }

        private static void ApplyVideoStreamMetadata(VideoMetadataInfo info, JsonElement stream)
        {
            info.Codec = TryGetJsonString(stream, "codec_name");
            info.CodecLongName = TryGetJsonString(stream, "codec_long_name");
            info.Profile = TryGetJsonString(stream, "profile");
            info.PixelFormat = TryGetJsonString(stream, "pix_fmt");
            info.ColorSpace = TryGetJsonString(stream, "color_space");
            info.ColorTransfer = TryGetJsonString(stream, "color_transfer");
            info.ColorPrimaries = TryGetJsonString(stream, "color_primaries");
            info.BitDepth = DetectBitDepth(stream, info.PixelFormat, info.Profile);
            info.ChromaSubsampling = DetectChromaSubsampling(info.PixelFormat);

            string codec = (info.Codec ?? "").Trim().ToLowerInvariant();
            string codecLong = (info.CodecLongName ?? "").Trim().ToLowerInvariant();
            info.IsAvc = codec is "h264" or "avc" || codecLong.Contains("h.264") || codecLong.Contains("avc");
            info.IsHevc = codec is "hevc" or "h265" || codecLong.Contains("h.265") || codecLong.Contains("hevc");
            info.NativeCodecPath = info.IsHevc
                ? "H.265/HEVC through Windows Media Foundation"
                : info.IsAvc
                    ? "H.264/AVC through Windows Media Foundation"
                    : "Windows Media Foundation native playback";

            string combined = $"{info.Codec} {info.CodecLongName} {info.Profile} {info.PixelFormat} {info.ColorTransfer} {info.ColorPrimaries} {info.ColorSpace}".ToLowerInvariant();

            if (stream.TryGetProperty("side_data_list", out var sideDataList) &&
                sideDataList.ValueKind == JsonValueKind.Array)
            {
                foreach (var sideData in sideDataList.EnumerateArray())
                {
                    string sideType = TryGetJsonString(sideData, "side_data_type") ?? "";
                    string sideTypeLower = sideType.ToLowerInvariant();
                    combined += " " + sideTypeLower;

                    if (sideTypeLower.Contains("dovi") || sideTypeLower.Contains("dolby vision"))
                    {
                        info.IsDolbyVision = true;
                        int profile = TryGetJsonInt(sideData, "dv_profile");
                        if (profile > 0)
                            info.DolbyVisionProfile = profile.ToString();
                    }

                    if (sideTypeLower.Contains("smpte2094-40") || sideTypeLower.Contains("hdr10+"))
                        info.IsHdr10Plus = true;

                    if (sideTypeLower.Contains("mastering display") || sideTypeLower.Contains("content light"))
                        info.IsHdr10 = true;
                }
            }

            if (combined.Contains("dovi") || combined.Contains("dolby vision"))
                info.IsDolbyVision = true;

            if (string.IsNullOrWhiteSpace(info.DolbyVisionProfile))
                info.DolbyVisionProfile = DetectDolbyVisionProfileFromText(combined);

            if (combined.Contains("smpte2094-40") || combined.Contains("hdr10+"))
                info.IsHdr10Plus = true;

            if (string.Equals(info.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("hlg"))
            {
                info.IsHlg = true;
            }

            if (string.Equals(info.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(info.ColorTransfer, "pq", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("mastering display") ||
                combined.Contains("content light"))
            {
                info.IsHdr10 = true;
            }

            ChooseVideoBadge(info);
        }

        private static int DetectBitDepth(JsonElement stream, string pixelFormat, string profile)
        {
            int bits = TryGetJsonInt(stream, "bits_per_raw_sample");
            if (bits > 0)
                return bits;

            string text = $"{pixelFormat} {profile}".ToLowerInvariant();
            if (text.Contains("12"))
                return 12;
            if (text.Contains("10"))
                return 10;
            if (text.Contains("16"))
                return 16;

            return 8;
        }

        private static string DetectChromaSubsampling(string pixelFormat)
        {
            string pix = (pixelFormat ?? "").ToLowerInvariant();
            if (pix.Contains("yuv420") || pix.Contains("p010") || pix.Contains("nv12"))
                return "4:2:0";
            if (pix.Contains("yuv422"))
                return "4:2:2";
            if (pix.Contains("yuv444"))
                return "4:4:4";
            if (pix.Contains("rgb") || pix.Contains("gbr"))
                return "RGB";

            return string.IsNullOrWhiteSpace(pixelFormat) ? "Unknown" : "Available as " + pixelFormat;
        }

        private static string DetectDolbyVisionProfileFromText(string text)
        {
            try
            {
                var match = Regex.Match(text ?? "", @"(?:dv|dovi|dolby\s*vision)[^\d]{0,12}(?<profile>\d{1,2})", RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups["profile"].Value;
            }
            catch { }

            return null;
        }

        private static void ChooseVideoBadge(VideoMetadataInfo info)
        {
            if (info == null)
                return;

            if (info.IsDolbyVision)
                info.Badge = "Dolby Vision";
            else if (info.IsHdr10Plus)
                info.Badge = "HDR10+";
            else if (info.IsHlg)
                info.Badge = "HLG";
            else if (info.IsHdr10)
                info.Badge = "HDR10";
            else
                info.Badge = "SDR";

            info.DynamicRange = info.Badge;
        }

        private void ApplyDisplayHdrInfo(VideoMetadataInfo info)
        {
            try
            {
                var displayInfo = global::Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                var advanced = displayInfo.GetAdvancedColorInfo();
                info.DisplayAdvancedColorKind = advanced.CurrentAdvancedColorKind.ToString();
                info.WindowsHdrEnabled = advanced.CurrentAdvancedColorKind == global::Windows.Graphics.Display.AdvancedColorKind.HighDynamicRange;
                info.DisplayHdrSupported = advanced.IsAdvancedColorKindAvailable(global::Windows.Graphics.Display.AdvancedColorKind.HighDynamicRange);
            }
            catch (Exception ex)
            {
                info.DisplayAdvancedColorKind = "Unavailable";
                info.WindowsHdrEnabled = false;
                info.DisplayHdrSupported = false;
                info.Notes = "Windows HDR/display capability check unavailable: " + ex.Message;
            }
        }

        private static void ChoosePlaybackPath(VideoMetadataInfo info)
        {
            if (info == null)
                return;

            ChooseVideoBadge(info);

            if (!info.IsDolbyVision && !info.IsHdr10Plus && !info.IsHdr10 && !info.IsHlg)
            {
                info.PlaybackPath = $"SDR video detected. Using {GetNativeCodecPath(info)}.";
                return;
            }

            if (info.WindowsHdrEnabled && info.DisplayHdrSupported)
            {
                info.PlaybackPath = $"{info.Badge} video detected. Windows HDR is enabled and the display reports HDR support; using {GetNativeCodecPath(info)}.";
                return;
            }

            if (info.IsDolbyVision)
            {
                if (info.IsHdr10 || info.IsHdr10Plus || string.Equals(info.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase))
                {
                    info.PlaybackPath = $"Dolby Vision detected, but native Dolby Vision HDR rendering is not confirmed. Using {GetNativeCodecPath(info)} and falling back to the HDR10/PQ base layer where Windows exposes it; otherwise Windows will tone-map or play SDR.";
                    return;
                }

                info.PlaybackPath = $"Dolby Vision detected, but HDR rendering is not available. Using {GetNativeCodecPath(info)} and allowing Windows to tone-map or play SDR.";
                return;
            }

            info.PlaybackPath = $"{info.Badge} video detected, but Windows HDR is disabled or the display does not report HDR support. Using {GetNativeCodecPath(info)} with Windows tone-mapping or SDR presentation.";
        }

        private static string GetNativeCodecPath(VideoMetadataInfo info)
        {
            if (info == null)
                return "native Windows Media Foundation playback";

            if (!string.IsNullOrWhiteSpace(info.NativeCodecPath))
                return info.NativeCodecPath;

            if (info.IsHevc)
                return "H.265/HEVC through Windows Media Foundation";

            if (info.IsAvc)
                return "H.264/AVC through Windows Media Foundation";

            return "native Windows Media Foundation playback";
        }

        private async System.Threading.Tasks.Task ApplyNativeVideoCodecCapabilityAsync(WStorage.StorageFile file, VideoMetadataInfo info)
        {
            try
            {
                if (info == null)
                    return;

                if (info.IsHevc)
                {
                    info.HevcExtensionInstalled = await IsAnyCodecExtensionInstalledAsync(
                        MicrosoftHevcVideoExtensionPrefix,
                        MicrosoftHevcVideoExtensionsPrefix,
                        MicrosoftHevcDeviceExtensionPrefix);
                }

                if (info.IsDolbyVision)
                {
                    info.DolbyAccessInstalled = await IsCodecExtensionInstalledAsync(DolbyAccessPrefix);
                    info.DolbyVisionAccessInstalled = await IsCodecExtensionInstalledAsync(DolbyVisionAccessPrefix);
                }

                ChoosePlaybackPath(info);

                var notes = new List<string>();
                if (!string.IsNullOrWhiteSpace(info.Notes))
                    notes.Add(info.Notes);

                if (info.IsAvc)
                    notes.Add("H.264/AVC is enabled through Windows Media Foundation native playback.");

                if (info.IsHevc)
                {
                    notes.Add(info.HevcExtensionInstalled
                        ? "HEVC/H.265 support appears installed, so Zink will use Windows' native HEVC decoder."
                        : "HEVC/H.265 support was not detected. Dolby Vision and HEVC playback may require the Windows HEVC Video Extensions or OEM HEVC support.");
                }

                if (info.IsDolbyVision)
                {
                    notes.Add(info.DolbyVisionAccessInstalled
                        ? "Dolby Vision Access appears installed. Dolby Vision still requires a Dolby Vision capable display, GPU driver, and Windows HDR path."
                        : "Dolby Vision metadata was detected. Zink enables the native Windows path, but full Dolby Vision rendering depends on Dolby/OEM Windows support.");
                }

                info.Notes = string.Join("\n", notes);
                LogVideoPlaybackPath($"{GetNativeCodecPath(info)} selected. HEVC extension installed: {info.HevcExtensionInstalled}. Dolby Access installed: {info.DolbyAccessInstalled}. Dolby Vision Access installed: {info.DolbyVisionAccessInstalled}. Dolby Vision detected: {info.IsDolbyVision}.");

                await PromptForNativeVideoSupportIfNeededAsync(file, info);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Video native codec capability check failed: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task PromptForNativeVideoSupportIfNeededAsync(WStorage.StorageFile file, VideoMetadataInfo info)
        {
            try
            {
                if (file == null || info == null || XamlRoot == null)
                    return;

                if (!info.IsHevc && !info.IsDolbyVision)
                    return;

                if (info.IsHevc && info.HevcExtensionInstalled && (!info.IsDolbyVision || info.DolbyVisionAccessInstalled))
                    return;

                var path = file.Path ?? "";
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (_videoSupportPromptAlreadyShownForCurrentFile &&
                    string.Equals(_lastVideoSupportPromptedPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                _videoSupportPromptAlreadyShownForCurrentFile = true;
                _lastVideoSupportPromptedPath = path;

                string title = info.IsDolbyVision
                    ? "Enable Dolby Vision playback"
                    : "Enable HEVC playback";

                string content = info.IsDolbyVision
                    ? "This film has Dolby Vision metadata. Zink will use Windows' native H.265/HEVC Media Foundation path, but Dolby Vision rendering also needs Windows HEVC support, Dolby/OEM support, Windows HDR, and a Dolby Vision capable display.\n\nOpen Microsoft Store to install or enable the missing Windows video support?"
                    : "This film uses H.265/HEVC. Zink will use Windows' native Media Foundation path, but this PC may need the Windows HEVC Video Extensions or OEM HEVC support.\n\nOpen Microsoft Store to install HEVC support?";

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = content,
                    PrimaryButtonText = info.IsDolbyVision ? "Open Dolby/HEVC support" : "Open HEVC support",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                    return;

                string query = info.IsDolbyVision
                    ? "Dolby Vision Access HEVC Video Extensions"
                    : "HEVC Video Extensions";

                _waitingForCodecInstallReturn = true;
                _pendingReloadVideoPath = _currentFile?.Path;
                _pendingReloadResumeSeconds = 0;

                await WSystem.Launcher.LaunchUriAsync(new Uri("ms-windows-store://search/?query=" + Uri.EscapeDataString(query)));
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Video native codec support prompt failed: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task<bool> IsAnyCodecExtensionInstalledAsync(params string[] packagePrefixes)
        {
            try
            {
                if (packagePrefixes == null || packagePrefixes.Length == 0)
                    return false;

                foreach (var prefix in packagePrefixes)
                {
                    if (await IsCodecExtensionInstalledAsync(prefix))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private void UpdateVideoMetadataUI(VideoMetadataInfo info, bool showBadge = true)
        {
            try
            {
                if (info == null)
                    info = VideoMetadataInfo.Empty;

                if (VideoFormatBadgeText != null)
                    VideoFormatBadgeText.Text = string.IsNullOrWhiteSpace(info.Badge) ? "SDR" : info.Badge;

                if (VideoFormatBadge != null && showBadge)
                {
                    VideoFormatBadge.Visibility = Visibility.Visible;
                    _videoBadgeHideTimer?.Stop();
                    _videoBadgeHideTimer?.Start();
                }

                if (VideoInfoTextBlock != null)
                    VideoInfoTextBlock.Text = FormatVideoMetadataInfo(info);
            }
            catch { }
        }

        private static string FormatVideoMetadataInfo(VideoMetadataInfo info)
        {
            if (info == null)
                info = VideoMetadataInfo.Empty;

            var builder = new StringBuilder();
            AppendInfoLine(builder, "File", info.FileName);
            AppendInfoLine(builder, "Badge", info.Badge);
            AppendInfoLine(builder, "Dynamic range", info.DynamicRange);
            AppendInfoLine(builder, "Codec", FormatVideoCodec(info));
            AppendInfoLine(builder, "H.264/AVC", info.IsAvc ? "Yes" : "No");
            AppendInfoLine(builder, "HEVC/H.265", info.IsHevc ? "Yes" : "No");
            AppendInfoLine(builder, "Native codec path", GetNativeCodecPath(info));
            AppendInfoLine(builder, "Bit depth", info.BitDepth > 0 ? info.BitDepth + "-bit" : "Unknown");
            AppendInfoLine(builder, "Colour space", info.ColorSpace);
            AppendInfoLine(builder, "Colour transfer", info.ColorTransfer);
            AppendInfoLine(builder, "Colour primaries", info.ColorPrimaries);
            AppendInfoLine(builder, "Chroma subsampling", info.ChromaSubsampling);
            AppendInfoLine(builder, "Dolby Vision", info.IsDolbyVision ? "Yes" : "No");
            AppendInfoLine(builder, "Dolby Vision profile", info.DolbyVisionProfile);
            AppendInfoLine(builder, "HDR10", info.IsHdr10 ? "Yes" : "No");
            AppendInfoLine(builder, "HDR10+", info.IsHdr10Plus ? "Yes" : "No");
            AppendInfoLine(builder, "HLG", info.IsHlg ? "Yes" : "No");
            AppendInfoLine(builder, "Windows HDR enabled", info.WindowsHdrEnabled ? "Yes" : "No");
            AppendInfoLine(builder, "Display HDR support", info.DisplayHdrSupported ? "Yes" : "No");
            AppendInfoLine(builder, "Display advanced colour", info.DisplayAdvancedColorKind);
            AppendInfoLine(builder, "HEVC extension installed", info.HevcExtensionInstalled ? "Yes" : "No");
            AppendInfoLine(builder, "Dolby Access installed", info.DolbyAccessInstalled ? "Yes" : "No");
            AppendInfoLine(builder, "Dolby Vision Access installed", info.DolbyVisionAccessInstalled ? "Yes" : "No");
            AppendInfoLine(builder, "Playback path", info.PlaybackPath);
            AppendInfoLine(builder, "Detection source", info.DetectionSource);
            AppendInfoLine(builder, "Notes", info.Notes);
            return builder.ToString().Trim();
        }

        private static string FormatVideoCodec(VideoMetadataInfo info)
        {
            string codec = string.IsNullOrWhiteSpace(info.Codec) ? "Unknown" : info.Codec.ToUpperInvariant();
            string profile = string.IsNullOrWhiteSpace(info.Profile) ? "" : " / " + info.Profile;
            string longName = string.IsNullOrWhiteSpace(info.CodecLongName) ? "" : " (" + info.CodecLongName + ")";
            return codec + profile + longName;
        }

        private static void AppendInfoLine(StringBuilder builder, string label, string value)
        {
            if (builder == null)
                return;

            builder.Append(label);
            builder.Append(": ");
            builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "Unknown" : value);
        }

        private void LogVideoPlaybackPath(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message))
                    return;

                DiagnosticLogService.WriteLine("Video Player HDR path: " + message);
            }
            catch { }
        }

        private static AudioStreamInfo GetPreferredAudioStream(IReadOnlyList<AudioStreamInfo> streams, string preferredMode)
        {
            try
            {
                if (streams == null || streams.Count == 0)
                    return null;

                string mode = NormalizeSurroundMode(preferredMode);

                if (!string.Equals(mode, SurroundModeAuto, StringComparison.OrdinalIgnoreCase))
                {
                    AudioStreamInfo preferred = null;
                    int preferredScore = int.MinValue;

                    foreach (var stream in streams)
                    {
                        if (!StreamMatchesSurroundMode(stream, mode))
                            continue;

                        if (!IsLikelyWindowsPlayableAudioStream(stream))
                            continue;

                        int score = GetAudioStreamScore(stream);
                        if (score > preferredScore)
                        {
                            preferred = stream;
                            preferredScore = score;
                        }
                    }

                    if (preferred != null)
                        return preferred;

                    foreach (var stream in streams)
                    {
                        if (!StreamMatchesSurroundMode(stream, mode))
                            continue;

                        int score = GetAudioStreamScore(stream);
                        if (score > preferredScore)
                        {
                            preferred = stream;
                            preferredScore = score;
                        }
                    }

                    if (preferred != null)
                        return preferred;
                }

                return GetBestAudioStream(streams);
            }
            catch
            {
                return GetBestAudioStream(streams);
            }
        }

        private static string TryGetJsonString(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
            catch { }

            return null;
        }

        private static int TryGetJsonInt(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var value))
                {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                        return number;

                    if (value.ValueKind == JsonValueKind.String &&
                        int.TryParse(value.GetString(), out number))
                    {
                        return number;
                    }
                }
            }
            catch { }

            return 0;
        }

        private static AudioStreamInfo GetBestAudioStream(IReadOnlyList<AudioStreamInfo> streams)
        {
            try
            {
                AudioStreamInfo best = null;
                int bestScore = int.MinValue;

                if (streams == null)
                    return null;

                foreach (var stream in streams)
                {
                    if (!IsLikelyWindowsPlayableAudioStream(stream))
                        continue;

                    int score = GetAudioStreamScore(stream);
                    if (score > bestScore)
                    {
                        best = stream;
                        bestScore = score;
                    }
                }

                if (best != null)
                    return best;

                bestScore = int.MinValue;

                foreach (var stream in streams)
                {
                    int score = GetAudioStreamScore(stream);
                    if (score > bestScore)
                    {
                        best = stream;
                        bestScore = score;
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static int GetAudioStreamScore(AudioStreamInfo stream)
        {
            if (stream == null)
                return int.MinValue;

            int channels = Math.Max(0, stream.Channels);
            int score = Math.Min(channels, 6) * 100;
            string codec = (stream.Codec ?? "").Trim().ToLowerInvariant();
            string profile = (stream.Profile ?? "").Trim().ToLowerInvariant();
            string layout = (stream.ChannelLayout ?? "").Trim().ToLowerInvariant();
            string title = (stream.Title ?? "").Trim().ToLowerInvariant();

            if (IsReliableWindowsAudioStream(stream))
                score += 20000;
            else if (IsLikelyWindowsPlayableAudioStream(stream))
                score += 10000;
            else
                score -= 10000;

            if (codec == "eac3" || codec == "ac4")
                score += 600;
            else if (codec == "ac3")
                score += 550;
            else if (codec == "aac")
                score += channels <= 6 ? 500 : 80;
            else if (codec == "mp3" || codec == "opus")
                score += 450;
            else if (codec == "truehd")
                score += 30;
            else if (codec == "dts" || codec == "dca")
                score += 25;

            if (stream.IsDolbyAtmos)
                score += 140;

            if (string.Equals(stream.SurroundLayout, SurroundMode24110, StringComparison.OrdinalIgnoreCase))
                score += 100;
            else if (string.Equals(stream.SurroundLayout, SurroundMode916, StringComparison.OrdinalIgnoreCase))
                score += 70;
            else if (string.Equals(stream.SurroundLayout, SurroundMode914, StringComparison.OrdinalIgnoreCase))
                score += 65;
            else if (string.Equals(stream.SurroundLayout, SurroundMode714, StringComparison.OrdinalIgnoreCase))
                score += 60;
            else if (string.Equals(stream.SurroundLayout, SurroundMode712, StringComparison.OrdinalIgnoreCase))
                score += 55;
            else if (string.Equals(stream.SurroundLayout, SurroundMode512, StringComparison.OrdinalIgnoreCase))
                score += 52;
            else if (string.Equals(stream.SurroundLayout, SurroundMode72, StringComparison.OrdinalIgnoreCase))
                score += 50;

            if (channels > 6 && codec == "aac")
                score -= 9000;

            if (profile.Contains("ma") || profile.Contains("hd") ||
                layout.Contains("7.1") || title.Contains("7.1"))
            {
                score += 20;
            }

            return score;
        }

        private static bool IsReliableWindowsAudioStream(AudioStreamInfo stream)
        {
            if (stream == null)
                return false;

            string codec = (stream.Codec ?? "").Trim().ToLowerInvariant();
            int channels = Math.Max(0, stream.Channels);

            if (codec is "ac3" or "eac3" or "ac4" or "mp3" or "mp2" or "opus")
                return true;

            if (codec == "aac")
                return channels <= 6;

            if (codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase))
                return true;

            return codec is "wmav1" or "wmav2" or "wmapro";
        }

        private static bool IsLikelyWindowsPlayableAudioStream(AudioStreamInfo stream)
        {
            if (stream == null)
                return false;

            string codec = (stream.Codec ?? "").Trim().ToLowerInvariant();

            return codec is
                "aac" or
                "mp3" or
                "mp2" or
                "ac3" or
                "eac3" or
                "ac4" or
                "opus" or
                "flac" or
                "alac" or
                "pcm_s16le" or
                "pcm_s24le" or
                "pcm_s32le" or
                "pcm_f32le" or
                "wmav1" or
                "wmav2" or
                "wmapro";
        }

        private static string FormatAudioStreamSummary(AudioStreamInfo stream)
        {
            if (stream == null)
                return "Unknown audio";

            string channelText = FormatChannelText(stream.Channels, stream.ChannelLayout, stream.SurroundLayout);
            string codecText = FormatCodecText(stream.Codec, stream.Profile);
            string atmosText = stream.IsDolbyAtmos ? " Dolby Atmos" : "";
            string languageText = string.IsNullOrWhiteSpace(stream.Language)
                ? ""
                : $" - {stream.Language.ToUpperInvariant()}";
            string titleText = string.IsNullOrWhiteSpace(stream.Title)
                ? ""
                : $" - {stream.Title}";

            return $"{channelText} {codecText}{atmosText}{languageText}{titleText}".Trim();
        }

        private static string FormatChannelText(int channels, string layout, string detectedLayout = null)
        {
            if (!string.IsNullOrWhiteSpace(detectedLayout))
                return detectedLayout;

            var layoutText = layout ?? "";

            if (ContainsLayoutToken(layoutText, SurroundMode24110))
                return SurroundMode24110;

            if (ContainsLayoutToken(layoutText, SurroundMode916))
                return "9.1.6";

            if (ContainsLayoutToken(layoutText, SurroundMode914))
                return "9.1.4";

            if (ContainsLayoutToken(layoutText, SurroundMode714))
                return "7.1.4";

            if (ContainsLayoutToken(layoutText, SurroundMode712))
                return "7.1.2";

            if (ContainsLayoutToken(layoutText, SurroundMode512))
                return "5.1.2";

            if (ContainsLayoutToken(layoutText, SurroundMode72))
                return "7.2";

            if (channels >= 8 || ContainsLayoutToken(layoutText, SurroundMode71))
                return "7.1";

            if (channels >= 6 || ContainsLayoutToken(layoutText, SurroundMode51))
                return "5.1";

            if (channels == 3 || ContainsLayoutToken(layoutText, SurroundMode21))
                return "2.1";

            if (channels == 2)
                return "Stereo";

            if (channels == 1)
                return "Mono";

            if (channels > 0)
                return $"{channels} channel";

            return "Unknown channels";
        }

        private static string FormatCodecText(string codec, string profile)
        {
            string normalized = (codec ?? "").Trim().ToLowerInvariant();
            string profileText = string.IsNullOrWhiteSpace(profile) ? "" : $" {profile}";

            return normalized switch
            {
                "truehd" => "Dolby TrueHD",
                "eac3" => "Dolby Digital Plus",
                "ac3" => "Dolby Digital",
                "ac4" => "Dolby AC-4",
                "dts" => $"DTS{profileText}",
                "dca" => $"DTS{profileText}",
                "aac" => "AAC",
                "opus" => "Opus",
                "mp3" => "MP3",
                _ => string.IsNullOrWhiteSpace(codec) ? "Unknown codec" : codec.ToUpperInvariant()
            };
        }

        private static string DetectSurroundLayout(int channels, string layout, string codecLongName, string profile, string title)
        {
            string combined = $"{layout} {codecLongName} {profile} {title}".ToLowerInvariant();

            if (ContainsLayoutToken(combined, SurroundMode24110) || channels >= 35)
                return SurroundMode24110;

            if (ContainsLayoutToken(combined, SurroundMode916))
                return SurroundMode916;

            if (ContainsLayoutToken(combined, SurroundMode914))
                return SurroundMode914;

            if (ContainsLayoutToken(combined, SurroundMode714))
                return SurroundMode714;

            if (ContainsLayoutToken(combined, SurroundMode712))
                return SurroundMode712;

            if (ContainsLayoutToken(combined, SurroundMode512))
                return SurroundMode512;

            if (ContainsLayoutToken(combined, SurroundMode72))
                return SurroundMode72;

            if (ContainsLayoutToken(combined, SurroundMode71) || channels == 8)
                return SurroundMode71;

            if (ContainsLayoutToken(combined, SurroundMode51) || channels == 6)
                return SurroundMode51;

            if (ContainsLayoutToken(combined, SurroundMode21) || channels == 3)
                return SurroundMode21;

            if (channels >= 16)
                return SurroundMode916;

            if (channels >= 14)
                return SurroundMode914;

            if (channels >= 12)
                return SurroundMode714;

            if (channels >= 10)
                return SurroundMode712;

            if (channels >= 9)
                return SurroundMode72;

            return null;
        }

        private static bool IsDolbyAtmosStream(string codec, string codecLongName, string profile, string layout, string title)
        {
            string normalizedCodec = (codec ?? "").Trim().ToLowerInvariant();
            string combined = $"{codecLongName} {profile} {layout} {title}".ToLowerInvariant();

            if (combined.Contains("atmos") ||
                combined.Contains("joc") ||
                combined.Contains("joint object coding") ||
                combined.Contains("truehd atmos") ||
                combined.Contains("object") ||
                combined.Contains("immersive"))
            {
                return normalizedCodec is "truehd" or "eac3" or "ac4" or "mlp";
            }

            return false;
        }

        private static bool StreamMatchesSurroundMode(AudioStreamInfo stream, string mode)
        {
            if (stream == null)
                return false;

            mode = NormalizeSurroundMode(mode);

            if (string.Equals(mode, SurroundModeAtmos, StringComparison.OrdinalIgnoreCase))
                return stream.IsDolbyAtmos;

            if (string.Equals(stream.SurroundLayout, mode, StringComparison.OrdinalIgnoreCase))
                return true;

            string summary = $"{stream.ChannelLayout} {stream.CodecLongName} {stream.Profile} {stream.Title}".ToLowerInvariant();
            return ContainsLayoutToken(summary, mode);
        }

        private static bool ContainsLayoutToken(string text, string layout)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(layout))
                return false;

            string escapedLayout = Regex.Escape(layout).Replace("\\.", "[._]");
            return Regex.IsMatch(
                text,
                $@"(?<!\d){escapedLayout}(?!(?:[._]\d)|\d)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string NormalizeSurroundMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
                return SurroundModeAuto;

            mode = mode.Trim().ToLowerInvariant();

            return mode switch
            {
                SurroundModeAtmos => SurroundModeAtmos,
                SurroundMode21 => SurroundMode21,
                SurroundMode51 => SurroundMode51,
                SurroundMode71 => SurroundMode71,
                SurroundMode72 => SurroundMode72,
                SurroundMode512 => SurroundMode512,
                SurroundMode712 => SurroundMode712,
                SurroundMode714 => SurroundMode714,
                SurroundMode914 => SurroundMode914,
                SurroundMode916 => SurroundMode916,
                SurroundMode24110 => SurroundMode24110,
                _ => SurroundModeAuto
            };
        }

        private static string GetSurroundModeSelectionPrefix(string mode)
        {
            mode = NormalizeSurroundMode(mode);

            return mode switch
            {
                SurroundModeAtmos => "Dolby Atmos",
                SurroundMode21 => "Surround 2.1",
                SurroundMode51 => "Surround 5.1",
                SurroundMode71 => "Surround 7.1",
                SurroundMode72 => "Surround 7.2",
                SurroundMode512 => "Dolby Atmos 5.1.2",
                SurroundMode712 => "Dolby Atmos 7.1.2",
                SurroundMode714 => "Surround 7.1.4",
                SurroundMode914 => "Surround 9.1.4",
                SurroundMode916 => "Surround 9.1.6",
                SurroundMode24110 => "Dolby Atmos 24.1.10",
                _ => "Auto"
            };
        }

        private async System.Threading.Tasks.Task<string> GetBundledFfprobePathAsync()
        {
            try
            {
                var installed = WAppModel.Package.Current.InstalledLocation;

                try
                {
                    var toolsFolder = await installed.GetFolderAsync(ToolsFolderName);
                    var probeFile = await toolsFolder.GetFileAsync(FfprobeExeName);
                    return probeFile.Path;
                }
                catch { }

                try
                {
                    var rootProbeFile = await installed.GetFileAsync(FfprobeExeName);
                    return rootProbeFile.Path;
                }
                catch { }
            }
            catch { }

            try
            {
                var baseDirectory = AppContext.BaseDirectory;
                var toolProbePath = Path.Combine(baseDirectory, ToolsFolderName, FfprobeExeName);
                if (File.Exists(toolProbePath))
                    return toolProbePath;

                var rootProbePath = Path.Combine(baseDirectory, FfprobeExeName);
                if (File.Exists(rootProbePath))
                    return rootProbePath;
            }
            catch { }

            return null;
        }

        private async System.Threading.Tasks.Task<string> GetBundledFfmpegPathAsync()
        {
            try
            {
                var installed = WAppModel.Package.Current.InstalledLocation;

                try
                {
                    var toolsFolder = await installed.GetFolderAsync(ToolsFolderName);
                    var ffmpegFile = await toolsFolder.GetFileAsync(FfmpegExeName);
                    return ffmpegFile.Path;
                }
                catch { }

                try
                {
                    var rootFfmpegFile = await installed.GetFileAsync(FfmpegExeName);
                    return rootFfmpegFile.Path;
                }
                catch { }
            }
            catch { }

            try
            {
                var baseDirectory = AppContext.BaseDirectory;
                var toolFfmpegPath = Path.Combine(baseDirectory, ToolsFolderName, FfmpegExeName);
                if (File.Exists(toolFfmpegPath))
                    return toolFfmpegPath;

                var rootFfmpegPath = Path.Combine(baseDirectory, FfmpegExeName);
                if (File.Exists(rootFfmpegPath))
                    return rootFfmpegPath;
            }
            catch { }

            return null;
        }

        private async System.Threading.Tasks.Task<WStorage.StorageFile> EnsureAudioCompatiblePlaybackFileAsync(WStorage.StorageFile sourceFile)
        {
            try
            {
                if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.Path))
                    return sourceFile;

                if (!ShouldCreateAudioFallbackFile(_selectedAudioStream))
                {
                    WriteVideoAudioDiagnostics("Audio fallback not needed for selected stream: " + (_selectedAudioStream == null ? "none" : FormatAudioStreamSummary(_selectedAudioStream)));
                    return sourceFile;
                }

                var fallbackPath = await GetAudioFallbackPathAsync(sourceFile, _selectedAudioStream);
                if (File.Exists(fallbackPath))
                {
                    _usingAudioFallbackFile = true;
                    WriteVideoAudioDiagnostics("Using existing audio fallback file: " + fallbackPath);
                    return await WStorage.StorageFile.GetFileFromPathAsync(fallbackPath);
                }

                var ffmpegPath = await GetBundledFfmpegPathAsync();
                if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                {
                    WriteVideoAudioDiagnostics("Audio fallback needed but ffmpeg.exe was not found. Continuing with original file.");
                    return sourceFile;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath));
                WriteVideoAudioDiagnostics("Creating audio fallback file with stereo AAC audio. Source: " + sourceFile.Path + " Target: " + fallbackPath + " Stream: " + FormatAudioStreamSummary(_selectedAudioStream));

                var tempPath = fallbackPath + ".tmp";
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("-y");
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(sourceFile.Path);
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("0:v:0?");
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("0:a:" + Math.Max(0, _selectedAudioStream?.AudioTrackNumber ?? 0));
                startInfo.ArgumentList.Add("-sn");
                startInfo.ArgumentList.Add("-dn");
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("copy");
                startInfo.ArgumentList.Add("-c:a");
                startInfo.ArgumentList.Add("aac");
                startInfo.ArgumentList.Add("-ac");
                startInfo.ArgumentList.Add("2");
                startInfo.ArgumentList.Add("-b:a");
                startInfo.ArgumentList.Add("192k");
                startInfo.ArgumentList.Add(tempPath);

                using var process = new Process();
                process.StartInfo = startInfo;
                process.Start();

                string stdoutTaskResult = "";
                string stderrTaskResult = "";
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                bool exited = await System.Threading.Tasks.Task.Run(() => process.WaitForExit((int)TimeSpan.FromMinutes(20).TotalMilliseconds));
                stdoutTaskResult = await stdoutTask;
                stderrTaskResult = await stderrTask;

                if (!exited)
                {
                    try { process.Kill(true); } catch { }
                    WriteVideoAudioDiagnostics("Audio fallback creation timed out. Continuing with original file.");
                    return sourceFile;
                }

                if (process.ExitCode != 0 || !File.Exists(tempPath))
                {
                    WriteVideoAudioDiagnostics("Audio fallback creation failed with exit " + process.ExitCode + ". stdout: " + TrimForLog(stdoutTaskResult, 600) + " stderr: " + TrimForLog(stderrTaskResult, 1200));
                    return sourceFile;
                }

                try { if (File.Exists(fallbackPath)) File.Delete(fallbackPath); } catch { }
                File.Move(tempPath, fallbackPath);

                _usingAudioFallbackFile = true;
                WriteVideoAudioDiagnostics("Audio fallback file created successfully: " + fallbackPath);
                return await WStorage.StorageFile.GetFileFromPathAsync(fallbackPath);
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Audio fallback setup failed: " + ex.Message + ". Continuing with original file.");
                return sourceFile;
            }
        }

        private async System.Threading.Tasks.Task ConfigureDirectAudioSupportForCurrentFileAsync(WStorage.StorageFile sourceFile)
        {
            _effectivePlaybackFile = sourceFile;
            _usingAudioFallbackFile = false;
            ConfigureDirectAudioSupportForSelectedStream();
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private void ConfigureDirectAudioSupportForSelectedStream()
        {
            try
            {
                if (mediaPlayerElement?.MediaPlayer != null)
                    mediaPlayerElement.MediaPlayer.IsMuted = false;

                var selected = _selectedAudioStream;
                if (selected != null && NeedsNonNativeAudioEngine(selected))
                {
                    UseCompatibilityPlaybackSurface();
                    WriteVideoAudioDiagnostics(
                        "Compatibility playback engine enabled for " +
                        FormatAudioStreamSummary(selected) +
                        ". This uses the bundled LibVLC media engine in-process; no FFmpeg playback process will be started.");
                    return;
                }

                UseNativePlaybackSurface();
                WriteVideoAudioDiagnostics("Native Windows audio renderer enabled for " + (selected == null ? "no selected stream" : FormatAudioStreamSummary(selected)));
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Direct audio support configuration failed: " + ex.Message);
            }
        }

        private static bool NeedsNonNativeAudioEngine(AudioStreamInfo stream)
        {
            if (stream == null)
                return false;

            if (!IsReliableWindowsAudioStream(stream))
                return true;

            if (stream.Channels > 2)
                return true;

            var layout = (stream.ChannelLayout ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(layout) &&
                layout != "mono" &&
                layout != "stereo" &&
                layout != "2.0")
            {
                return true;
            }

            var surroundLayout = (stream.SurroundLayout ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(surroundLayout) &&
                surroundLayout != "mono" &&
                surroundLayout != "stereo" &&
                surroundLayout != "2.0")
            {
                return true;
            }

            return false;
        }

        private async System.Threading.Tasks.Task LoadAndPlayWithCompatibilityEngineAsync(WStorage.StorageFile file)
        {
            try
            {
                EnsureCompatibilityPlaybackEngine();
                if (_vlcMediaPlayer == null || _libVLC == null)
                {
                    WriteVideoAudioDiagnostics("Compatibility audio engine was requested but unavailable; continuing with native Windows playback.");
                    return;
                }

                UseCompatibilityPlaybackSurface();
                ApplySavedVolume();
                try { mediaPlayerElement.MediaPlayer.Volume = 0; } catch { }

                PlayCompatibilityAudioFromMilliseconds(0);

                await System.Threading.Tasks.Task.Delay(250);
                try
                {
                    var nativeSeconds = mediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position.TotalSeconds ?? 0;
                    if (nativeSeconds > 0)
                        PlayCompatibilityAudioFromMilliseconds((long)(nativeSeconds * 1000.0));
                }
                catch { }

                ScheduleCompatibilityAudioResync(350);
                ScheduleCompatibilityAudioResync(1200);

                WriteVideoAudioDiagnostics("Compatibility audio engine play invoked for " + FormatVideoFileForLog(file) + " with LibVLC video disabled.");
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Compatibility audio engine playback failed: " + ex.Message);
            }
        }

        private void PlayCompatibilityAudioFromMilliseconds(long startMilliseconds)
        {
            if (_vlcMediaPlayer == null || _libVLC == null || _currentFile == null)
                return;

            var clampedMilliseconds = Math.Max(0, startMilliseconds);
            var startSeconds = clampedMilliseconds / 1000.0;
            _vlcAudioBaseOffsetMilliseconds = clampedMilliseconds;
            _vlcReadyForSeek = true;

            try { _vlcAudioMedia?.Dispose(); } catch { }
            _vlcAudioMedia = new VlcMedia(_libVLC, new Uri(_currentFile.Path));
            _vlcAudioMedia.AddOption(":no-video");
            _vlcAudioMedia.AddOption(":audio-time-stretch");
            _vlcAudioMedia.AddOption(":input-fast-seek");
            if (startSeconds > 0)
                _vlcAudioMedia.AddOption(":start-time=" + startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            _vlcMediaPlayer.Play(_vlcAudioMedia);
        }

        private void VlcMediaPlayer_LengthChanged(object sender, LibVLCSharp.Shared.MediaPlayerLengthChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_useCompatibilityPlaybackEngine)
                        return;

                    var duration = TimeSpan.FromMilliseconds(Math.Max(0, e.Length));
                    if (duration.TotalSeconds <= 0)
                        return;

                    _vlcReadyForSeek = true;
                    _mediaReadyForSeek = true;
                    SeekSlider.IsEnabled = true;

                    if (_pendingResumeSeconds > 1 && (duration.TotalSeconds - _pendingResumeSeconds) > 2)
                    {
                        try { _vlcMediaPlayer.Time = (long)(_pendingResumeSeconds * 1000.0); } catch { }
                        _pendingResumeSeconds = 0;
                    }

                    _ignoreSliderChange = true;
                    SeekSlider.Minimum = 0;
                    SeekSlider.Maximum = duration.TotalSeconds;
                    SeekSlider.Value = Math.Max(0, Math.Min(GetCurrentPlaybackPositionSeconds(), duration.TotalSeconds));
                    _ignoreSliderChange = false;

                    TotalTimeText.Text = FormatTime(duration);
                    CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(GetCurrentPlaybackPositionSeconds()));
                    SyncDiscordPlaybackClockFromSession(force: true);
                    ResetDiscordSecondPushTracking();
                    try { _discordPresenceTimer?.Start(); } catch { }
                    RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                }
                catch { }
            });
        }

        private void VlcMediaPlayer_TimeChanged(object sender, LibVLCSharp.Shared.MediaPlayerTimeChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_useCompatibilityPlaybackEngine)
                        return;

                    var position = TimeSpan.FromMilliseconds(Math.Max(0, e.Time));
                    CurrentTimeText.Text = FormatTime(position);
                    UpdateSubtitleOverlay(position);
                }
                catch { }
            });
        }

        private void VlcMediaPlayer_Playing(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _vlcPlaybackEnded = false;
                    _userPausedDiscordPresence = false;
                    SyncDiscordPlaybackClockFromSession(force: true);
                    try { _discordPresenceTimer?.Start(); } catch { }
                    RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                }
                catch { }
            });
        }

        private void VlcMediaPlayer_Paused(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_userPausedDiscordPresence)
                    {
                        try { _discordPresenceTimer?.Stop(); } catch { }
                        RefreshDiscordPausedPresence(forcePush: true);
                    }
                }
                catch { }
            });
        }

        private void VlcMediaPlayer_EndReached(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _vlcPlaybackEnded = true;
                    _userPausedDiscordPresence = false;
                    _discordPresenceTimer?.Stop();
                    ResetDiscordPlaybackClock();
                    ClearDiscordVideoPresence();
                }
                catch { }
            });
        }

        private void VlcMediaPlayer_EncounteredError(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    WriteVideoAudioDiagnostics("Compatibility engine encountered a playback error for " + FormatVideoFileForLog(_currentFile) + ".");
                    ScheduleCompatibilityAudioResync(500, forceRestart: true);
                }
                catch { }
            });
        }

        private void ScheduleCompatibilityAudioResync(int delayMilliseconds, bool forceRestart = false)
        {
            _ = ResyncCompatibilityAudioAfterDelayAsync(delayMilliseconds, forceRestart);
        }

        private async System.Threading.Tasks.Task ResyncCompatibilityAudioAfterDelayAsync(int delayMilliseconds, bool forceRestart = false)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(Math.Max(0, delayMilliseconds));
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { EnsureCompatibilityAudioSyncedToNativeVideo(forceRestart); } catch { }
                });
            }
            catch { }
        }

        private void EnsureCompatibilityAudioSyncedToNativeVideo(bool forceRestart = false)
        {
            try
            {
                if (!_useCompatibilityPlaybackEngine || _vlcMediaPlayer == null || _currentFile == null)
                    return;

                var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null)
                    return;

                var duration = session.NaturalDuration;
                if (duration.TotalSeconds <= 0)
                    return;

                var state = session.PlaybackState;
                var nativePosition = session.Position;
                if (nativePosition < TimeSpan.Zero)
                    nativePosition = TimeSpan.Zero;
                if (nativePosition > duration)
                    nativePosition = duration;

                var targetMilliseconds = (long)nativePosition.TotalMilliseconds;
                var audioMilliseconds = _vlcAudioBaseOffsetMilliseconds + Math.Max(0, _vlcMediaPlayer.Time);
                var driftMilliseconds = Math.Abs(audioMilliseconds - targetMilliseconds);
                var shouldBePlaying = state == MediaPlaybackState.Playing || state == MediaPlaybackState.Opening || state == MediaPlaybackState.Buffering;
                var audioStopped = shouldBePlaying && !_vlcMediaPlayer.IsPlaying;
                var farOutOfSync = driftMilliseconds > 2200;

                var nowUtc = DateTime.UtcNow;
                if (!forceRestart && !audioStopped && !farOutOfSync)
                    return;

                if (!forceRestart && (nowUtc - _lastCompatibilityAudioSyncUtc).TotalMilliseconds < 650)
                    return;

                _lastCompatibilityAudioSyncUtc = nowUtc;

                if ((forceRestart || audioStopped) && (nowUtc - _lastCompatibilityAudioRestartUtc).TotalMilliseconds > 450)
                {
                    _lastCompatibilityAudioRestartUtc = nowUtc;
                    RestartCompatibilityAudioAtNativePositionAsync(targetMilliseconds, shouldBePlaying);
                    return;
                }

                if (driftMilliseconds > 7000)
                {
                    _lastCompatibilityAudioRestartUtc = nowUtc;
                    RestartCompatibilityAudioAtNativePositionAsync(targetMilliseconds, shouldBePlaying);
                    return;
                }

                _vlcMediaPlayer.Time = Math.Max(0, targetMilliseconds - _vlcAudioBaseOffsetMilliseconds);
                if (shouldBePlaying)
                    _vlcMediaPlayer.Play();
                else
                    _vlcMediaPlayer.Pause();
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Compatibility audio sync failed: " + ex.Message);
            }
        }

        private async void RestartCompatibilityAudioAtNativePositionAsync(long targetMilliseconds, bool shouldPlay)
        {
            if (_compatibilityAudioRestartInProgress)
                return;

            _compatibilityAudioRestartInProgress = true;
            try
            {
                if (_vlcMediaPlayer == null || _libVLC == null || _currentFile == null)
                    return;

                _vlcMediaPlayer.Stop();
                await System.Threading.Tasks.Task.Delay(120);

                PlayCompatibilityAudioFromMilliseconds(targetMilliseconds);
                await System.Threading.Tasks.Task.Delay(180);

                if (shouldPlay)
                    _vlcMediaPlayer.Play();
                else
                    _vlcMediaPlayer.Pause();

                WriteVideoAudioDiagnostics("Compatibility audio restarted at " + FormatTime(TimeSpan.FromMilliseconds(Math.Max(0, targetMilliseconds))) + " after seek/resync.");
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Compatibility audio restart failed: " + ex.Message);
            }
            finally
            {
                _compatibilityAudioRestartInProgress = false;
            }
        }

        private static bool ShouldCreateAudioFallbackFile(AudioStreamInfo stream)
        {
            if (stream == null)
                return false;

            string codec = (stream.Codec ?? "").Trim().ToLowerInvariant();

            if (!IsReliableWindowsAudioStream(stream))
                return true;

            if (stream.Channels > 2)
                return true;

            return codec is "dts" or "truehd" or "mlp" or "flac" or "opus" or "vorbis" or "pcm_s16le" or "pcm_s24le" or "pcm_bluray" or "alac";
        }

        private async System.Threading.Tasks.Task<string> GetAudioFallbackPathAsync(WStorage.StorageFile sourceFile, AudioStreamInfo stream)
        {
            string folderPath;

            try
            {
                var folder = await WStorage.ApplicationData.Current.LocalFolder.CreateFolderAsync(AudioFallbackFolderName, WStorage.CreationCollisionOption.OpenIfExists);
                folderPath = folder.Path;
            }
            catch
            {
                folderPath = Path.Combine(Path.GetTempPath(), "Zink", AudioFallbackFolderName);
            }

            Directory.CreateDirectory(folderPath);

            var key = sourceFile.Path + "|" + File.GetLastWriteTimeUtc(sourceFile.Path).Ticks + "|" + new FileInfo(sourceFile.Path).Length + "|" + (stream?.AudioTrackNumber ?? 0) + "|aac-stereo-v1";
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            var baseName = SanitizePlaybackCacheFileName(Path.GetFileNameWithoutExtension(sourceFile.Name));
            return Path.Combine(folderPath, baseName + "-" + hash.Substring(0, 16) + ".mkv");
        }

        private static string SanitizePlaybackCacheFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "video";

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
                builder.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '-' : ch);

            var sanitized = builder.ToString().Trim('-', '.', ' ');
            return string.IsNullOrWhiteSpace(sanitized) ? "video" : sanitized;
        }

        private async System.Threading.Tasks.Task TryLaunchCodecInstallerByPrefixAsync(string filePrefix, string friendlyName)
        {
            try
            {
                var installFolder = await WAppModel.Package.Current.InstalledLocation.GetFolderAsync(CodecInstallerFolderName);
                var files = await installFolder.GetFilesAsync();

                WStorage.StorageFile found = null;

                foreach (var file in files)
                {
                    if (file.Name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase) &&
                        file.FileType.Equals(".AppxBundle", StringComparison.OrdinalIgnoreCase))
                    {
                        found = file;
                        break;
                    }
                }

                if (found != null)
                {
                    _waitingForCodecInstallReturn = true;
                    _pendingReloadVideoPath = _currentFile?.Path;
                    _pendingReloadResumeSeconds = 0;

                    await WSystem.Launcher.LaunchFileAsync(found);
                    return;
                }
            }
            catch { }

            try
            {
                if (XamlRoot == null)
                    return;

                var notFoundDialog = new ContentDialog
                {
                    Title = "Installer not found",
                    Content =
                        $"{friendlyName} was not found inside the app package.\n\n" +
                        $"Place the .AppxBundle file inside a folder named '{CodecInstallerFolderName}' in the app package, then try again.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                };

                await notFoundDialog.ShowAsync();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task AutoReloadVideoAfterCodecInstallAsync()
        {
            try
            {
                if (!_waitingForCodecInstallReturn)
                    return;

                if (string.IsNullOrWhiteSpace(_pendingReloadVideoPath))
                {
                    _waitingForCodecInstallReturn = false;
                    return;
                }

                await System.Threading.Tasks.Task.Delay(1200);

                string path = _pendingReloadVideoPath;

                _waitingForCodecInstallReturn = false;
                _pendingReloadVideoPath = null;
                _pendingReloadResumeSeconds = 0;

                var file = await WStorage.StorageFile.GetFileFromPathAsync(path);

                var savedState = GetSavedCodecState(path);
                if (string.Equals(savedState, CodecStatePendingDdp, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyDigitalPlusPrefix))
                        SaveCodecState(path, CodecStateInstalledDdp);
                }
                else if (string.Equals(savedState, CodecStatePendingAc4, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyAC4Prefix))
                        SaveCodecState(path, CodecStateInstalledAc4);
                }

                _suppressCodecPromptOnce = true;
                _pendingResumeSeconds = 0;

                try { SavePositionSeconds(path, 0); } catch { }

                await LoadAndPlayAsync(file);
            }
            catch
            {
                _waitingForCodecInstallReturn = false;
            }
        }

        private void TryPushNowPlaying(bool isPlaying)
        {
            try
            {
                var name = _currentFile?.Name ?? "";
                if (string.IsNullOrWhiteSpace(name)) name = "Video";

                AppPlaybackService.Instance.SetGenericNowPlaying(
                    AppPlaybackService.MediaKind.Video,
                    primary: name,
                    secondary: "Video",
                    artworkUri: null,
                    isPlaying: isPlaying
                );
            }
            catch { }
        }

        private string GetDiscordVideoTitle()
        {
            try
            {
                if (_currentFile == null)
                    return "Video";

                var title = Path.GetFileNameWithoutExtension(_currentFile.Name);
                if (string.IsNullOrWhiteSpace(title))
                    title = _currentFile.Name;

                return string.IsNullOrWhiteSpace(title) ? "Video" : title;
            }
            catch
            {
                return "Video";
            }
        }

        private void RefreshDiscordVideoPresence(bool forcePlaying = false, bool forcePush = false)
        {
            try
            {
                if (_suppressDiscordPresenceRefresh)
                    return;

                if (_useCompatibilityPlaybackEngine)
                {
                    var vlcTitle = GetDiscordVideoTitle();
                    if (_discordPlaybackDuration.TotalSeconds <= 0)
                    {
                        var length = _vlcMediaPlayer?.Length ?? 0;
                        if (length > 0)
                            _discordPlaybackDuration = TimeSpan.FromMilliseconds(length);
                    }

                    if (_discordPlaybackDuration.TotalSeconds <= 0)
                        return;

                    if (!(forcePlaying || (_vlcMediaPlayer?.IsPlaying ?? false)))
                        return;

                    if (!_discordClockReady)
                        SyncDiscordPlaybackClockFromSession(force: true);

                    var vlcNowUtc = DateTime.UtcNow;
                    if (!forcePush && (vlcNowUtc - _lastDiscordPresencePushUtc).TotalSeconds < DiscordPresencePushIntervalSeconds)
                        return;

                    DiscordPresenceService.Instance.SetVideoPresence(
                        vlcTitle,
                        GetDiscordLiveElapsed(),
                        _discordPlaybackDuration,
                        "zink_1024",
                        vlcTitle);

                    _lastDiscordPresencePushUtc = vlcNowUtc;
                    return;
                }

                var player = mediaPlayerElement?.MediaPlayer;
                var session = player?.PlaybackSession;
                if (session == null)
                    return;

                var title = GetDiscordVideoTitle();
                var state = session.PlaybackState;

                if (_discordPlaybackDuration.TotalSeconds <= 0)
                {
                    var sessionDuration = session.NaturalDuration;
                    if (sessionDuration.TotalSeconds > 0)
                        _discordPlaybackDuration = sessionDuration;
                }

                if (_discordPlaybackDuration.TotalSeconds <= 0)
                    return;

                if (!(forcePlaying ||
                    state == MediaPlaybackState.Playing ||
                    state == MediaPlaybackState.Opening ||
                    state == MediaPlaybackState.Buffering))
                    return;

                if (!_discordClockReady)
                    SyncDiscordPlaybackClockFromSession(force: true);

                var nowUtc = DateTime.UtcNow;
                if (!forcePush && (nowUtc - _lastDiscordPresencePushUtc).TotalSeconds < DiscordPresencePushIntervalSeconds)
                    return;

                var position = GetDiscordLiveElapsed();

                DiscordPresenceService.Instance.SetVideoPresence(
                    title,
                    position,
                    _discordPlaybackDuration,
                    "zink_1024",
                    title);

                _lastDiscordPresencePushUtc = nowUtc;
            }
            catch { }
        }

        private void RefreshDiscordPausedPresence(bool forcePush = false)
        {
            try
            {
                if (_useCompatibilityPlaybackEngine)
                {
                    var length = _vlcMediaPlayer?.Length ?? 0;
                    if (length > 0)
                        _discordPlaybackDuration = TimeSpan.FromMilliseconds(length);

                    if (_discordPlaybackDuration.TotalSeconds <= 0)
                        return;

                    var vlcNowUtc = DateTime.UtcNow;
                    if (!forcePush && (vlcNowUtc - _lastDiscordPresencePushUtc).TotalSeconds < 1.0)
                        return;

                    var vlcPosition = TimeSpan.FromSeconds(GetCurrentPlaybackPositionSeconds());
                    if (vlcPosition > _discordPlaybackDuration)
                        vlcPosition = _discordPlaybackDuration;

                    DiscordPresenceService.Instance.SetVideoPresence(
                        GetDiscordVideoTitle(),
                        vlcPosition,
                        _discordPlaybackDuration,
                        "zink_1024",
                        GetDiscordVideoTitle());

                    _lastDiscordPresencePushUtc = vlcNowUtc;
                    return;
                }

                var player = mediaPlayerElement?.MediaPlayer;
                var session = player?.PlaybackSession;
                if (session == null)
                    return;

                var duration = session.NaturalDuration;
                if (duration.TotalSeconds > 0)
                    _discordPlaybackDuration = duration;

                if (_discordPlaybackDuration.TotalSeconds <= 0)
                    return;

                var nowUtc = DateTime.UtcNow;
                if (!forcePush && (nowUtc - _lastDiscordPresencePushUtc).TotalSeconds < 1.0)
                    return;

                var position = session.Position;
                if (position < TimeSpan.Zero)
                    position = TimeSpan.Zero;
                if (position > _discordPlaybackDuration)
                    position = _discordPlaybackDuration;

                var title = GetDiscordVideoTitle();

                DiscordPresenceService.Instance.SetVideoPausedPresence(
                    title,
                    position,
                    _discordPlaybackDuration,
                    "zink_1024",
                    title);

                _lastDiscordPresencePushUtc = nowUtc;
            }
            catch { }
        }

        private void ClearDiscordVideoPresence()
        {
            try
            {
                DiscordPresenceService.Instance.Clear();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<MediaPlaybackItem> BuildPlaybackItemWithNativeSubtitlesAsync(WStorage.StorageFile videoFile)
        {
            var mediaSource = MediaSource.CreateFromStorageFile(videoFile);

            try
            {
                var sidecar = await FindSidecarSubtitleAsync(videoFile);
                if (sidecar != null && ShouldAttachSidecarToNativeRenderer(sidecar))
                {
                    var uri = new Uri(sidecar.Path);
                    var tts = TimedTextSource.CreateFromUri(uri);
                    mediaSource.ExternalTimedTextSources.Add(tts);
                }
            }
            catch { }

            var item = new MediaPlaybackItem(mediaSource);

            item.TimedMetadataTracksChanged += (_, __) =>
            {
                try { ApplyNativeSubtitleTrackState(_nativeSubtitlesEnabled); } catch { }
            };

            item.AudioTracksChanged += (_, __) =>
            {
                try
                {
                    DispatcherQueue.TryEnqueue(() => TryAutoSelectBestAudioTrack());
                }
                catch { }
            };

            return item;
        }

        private bool ShouldAttachSidecarToNativeRenderer(WStorage.StorageFile sidecar)
        {
            try
            {
                if (sidecar == null)
                    return false;

                var appCanRenderSidecar =
                    sidecar.FileType.Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
                    sidecar.FileType.Equals(".vtt", StringComparison.OrdinalIgnoreCase);

                return !appCanRenderSidecar || _subtitleCues.Count == 0;
            }
            catch
            {
                return true;
            }
        }

        private void TryAutoSelectBestAudioTrack()
        {
            try
            {
                var item = _currentPlaybackItem;
                var selected = _selectedAudioStream;

                if (item == null || selected == null)
                {
                    WriteVideoAudioDiagnostics("Audio track auto-select skipped because no playback item or selected audio stream is available.");
                    return;
                }

                var tracks = item.AudioTracks;
                if (tracks == null || tracks.Count == 0)
                {
                    WriteVideoAudioDiagnostics("Audio track auto-select skipped because Windows exposed 0 audio track(s).");
                    return;
                }

                int trackNumber = Math.Max(0, selected.AudioTrackNumber);
                if (trackNumber < tracks.Count)
                {
                    tracks.SelectedIndex = trackNumber;
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} selected: {FormatAudioStreamSummary(selected)}";
                    UpdateSurroundModeStatusText();
                    WriteVideoAudioDiagnostics("Audio track auto-selected. Windows track count: " + tracks.Count + ", selected index: " + tracks.SelectedIndex + ", selected stream: " + FormatAudioStreamSummary(selected));
                }
                else
                {
                    WriteVideoAudioDiagnostics("Audio track auto-select could not select index " + trackNumber + " because Windows exposed " + tracks.Count + " audio track(s). Selected stream: " + FormatAudioStreamSummary(selected));
                }
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Audio track auto-select failed: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task<WStorage.StorageFile> FindSidecarSubtitleAsync(WStorage.StorageFile videoFile)
        {
            try
            {
                var folder = await videoFile.GetParentAsync();
                if (folder == null) return null;

                var baseName = Path.GetFileNameWithoutExtension(videoFile.Name);
                var exts = new[] { ".srt", ".vtt", ".ttml", ".dfxp" };

                foreach (var ext in exts)
                {
                    var candidateName = baseName + ext;
                    var item = await folder.TryGetItemAsync(candidateName);
                    if (item is WStorage.StorageFile sf) return sf;
                }

                var files = await folder.GetFilesAsync();
                foreach (var file in files)
                {
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
                    if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                        continue;

                    if (!fileNameWithoutExtension.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var ext in exts)
                    {
                        if (file.FileType.Equals(ext, StringComparison.OrdinalIgnoreCase))
                            return file;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private async System.Threading.Tasks.Task LoadSidecarSubtitlesForCurrentVideoAsync(WStorage.StorageFile videoFile)
        {
            _currentSubtitleFile = null;
            _subtitleCues.Clear();

            try
            {
                var sidecar = await FindSidecarSubtitleAsync(videoFile);
                if (sidecar != null)
                    await LoadSubtitleFileAsync(sidecar);
            }
            catch { }
        }

        private async System.Threading.Tasks.Task LoadSubtitleFileAsync(WStorage.StorageFile subtitleFile)
        {
            _currentSubtitleFile = subtitleFile;
            _subtitleCues.Clear();

            if (subtitleFile == null)
                return;

            try
            {
                var text = await WStorage.FileIO.ReadTextAsync(subtitleFile);
                var cues = ParseSubtitleText(text, subtitleFile.FileType);
                _subtitleCues.AddRange(cues);
            }
            catch
            {
                _currentSubtitleFile = null;
                _subtitleCues.Clear();
            }
        }

        private static List<SubtitleCue> ParseSubtitleText(string text, string extension)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<SubtitleCue>();

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            if (string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase))
                return ParseSrtSubtitles(text);

            return ParseWebVttSubtitles(text);
        }

        private static List<SubtitleCue> ParseWebVttSubtitles(string text)
        {
            var cues = new List<SubtitleCue>();
            var blocks = Regex.Split(text.Trim(), @"\n{2,}");

            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                int timingLineIndex = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("-->"))
                    {
                        timingLineIndex = i;
                        break;
                    }
                }

                if (timingLineIndex < 0)
                    continue;

                if (!TryParseSubtitleTimingLine(lines[timingLineIndex], out var start, out var end))
                    continue;

                var builder = new StringBuilder();
                for (int i = timingLineIndex + 1; i < lines.Length; i++)
                {
                    var line = CleanSubtitleTextLine(lines[i]);
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (builder.Length > 0)
                        builder.AppendLine();

                    builder.Append(line);
                }

                if (builder.Length > 0)
                    cues.Add(new SubtitleCue { Start = start, End = end, Text = builder.ToString() });
            }

            return cues;
        }

        private static List<SubtitleCue> ParseSrtSubtitles(string text)
        {
            var cues = new List<SubtitleCue>();
            var blocks = Regex.Split(text.Trim(), @"\n{2,}");

            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                int timingLineIndex = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("-->"))
                    {
                        timingLineIndex = i;
                        break;
                    }
                }

                if (timingLineIndex < 0)
                    continue;

                if (!TryParseSubtitleTimingLine(lines[timingLineIndex], out var start, out var end))
                    continue;

                var builder = new StringBuilder();
                for (int i = timingLineIndex + 1; i < lines.Length; i++)
                {
                    var line = CleanSubtitleTextLine(lines[i]);
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (builder.Length > 0)
                        builder.AppendLine();

                    builder.Append(line);
                }

                if (builder.Length > 0)
                    cues.Add(new SubtitleCue { Start = start, End = end, Text = builder.ToString() });
            }

            return cues;
        }

        private static bool TryParseSubtitleTimingLine(string line, out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;

            try
            {
                var parts = line.Split(new[] { "-->" }, StringSplitOptions.None);
                if (parts.Length < 2)
                    return false;

                return TryParseSubtitleTime(parts[0], out start) &&
                       TryParseSubtitleTime(parts[1], out end) &&
                       end > start;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseSubtitleTime(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            var firstTokenEnd = value.IndexOfAny(new[] { ' ', '\t' });
            if (firstTokenEnd > 0)
                value = value.Substring(0, firstTokenEnd);

            value = value.Replace(',', '.');
            var parts = value.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
                return false;

            double seconds;
            int minutes;
            int hours = 0;

            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[0], out hours))
                    return false;

                if (!int.TryParse(parts[1], out minutes))
                    return false;

                if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds))
                    return false;
            }
            else
            {
                if (!int.TryParse(parts[0], out minutes))
                    return false;

                if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds))
                    return false;
            }

            time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }

        private static string CleanSubtitleTextLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return "";

            line = Regex.Replace(line.Trim(), @"<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(line);
        }

        private void ApplyNativeSubtitleTrackState(bool enabled)
        {
            try
            {
                var item = _currentPlaybackItem;
                if (item == null) return;

                var tracks = item.TimedMetadataTracks;
                if (tracks == null || tracks.Count == 0) return;

                var presentationMode = enabled && _subtitleCues.Count == 0
                    ? TimedMetadataTrackPresentationMode.PlatformPresented
                    : TimedMetadataTrackPresentationMode.Disabled;

                int selectedTrackIndex = presentationMode == TimedMetadataTrackPresentationMode.PlatformPresented
                    ? GetPreferredNativeSubtitleTrackIndex(tracks)
                    : -1;

                for (uint i = 0; i < tracks.Count; i++)
                {
                    tracks.SetPresentationMode(
                        i,
                        (int)i == selectedTrackIndex
                            ? TimedMetadataTrackPresentationMode.PlatformPresented
                            : TimedMetadataTrackPresentationMode.Disabled);
                }
            }
            catch { }
        }

        private static int GetPreferredNativeSubtitleTrackIndex(IReadOnlyList<TimedMetadataTrack> tracks)
        {
            try
            {
                if (tracks == null || tracks.Count == 0)
                    return -1;

                for (int i = 0; i < tracks.Count; i++)
                {
                    var language = tracks[i]?.Language ?? "";
                    var label = tracks[i]?.Label ?? "";

                    if (IsEnglishSubtitleTrack(language, label))
                        return i;
                }

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsEnglishSubtitleTrack(string language, string label)
        {
            var combined = $"{language} {label}".Trim();
            if (string.IsNullOrWhiteSpace(combined))
                return false;

            return combined.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                   combined.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
                   combined.StartsWith("en_", StringComparison.OrdinalIgnoreCase) ||
                   combined.IndexOf("english", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateSubtitlesButtonState()
        {
            try
            {
                var label = SubtitlesButtonLabel;
                if (label == null)
                    return;

                if (_nativeSubtitlesEnabled)
                    label.Text = _subtitleCues.Count > 0 ? "Subtitles on" : "Subtitles on";
                else
                    label.Text = _subtitleCues.Count > 0 ? "Subtitles" : "Subtitles";
            }
            catch { }
        }

        private void UpdateSubtitleOverlay(TimeSpan position)
        {
            try
            {
                if (!_nativeSubtitlesEnabled || _subtitleCues.Count == 0)
                {
                    SubtitleOverlay.Visibility = Visibility.Collapsed;
                    SubtitleTextBlock.Text = "";
                    return;
                }

                SubtitleCue activeCue = null;
                foreach (var cue in _subtitleCues)
                {
                    if (position >= cue.Start && position <= cue.End)
                    {
                        activeCue = cue;
                        break;
                    }
                }

                if (activeCue == null || string.IsNullOrWhiteSpace(activeCue.Text))
                {
                    SubtitleOverlay.Visibility = Visibility.Collapsed;
                    SubtitleTextBlock.Text = "";
                    return;
                }

                SubtitleTextBlock.Text = activeCue.Text;
                SubtitleOverlay.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<WStorage.StorageFile> PickSubtitleFileAsync()
        {
            try
            {
                var picker = new WPickers.FileOpenPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                picker.SuggestedStartLocation = WPickers.PickerLocationId.VideosLibrary;

                picker.FileTypeFilter.Add(".srt");
                picker.FileTypeFilter.Add(".vtt");

                return await picker.PickSingleFileAsync();
            }
            catch
            {
                return null;
            }
        }

        private async void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (XamlRoot == null)
                    return;

                var dialog = new ContentDialog
                {
                    Title = "Subtitles",
                    Content = "Turn on subtitles now",
                    PrimaryButtonText = "Turn on subtitles now",
                    SecondaryButtonText = "Choose file",
                    CloseButtonText = "Turn off",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    _nativeSubtitlesEnabled = true;
                    ApplyNativeSubtitleTrackState(true);
                    UpdateSubtitleOverlay(mediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero);
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    var subtitleFile = await PickSubtitleFileAsync();
                    if (subtitleFile != null)
                    {
                        await LoadSubtitleFileAsync(subtitleFile);
                        _nativeSubtitlesEnabled = _subtitleCues.Count > 0;
                        ApplyNativeSubtitleTrackState(_nativeSubtitlesEnabled);
                        UpdateSubtitleOverlay(mediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero);
                    }
                }
                else
                {
                    _nativeSubtitlesEnabled = false;
                    ApplyNativeSubtitleTrackState(false);
                    UpdateSubtitleOverlay(TimeSpan.Zero);
                }

                UpdateSubtitlesButtonState();
            }
            catch { }
        }

        private async void TranslateAudioButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTranslatingAudio)
                return;

            try
            {
                if (_currentFile == null || string.IsNullOrWhiteSpace(_currentFile.Path))
                {
                    await ShowVideoMessageAsync("Translate audio", "Choose a video first, then Zink can translate its spoken audio into UK English.");
                    return;
                }

                var apiKey = GetOpenAiApiKey();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    await ShowVideoMessageAsync("Translate audio", "Add an OPENAI_API_KEY environment variable, restart Zink, then press Translate audio again.");
                    return;
                }

                _isTranslatingAudio = true;
                SetTranslateAudioButtonState("Translating...", false);
                WriteVideoAudioDiagnostics("Dubbed audio translation started for " + FormatVideoFileForLog(_currentFile));

                var translatedVtt = await CreateOrLoadTranslatedAudioSubtitlesAsync(_currentFile, apiKey);
                var cues = ParseWebVttSubtitles(translatedVtt);

                if (cues.Count == 0)
                {
                    await ShowVideoMessageAsync("Translate audio", "Zink translated the audio, but no subtitle timings came back.");
                    return;
                }

                var sourceFile = _currentFile;
                var currentPosition = mediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
                var dubbedVideoPath = await CreateOrLoadTranslatedDubbedVideoAsync(sourceFile, cues, apiKey);

                _subtitleCues.Clear();
                _subtitleCues.AddRange(cues);
                _currentSubtitleFile = null;
                _nativeSubtitlesEnabled = true;
                ApplyNativeSubtitleTrackState(false);
                UpdateSubtitlesButtonState();
                UpdateSubtitleOverlay(currentPosition);

                var dubbedFile = await WStorage.StorageFile.GetFileFromPathAsync(dubbedVideoPath);
                _pendingResumeSeconds = Math.Max(0, currentPosition.TotalSeconds);
                await LoadAndPlayAsync(dubbedFile);
                _subtitleCues.Clear();
                _subtitleCues.AddRange(cues);
                _currentSubtitleFile = null;
                _nativeSubtitlesEnabled = true;
                ApplyNativeSubtitleTrackState(false);
                UpdateSubtitlesButtonState();
                UpdateSubtitleOverlay(currentPosition);

                SetTranslateAudioButtonState("Translated audio", true);
                WriteVideoAudioDiagnostics("Dubbed audio translation loaded with " + cues.Count + " cue(s). Dubbed file: " + dubbedVideoPath);
            }
            catch (Exception ex)
            {
                WriteVideoAudioDiagnostics("Audio translation failed: " + ex.Message);
                await ShowVideoMessageAsync("Translate audio", "Zink could not translate this audio yet: " + ex.Message);
            }
            finally
            {
                _isTranslatingAudio = false;
                if (TranslateAudioButton != null)
                    TranslateAudioButton.IsEnabled = true;
                if (TranslateAudioButtonLabel != null && TranslateAudioButtonLabel.Text == "Translating...")
                    TranslateAudioButtonLabel.Text = "Translate audio";
            }
        }

        private async System.Threading.Tasks.Task<string> CreateOrLoadTranslatedAudioSubtitlesAsync(WStorage.StorageFile videoFile, string apiKey)
        {
            var cacheFolder = await GetAudioTranslationCacheFolderAsync();
            var cacheKey = GetAudioTranslationCacheKey(videoFile, _selectedAudioStream);
            var translationPath = Path.Combine(cacheFolder, cacheKey + ".uk-en.vtt");

            if (File.Exists(translationPath))
            {
                WriteVideoAudioDiagnostics("Using cached audio translation: " + translationPath);
                return await File.ReadAllTextAsync(translationPath);
            }

            var segmentFolder = Path.Combine(cacheFolder, cacheKey + "_segments");
            if (Directory.Exists(segmentFolder))
            {
                try { Directory.Delete(segmentFolder, true); } catch { }
            }
            Directory.CreateDirectory(segmentFolder);

            await ExtractAudioTranslationSegmentsAsync(videoFile, segmentFolder);

            var segmentFiles = Directory.GetFiles(segmentFolder, "segment_*.mp3");
            Array.Sort(segmentFiles, StringComparer.OrdinalIgnoreCase);
            if (segmentFiles.Length == 0)
                throw new InvalidOperationException("No audio could be extracted from this video.");

            var allCues = new List<SubtitleCue>();
            for (int i = 0; i < segmentFiles.Length; i++)
            {
                SetTranslateAudioButtonState($"Translating {i + 1}/{segmentFiles.Length}", false);
                var segmentVtt = await TranslateAudioSegmentToVttAsync(segmentFiles[i], apiKey);
                var segmentCues = ParseWebVttSubtitles(segmentVtt);
                var offset = TimeSpan.FromSeconds(i * AudioTranslationSegmentSeconds);

                foreach (var cue in segmentCues)
                {
                    allCues.Add(new SubtitleCue
                    {
                        Start = cue.Start + offset,
                        End = cue.End + offset,
                        Text = cue.Text
                    });
                }
            }

            var combinedVtt = BuildWebVtt(allCues);
            await File.WriteAllTextAsync(translationPath, combinedVtt, Encoding.UTF8);

            try { Directory.Delete(segmentFolder, true); } catch { }
            return combinedVtt;
        }

        private sealed class AudioDubGroup
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public string Text { get; set; }
        }

        private async System.Threading.Tasks.Task<string> CreateOrLoadTranslatedDubbedVideoAsync(WStorage.StorageFile videoFile, IReadOnlyList<SubtitleCue> cues, string apiKey)
        {
            var cacheFolder = await GetAudioTranslationCacheFolderAsync();
            var cacheKey = GetAudioTranslationCacheKey(videoFile, _selectedAudioStream);
            var dubbedVideoPath = Path.Combine(cacheFolder, cacheKey + ".uk-en-dub.mkv");

            if (File.Exists(dubbedVideoPath))
            {
                WriteVideoAudioDiagnostics("Using cached dubbed audio video: " + dubbedVideoPath);
                return dubbedVideoPath;
            }

            var workFolder = Path.Combine(cacheFolder, cacheKey + "_dub");
            if (Directory.Exists(workFolder))
            {
                try { Directory.Delete(workFolder, true); } catch { }
            }
            Directory.CreateDirectory(workFolder);

            var ffmpegPath = await GetBundledFfmpegPathAsync();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new InvalidOperationException("ffmpeg.exe was not found.");

            var groups = BuildAudioDubGroups(cues);
            if (groups.Count == 0)
                throw new InvalidOperationException("No translated speech was available to dub.");

            var concatFiles = new List<string>();
            var cursor = TimeSpan.Zero;

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var gap = group.Start - cursor;
                if (gap.TotalMilliseconds > 120)
                {
                    var silencePath = Path.Combine(workFolder, "silence_" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + ".mp3");
                    await CreateSilenceAudioAsync(ffmpegPath, silencePath, gap);
                    concatFiles.Add(silencePath);
                    cursor += gap;
                }

                SetTranslateAudioButtonState($"Voicing {i + 1}/{groups.Count}", false);
                var speechPath = Path.Combine(workFolder, "speech_" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + ".mp3");
                await CreateUkEnglishSpeechAsync(group.Text, speechPath, apiKey);
                concatFiles.Add(speechPath);

                var speechDuration = await GetMediaDurationAsync(speechPath);
                cursor += speechDuration > TimeSpan.Zero ? speechDuration : (group.End - group.Start);
            }

            var dubbedAudioPath = Path.Combine(workFolder, "zink_uk_english_dub.mp3");
            await ConcatenateAudioFilesAsync(ffmpegPath, concatFiles, dubbedAudioPath);

            SetTranslateAudioButtonState("Muxing audio", false);
            await MuxDubbedAudioWithVideoAsync(ffmpegPath, videoFile.Path, dubbedAudioPath, dubbedVideoPath);

            try { Directory.Delete(workFolder, true); } catch { }
            return dubbedVideoPath;
        }

        private static List<AudioDubGroup> BuildAudioDubGroups(IReadOnlyList<SubtitleCue> cues)
        {
            var groups = new List<AudioDubGroup>();
            AudioDubGroup current = null;
            var builder = new StringBuilder();

            foreach (var cue in cues)
            {
                var text = NormalizeDubText(cue?.Text);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var startsNewGroup =
                    current == null ||
                    builder.Length + text.Length + 1 > AudioDubMaxGroupCharacters ||
                    (cue.Start - current.End).TotalSeconds > 1.5;

                if (startsNewGroup)
                {
                    if (current != null)
                    {
                        current.Text = builder.ToString().Trim();
                        groups.Add(current);
                    }

                    current = new AudioDubGroup
                    {
                        Start = cue.Start,
                        End = cue.End
                    };
                    builder.Clear();
                }

                if (builder.Length > 0)
                    builder.Append(' ');

                builder.Append(text);
                current.End = cue.End;
            }

            if (current != null)
            {
                current.Text = builder.ToString().Trim();
                groups.Add(current);
            }

            return groups;
        }

        private static string NormalizeDubText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            return Regex.Replace(text, "\\s+", " ").Trim();
        }

        private static async System.Threading.Tasks.Task CreateUkEnglishSpeechAsync(string text, string outputPath, string apiKey)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiAudioSpeechEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var payload = new
            {
                model = "gpt-4o-mini-tts",
                voice = "marin",
                input = text,
                response_format = "mp3",
                instructions = "Speak in clear, natural UK English. Keep the delivery close to a film dub: conversational, measured, and easy to understand."
            };

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                var body = "";
                try { body = Encoding.UTF8.GetString(bytes); } catch { }
                throw new InvalidOperationException("Speech service returned " + (int)response.StatusCode + ": " + TrimForLog(body, 600));
            }

            await File.WriteAllBytesAsync(outputPath, bytes);
        }

        private static async System.Threading.Tasks.Task CreateSilenceAudioAsync(string ffmpegPath, string outputPath, TimeSpan duration)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("lavfi");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("anullsrc=r=24000:cl=mono");
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-q:a");
            startInfo.ArgumentList.Add("9");
            startInfo.ArgumentList.Add("-acodec");
            startInfo.ArgumentList.Add("libmp3lame");
            startInfo.ArgumentList.Add(outputPath);

            await RunProcessOrThrowAsync(startInfo, "Silence audio generation failed");
        }

        private static async System.Threading.Tasks.Task ConcatenateAudioFilesAsync(string ffmpegPath, IReadOnlyList<string> inputPaths, string outputPath)
        {
            var listPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(), "dub_concat.txt");
            var builder = new StringBuilder();

            foreach (var path in inputPaths)
            {
                builder.Append("file '");
                builder.Append(path.Replace("\\", "/").Replace("'", "'\\''"));
                builder.AppendLine("'");
            }

            await File.WriteAllTextAsync(listPath, builder.ToString(), Encoding.UTF8);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("concat");
            startInfo.ArgumentList.Add("-safe");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(listPath);
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("libmp3lame");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("128k");
            startInfo.ArgumentList.Add(outputPath);

            await RunProcessOrThrowAsync(startInfo, "Dubbed audio concatenation failed");
        }

        private static async System.Threading.Tasks.Task MuxDubbedAudioWithVideoAsync(string ffmpegPath, string videoPath, string dubbedAudioPath, string outputPath)
        {
            var tempPath = outputPath + ".tmp.mkv";
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoPath);
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(dubbedAudioPath);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("1:a:0");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("160k");
            startInfo.ArgumentList.Add("-metadata:s:a:0");
            startInfo.ArgumentList.Add("language=eng");
            startInfo.ArgumentList.Add("-metadata:s:a:0");
            startInfo.ArgumentList.Add("title=Zink UK English translation");
            startInfo.ArgumentList.Add(tempPath);

            await RunProcessOrThrowAsync(startInfo, "Dubbed video muxing failed");

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            File.Move(tempPath, outputPath);
        }

        private async System.Threading.Tasks.Task<TimeSpan> GetMediaDurationAsync(string mediaPath)
        {
            try
            {
                var ffprobePath = await GetBundledFfprobePathAsync();
                if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
                    return TimeSpan.Zero;

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("-v");
                startInfo.ArgumentList.Add("error");
                startInfo.ArgumentList.Add("-show_entries");
                startInfo.ArgumentList.Add("format=duration");
                startInfo.ArgumentList.Add("-of");
                startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
                startInfo.ArgumentList.Add(mediaPath);

                using var process = Process.Start(startInfo);
                if (process == null)
                    return TimeSpan.Zero;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                await stderrTask;

                var stdout = (await stdoutTask)?.Trim();
                if (double.TryParse(stdout, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    return TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
            catch { }

            return TimeSpan.Zero;
        }

        private static async System.Threading.Tasks.Task RunProcessOrThrowAsync(ProcessStartInfo startInfo, string errorPrefix)
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException(errorPrefix + ": process could not start.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var completed = await System.Threading.Tasks.Task.WhenAny(waitTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(10)));
            if (completed != waitTask)
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException(errorPrefix + ": timed out.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(errorPrefix + ": " + TrimForLog(stderr + "\n" + stdout, 900));
        }

        private async System.Threading.Tasks.Task<string> GetAudioTranslationCacheFolderAsync()
        {
            try
            {
                var folder = await WStorage.ApplicationData.Current.LocalFolder.CreateFolderAsync(AudioTranslationFolderName, WStorage.CreationCollisionOption.OpenIfExists);
                Directory.CreateDirectory(folder.Path);
                return folder.Path;
            }
            catch
            {
                var folderPath = Path.Combine(Path.GetTempPath(), "Zink", AudioTranslationFolderName);
                Directory.CreateDirectory(folderPath);
                return folderPath;
            }
        }

        private static string GetAudioTranslationCacheKey(WStorage.StorageFile videoFile, AudioStreamInfo stream)
        {
            var path = videoFile?.Path ?? "";
            var length = "0";
            var ticks = "0";

            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    length = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    ticks = info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { }

            var key = path + "|" + length + "|" + ticks + "|" + (stream?.AudioTrackNumber ?? 0) + "|uk-en-v1";
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        }

        private async System.Threading.Tasks.Task ExtractAudioTranslationSegmentsAsync(WStorage.StorageFile videoFile, string segmentFolder)
        {
            var ffmpegPath = await GetBundledFfmpegPathAsync();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new InvalidOperationException("ffmpeg.exe was not found.");

            var outputPattern = Path.Combine(segmentFolder, "segment_%03d.mp3");
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoFile.Path);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:" + Math.Max(0, _selectedAudioStream?.AudioTrackNumber ?? 0));
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("16000");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("48k");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("segment");
            startInfo.ArgumentList.Add("-segment_time");
            startInfo.ArgumentList.Add(AudioTranslationSegmentSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-reset_timestamps");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add(outputPattern);

            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("FFmpeg could not start.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var completed = await System.Threading.Tasks.Task.WhenAny(waitTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(10)));
            if (completed != waitTask)
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException("Audio extraction timed out.");
            }

            var stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException("Audio extraction failed: " + TrimForLog(stderr, 600));
        }

        private static async System.Threading.Tasks.Task<string> TranslateAudioSegmentToVttAsync(string audioPath, string apiKey)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiAudioTranslationsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("whisper-1"), "model");
            form.Add(new StringContent("vtt"), "response_format");
            form.Add(new StringContent("Translate all speech into natural UK English. Keep names, brands, and technical terms accurate."), "prompt");

            await using var stream = File.OpenRead(audioPath);
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(fileContent, "file", Path.GetFileName(audioPath));
            request.Content = form;

            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Translation service returned " + (int)response.StatusCode + ": " + TrimForLog(body, 600));

            return body;
        }

        private static string BuildWebVtt(IReadOnlyList<SubtitleCue> cues)
        {
            var builder = new StringBuilder();
            builder.AppendLine("WEBVTT");
            builder.AppendLine();

            for (int i = 0; i < cues.Count; i++)
            {
                var cue = cues[i];
                builder.AppendLine((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.Append(FormatWebVttTime(cue.Start));
                builder.Append(" --> ");
                builder.AppendLine(FormatWebVttTime(cue.End));
                builder.AppendLine(cue.Text ?? "");
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string FormatWebVttTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
                time = TimeSpan.Zero;

            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}.{3:000}",
                (int)time.TotalHours,
                time.Minutes,
                time.Seconds,
                time.Milliseconds);
        }

        private static string GetOpenAiApiKey()
        {
            try
            {
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (!string.IsNullOrWhiteSpace(key))
                    return key.Trim();
            }
            catch { }

            try
            {
                return (WStorage.ApplicationData.Current.LocalSettings.Values["OPENAI_API_KEY"] as string)?.Trim();
            }
            catch { }

            return null;
        }

        private void SetTranslateAudioButtonState(string text, bool enabled)
        {
            try
            {
                if (TranslateAudioButtonLabel != null)
                    TranslateAudioButtonLabel.Text = text;

                if (TranslateAudioButton != null)
                    TranslateAudioButton.IsEnabled = enabled;
            }
            catch { }
        }

        private async System.Threading.Tasks.Task ShowVideoMessageAsync(string title, string message)
        {
            try
            {
                if (XamlRoot == null)
                    return;

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch { }
        }

        private async void AudioInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (XamlRoot == null)
                    return;

                var content = new StackPanel
                {
                    Spacing = 8,
                    Width = 420
                };

                content.Children.Add(new TextBlock
                {
                    Text = _audioInfoStatus,
                    TextWrapping = TextWrapping.Wrap
                });

                content.Children.Add(new TextBlock
                {
                    Text = "Video",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 10, 0, 0)
                });

                content.Children.Add(new TextBlock
                {
                    Text = _videoInfoStatus,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });

                if (_detectedAudioStreams != null && _detectedAudioStreams.Count > 0)
                {
                    foreach (var stream in _detectedAudioStreams)
                    {
                        content.Children.Add(new TextBlock
                        {
                            Text = $"Track {stream.AudioTrackNumber + 1}: {FormatAudioStreamSummary(stream)}",
                            TextWrapping = TextWrapping.Wrap
                        });
                    }
                }
                else
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "Zink will show 5.1, 7.1, Dolby, and DTS details here when they are detected in the film.",
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                var dialog = new ContentDialog
                {
                    Title = "Audio info",
                    Content = content,
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch { }
        }

        private void VideoInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VideoInfoTextBlock != null)
                    VideoInfoTextBlock.Text = _videoInfoStatus;

                if (VideoInfoPanel != null)
                    VideoInfoPanel.Visibility = Visibility.Visible;

                ControlPanel.Visibility = Visibility.Visible;
                hideControlsTimer?.Stop();
            }
            catch { }
        }

        private void CloseVideoInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VideoInfoPanel != null)
                    VideoInfoPanel.Visibility = Visibility.Collapsed;

                hideControlsTimer?.Start();
            }
            catch { }
        }

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            LogVideoPlaybackPath("Media opened successfully. " + (_detectedVideoMetadata?.PlaybackPath ?? "Native Windows playback path active."));
            WriteVideoAudioDiagnostics("Media opened successfully.\n" + BuildPlaybackSettingsSnapshot(sender) + "\n" + BuildAudioDiagnosticsSnapshot());

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var session = sender.PlaybackSession;
                    var dur = session.NaturalDuration;

                    if (dur.TotalSeconds > 0)
                    {
                        _mediaReadyForSeek = true;
                        SeekSlider.IsEnabled = true;

                        if (_pendingResumeSeconds > 1 && (dur.TotalSeconds - _pendingResumeSeconds) > 2)
                        {
                            try { session.Position = TimeSpan.FromSeconds(_pendingResumeSeconds); } catch { }
                            _pendingResumeSeconds = 0;
                        }

                        _ignoreSliderChange = true;
                        SeekSlider.Minimum = 0;
                        SeekSlider.Maximum = dur.TotalSeconds;
                        SeekSlider.Value = Math.Max(0, Math.Min(session.Position.TotalSeconds, dur.TotalSeconds));
                        _ignoreSliderChange = false;

                        TotalTimeText.Text = FormatTime(dur);
                        CurrentTimeText.Text = FormatTime(session.Position);

                        try
                        {
                            if (mediaPlayerElement?.MediaPlayer != null)
                            {
                                ApplySavedVolume();
                            }
                        }
                        catch { }

                        SyncDiscordPlaybackClockFromSession(force: true);
                        TryAutoSelectBestAudioTrack();
                        WriteVideoAudioDiagnostics("Media opened UI initialization complete.\n" + BuildPlaybackSettingsSnapshot(sender) + "\n" + BuildAudioDiagnosticsSnapshot());
                        ResetDiscordSecondPushTracking();
                        try { _discordPresenceTimer?.Start(); } catch { }
                        RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                    }
                }
                catch { }
            });
        }

        private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            try
            {
                string error = args == null
                    ? "Unknown media failure."
                    : $"{args.Error} {args.ErrorMessage}".Trim();

                LogVideoPlaybackPath("Native Windows playback failed: " + error + " Safe fallback is unavailable inside the app without a supported Windows decoder; try enabling Windows HDR, installing the HEVC Video Extensions, or playing the HDR10/SDR base layer if the file provides one.");
                WriteVideoAudioDiagnostics("Media failed: " + error + "\n" + BuildPlaybackSettingsSnapshot(sender) + "\n" + BuildAudioDiagnosticsSnapshot());
            }
            catch { }
        }

        private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _userPausedDiscordPresence = false;
                    _discordPresenceTimer?.Stop();
                    ResetDiscordPlaybackClock();
                    ClearDiscordVideoPresence();
                }
                catch { }
            });
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_suppressDiscordPresenceRefresh)
                        return;

                    var state = sender.PlaybackState;
                    if (_lastLoggedPlaybackState != state)
                    {
                        _lastLoggedPlaybackState = state;
                        WriteVideoAudioDiagnostics("Playback state changed to " + state + ".\n" + BuildPlaybackSettingsSnapshot(mediaPlayerElement?.MediaPlayer));
                    }

                    if (state == MediaPlaybackState.Playing)
                    {
                        _userPausedDiscordPresence = false;

                        if (!_discordClockReady)
                            SyncDiscordPlaybackClockFromSession(force: true);
                        else
                            SyncDiscordPlaybackClockFromSession(force: false);

                        try { _discordPresenceTimer?.Start(); } catch { }
                        RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                    }
                    else if (state == MediaPlaybackState.Opening || state == MediaPlaybackState.Buffering)
                    {
                        if (!_userPausedDiscordPresence)
                            RefreshDiscordVideoPresence(forcePlaying: true, forcePush: false);
                    }
                    else if (state == MediaPlaybackState.Paused)
                    {
                        if (_userPausedDiscordPresence)
                        {
                            try { _discordPresenceTimer?.Stop(); } catch { }
                            RefreshDiscordPausedPresence(forcePush: true);
                        }
                    }
                }
                catch { }
            });
        }

        private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;

            _isUserSeeking = true;
            SeekSlider.CapturePointer(e.Pointer);

            SetSliderFromPointer(e);
            ApplyLiveSeekFromSlider(force: true);
        }

        private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;
            if (!_isUserSeeking) return;

            SetSliderFromPointer(e);
            ApplyLiveSeekFromSlider(force: false);
        }

        private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;

            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                ApplySeekFromSlider();
            }

            SeekSlider.ReleasePointerCaptures();
        }

        private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                ApplySeekFromSlider();
            }
        }

        private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_ignoreSliderChange) return;

            if (_isUserSeeking)
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        private void SetSliderFromPointer(PointerRoutedEventArgs e)
        {
            try
            {
                var width = SeekSlider.ActualWidth;
                if (width <= 0) return;

                var p = e.GetCurrentPoint(SeekSlider).Position.X;
                var ratio = p / width;
                ratio = Math.Max(0, Math.Min(1, ratio));

                var value = SeekSlider.Minimum + ratio * (SeekSlider.Maximum - SeekSlider.Minimum);

                _ignoreSliderChange = true;
                SeekSlider.Value = value;
                _ignoreSliderChange = false;
            }
            catch { }
        }

        private void ApplySeekFromSlider()
        {
            try
            {
                if (_useCompatibilityPlaybackEngine)
                {
                    var compatPlayer = mediaPlayerElement.MediaPlayer;
                    var compatSession = compatPlayer.PlaybackSession;

                    if (!compatSession.CanSeek) return;

                    var compatDuration = compatSession.NaturalDuration;
                    if (compatDuration.TotalSeconds <= 0) return;

                    var compatSeconds = Math.Max(0, Math.Min(SeekSlider.Value, compatDuration.TotalSeconds));
                    var compatWasPlaying = compatSession.PlaybackState == MediaPlaybackState.Playing;

                    _suppressDiscordPresenceRefresh = true;

                    compatPlayer.Pause();
                    _vlcMediaPlayer?.Pause();

                    var compatPosition = TimeSpan.FromSeconds(compatSeconds);
                    compatSession.Position = compatPosition;
                    if (_vlcMediaPlayer != null)
                        RestartCompatibilityAudioAtNativePositionAsync((long)compatPosition.TotalMilliseconds, compatWasPlaying);
                    CurrentTimeText.Text = FormatTime(compatPosition);
                    UpdateSubtitleOverlay(compatPosition);

                    if (compatWasPlaying)
                    {
                        _userPausedDiscordPresence = false;
                        compatPlayer.Play();
                    }

                    _suppressDiscordPresenceRefresh = false;
                    SyncDiscordPlaybackClockFromSession(force: true);
                    ResetDiscordSecondPushTracking();
                    RefreshDiscordVideoPresence(forcePlaying: compatWasPlaying, forcePush: true);
                    ScheduleCompatibilityAudioResync(700);
                    return;
                }

                var player = mediaPlayerElement.MediaPlayer;
                var session = player.PlaybackSession;

                if (!session.CanSeek) return;

                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                var seconds = Math.Max(0, Math.Min(SeekSlider.Value, dur.TotalSeconds));
                var wasPlaying = session.PlaybackState == MediaPlaybackState.Playing;

                _suppressDiscordPresenceRefresh = true;

                player.Pause();
                session.Position = TimeSpan.FromSeconds(seconds);
                CurrentTimeText.Text = FormatTime(session.Position);
                UpdateSubtitleOverlay(session.Position);

                if (wasPlaying)
                {
                    _userPausedDiscordPresence = false;
                    player.Play();
                }

                _suppressDiscordPresenceRefresh = false;
                SyncDiscordPlaybackClockFromSession(force: true);
                ResetDiscordSecondPushTracking();
                RefreshDiscordVideoPresence(forcePlaying: wasPlaying, forcePush: true);
            }
            catch
            {
                _suppressDiscordPresenceRefresh = false;
            }
        }

        private void ApplyLiveSeekFromSlider(bool force)
        {
            try
            {
                if (_useCompatibilityPlaybackEngine)
                {
                    var compatSession = mediaPlayerElement.MediaPlayer.PlaybackSession;

                    if (!compatSession.CanSeek) return;

                    var compatDuration = compatSession.NaturalDuration;
                    if (compatDuration.TotalSeconds <= 0) return;

                    var compatNowUtc = DateTime.UtcNow;
                    if (!force && (compatNowUtc - _lastLiveSeekUtc).TotalMilliseconds < 35)
                    {
                        CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
                        return;
                    }

                    _lastLiveSeekUtc = compatNowUtc;
                    var compatSeconds = Math.Max(0, Math.Min(SeekSlider.Value, compatDuration.TotalSeconds));
                    var compatPosition = TimeSpan.FromSeconds(compatSeconds);

                    _suppressDiscordPresenceRefresh = true;
                    compatSession.Position = compatPosition;
                    if (_vlcMediaPlayer != null)
                        _vlcMediaPlayer.Time = Math.Max(0, (long)compatPosition.TotalMilliseconds - _vlcAudioBaseOffsetMilliseconds);
                    CurrentTimeText.Text = FormatTime(compatPosition);
                    UpdateSubtitleOverlay(compatPosition);
                    _suppressDiscordPresenceRefresh = false;
                    ScheduleCompatibilityAudioResync(250);
                    return;
                }

                var player = mediaPlayerElement.MediaPlayer;
                var session = player.PlaybackSession;

                if (!session.CanSeek) return;

                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                var nowUtc = DateTime.UtcNow;
                if (!force && (nowUtc - _lastLiveSeekUtc).TotalMilliseconds < 35)
                {
                    CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
                    return;
                }

                _lastLiveSeekUtc = nowUtc;

                var seconds = Math.Max(0, Math.Min(SeekSlider.Value, dur.TotalSeconds));
                var position = TimeSpan.FromSeconds(seconds);

                _suppressDiscordPresenceRefresh = true;

                session.Position = position;
                CurrentTimeText.Text = FormatTime(position);
                UpdateSubtitleOverlay(position);

                _suppressDiscordPresenceRefresh = false;
            }
            catch
            {
                _suppressDiscordPresenceRefresh = false;
            }
        }

        private void UpdateSeekUI()
        {
            try
            {
                if (_isUserSeeking) return;

                var session = mediaPlayerElement.MediaPlayer.PlaybackSession;
                var duration = session.NaturalDuration;

                if (duration.TotalSeconds <= 0) return;

                var pos = session.Position;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (pos > duration) pos = duration;

                _ignoreSliderChange = true;
                SeekSlider.Maximum = duration.TotalSeconds;
                SeekSlider.Value = pos.TotalSeconds;
                _ignoreSliderChange = false;

                CurrentTimeText.Text = FormatTime(pos);
                TotalTimeText.Text = FormatTime(duration);
                UpdateSubtitleOverlay(pos);

                if (_useCompatibilityPlaybackEngine)
                    EnsureCompatibilityAudioSyncedToNativeVideo();

                MaybeSaveResumePosition(duration.TotalSeconds, pos.TotalSeconds);
            }
            catch { }
        }

        private static string FormatTime(TimeSpan t)
            => t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");

        private async void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            var mainWindow = App.MainWindow as MainWindow;
            var sidebarColumnDef = mainWindow?.SidebarColumnReference;

            if (!isFullScreen)
            {
                FullScreenButton.IsEnabled = false;
                try
                {
                    await PlayFullScreenTransitionAsync(true);

                    StartNvidiaOverlaySuppression();
                    appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

                    if (sidebarColumnDef != null)
                        sidebarColumnDef.Width = new GridLength(0);

                    isFullScreen = true;
                    FullScreenLabel.Text = "Exit Fullscreen";
                    await FinishFullScreenTransitionAsync(true);
                }
                finally
                {
                    ResetFullScreenTransitionVisuals();
                    FullScreenButton.IsEnabled = true;
                }
            }
            else
            {
                FullScreenButton.IsEnabled = false;
                try
                {
                    await PlayFullScreenTransitionAsync(false);

                    appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    StopNvidiaOverlaySuppression();

                    if (sidebarColumnDef != null)
                        sidebarColumnDef.Width = new GridLength(250);

                    isFullScreen = false;
                    FullScreenLabel.Text = "Fullscreen";
                    await FinishFullScreenTransitionAsync(false);
                }
                finally
                {
                    ResetFullScreenTransitionVisuals();
                    FullScreenButton.IsEnabled = true;
                }
            }
        }

        private System.Threading.Tasks.Task PlayFullScreenTransitionAsync(bool enteringFullScreen)
        {
            try
            {
                var storyboard = CreateFullScreenTransitionStoryboard(enteringFullScreen, false);
                return BeginStoryboardAsync(storyboard);
            }
            catch
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        private System.Threading.Tasks.Task FinishFullScreenTransitionAsync(bool enteringFullScreen)
        {
            try
            {
                var storyboard = CreateFullScreenTransitionStoryboard(enteringFullScreen, true);
                return BeginStoryboardAsync(storyboard);
            }
            catch
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        private Storyboard CreateFullScreenTransitionStoryboard(bool enteringFullScreen, bool finishing)
        {
            FullScreenAnimationOverlay.Visibility = Visibility.Visible;
            FullScreenAnimationOverlay.RenderTransformOrigin = new global::Windows.Foundation.Point(0.5, 0.5);

            if (!finishing)
            {
                FullScreenAnimationOverlay.Opacity = 0;
                FullScreenOverlayTransform.ScaleX = enteringFullScreen ? 0.92 : 1.05;
                FullScreenOverlayTransform.ScaleY = enteringFullScreen ? 0.92 : 1.05;
                VideoSurfaceTransform.ScaleX = enteringFullScreen ? 0.985 : 1.018;
                VideoSurfaceTransform.ScaleY = enteringFullScreen ? 0.985 : 1.018;
                ControlPanel.Opacity = 1;
            }

            var storyboard = new Storyboard();
            var duration = new Duration(TimeSpan.FromMilliseconds(finishing ? 260 : 220));
            var ease = new CubicEase
            {
                EasingMode = finishing ? EasingMode.EaseOut : EasingMode.EaseInOut
            };

            if (finishing)
            {
                AddDoubleAnimation(storyboard, FullScreenAnimationOverlay, "Opacity", FullScreenAnimationOverlay.Opacity, 0, duration, ease);
                AddDoubleAnimation(storyboard, FullScreenOverlayTransform, "ScaleX", FullScreenOverlayTransform.ScaleX, 1, duration, ease);
                AddDoubleAnimation(storyboard, FullScreenOverlayTransform, "ScaleY", FullScreenOverlayTransform.ScaleY, 1, duration, ease);
                AddDoubleAnimation(storyboard, VideoSurfaceTransform, "ScaleX", VideoSurfaceTransform.ScaleX, 1, duration, ease);
                AddDoubleAnimation(storyboard, VideoSurfaceTransform, "ScaleY", VideoSurfaceTransform.ScaleY, 1, duration, ease);
                AddDoubleAnimation(storyboard, ControlPanel, "Opacity", ControlPanel.Opacity, 1, duration, ease);
            }
            else
            {
                AddDoubleAnimation(storyboard, FullScreenAnimationOverlay, "Opacity", 0, enteringFullScreen ? 0.34 : 0.48, duration, ease);
                AddDoubleAnimation(storyboard, FullScreenOverlayTransform, "ScaleX", FullScreenOverlayTransform.ScaleX, enteringFullScreen ? 1.08 : 0.9, duration, ease);
                AddDoubleAnimation(storyboard, FullScreenOverlayTransform, "ScaleY", FullScreenOverlayTransform.ScaleY, enteringFullScreen ? 1.08 : 0.9, duration, ease);
                AddDoubleAnimation(storyboard, VideoSurfaceTransform, "ScaleX", VideoSurfaceTransform.ScaleX, enteringFullScreen ? 1.02 : 0.975, duration, ease);
                AddDoubleAnimation(storyboard, VideoSurfaceTransform, "ScaleY", VideoSurfaceTransform.ScaleY, enteringFullScreen ? 1.02 : 0.975, duration, ease);
                AddDoubleAnimation(storyboard, ControlPanel, "Opacity", 1, enteringFullScreen ? 0.58 : 0.74, duration, ease);
            }

            return storyboard;
        }

        private static void AddDoubleAnimation(
            Storyboard storyboard,
            DependencyObject target,
            string targetProperty,
            double from,
            double to,
            Duration duration,
            EasingFunctionBase ease)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = duration,
                EasingFunction = ease
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, targetProperty);
            storyboard.Children.Add(animation);
        }

        private System.Threading.Tasks.Task BeginStoryboardAsync(Storyboard storyboard)
        {
            var completionSource = new System.Threading.Tasks.TaskCompletionSource<bool>();

            void StoryboardCompleted(object sender, object args)
            {
                storyboard.Completed -= StoryboardCompleted;
                completionSource.TrySetResult(true);
            }

            storyboard.Completed += StoryboardCompleted;
            storyboard.Begin();

            return completionSource.Task;
        }

        private void ResetFullScreenTransitionVisuals()
        {
            try
            {
                FullScreenAnimationOverlay.Opacity = 0;
                FullScreenAnimationOverlay.Visibility = Visibility.Collapsed;
                FullScreenOverlayTransform.ScaleX = 1;
                FullScreenOverlayTransform.ScaleY = 1;
                VideoSurfaceTransform.ScaleX = 1;
                VideoSurfaceTransform.ScaleY = 1;
                ControlPanel.Opacity = 1;
            }
            catch { }
        }

        private void StartNvidiaOverlaySuppression()
        {
            try
            {
                SuppressNvidiaOverlayWindows();
                _nvidiaOverlaySuppressTimer?.Stop();
                _nvidiaOverlaySuppressTimer?.Start();
            }
            catch { }
        }

        private void StopNvidiaOverlaySuppression()
        {
            try
            {
                _nvidiaOverlaySuppressTimer?.Stop();
            }
            catch { }
        }

        private static void SuppressNvidiaOverlayWindows()
        {
            try
            {
                EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hwnd))
                            return true;

                        GetWindowThreadProcessId(hwnd, out var processId);
                        if (processId == 0)
                            return true;

                        using var process = Process.GetProcessById((int)processId);
                        var processName = process.ProcessName ?? "";
                        var windowText = GetWindowText(hwnd);
                        var className = GetWindowClassName(hwnd);

                        if (IsNvidiaOverlayProcess(processName) ||
                            IsNvidiaOverlayWindow(processName, windowText, className))
                            ShowWindow(hwnd, SW_HIDE);
                    }
                    catch { }

                    return true;
                }, IntPtr.Zero);

                foreach (var processName in GetNvidiaOverlayProcessNames())
                {
                    try
                    {
                        foreach (var process in Process.GetProcessesByName(processName))
                        {
                            try
                            {
                                if (!process.HasExited)
                                    process.Kill();
                            }
                            catch { }
                            finally
                            {
                                try { process.Dispose(); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool IsNvidiaOverlayProcess(string processName)
        {
            return string.Equals(processName, "NVIDIA Overlay", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "NVIDIA Share", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "NVIDIA Web Helper", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "nvsphelper64", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNvidiaOverlayWindow(string processName, string windowText, string className)
        {
            var combined = $"{processName} {windowText} {className}";

            return combined.IndexOf("NVIDIA Overlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NVIDIA Share", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("GeForce Overlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NVIDIA GeForce", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("ShadowPlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NvCamera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NvOverlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NVIDIA Notification", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<string> GetNvidiaOverlayProcessNames()
        {
            yield return "NVIDIA Overlay";
            yield return "NVIDIA Share";
            yield return "NVIDIA Web Helper";
            yield return "nvsphelper64";
        }

        private static string GetWindowText(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(512);
                GetWindowText(hwnd, builder, builder.Capacity);
                return builder.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(512);
                GetClassName(hwnd, builder, builder.Capacity);
                return builder.ToString();
            }
            catch
            {
                return "";
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private const int SW_HIDE = 0;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private void FlyoutVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (!_volumeUiReady)
                    return;

                if (!_useCompatibilityPlaybackEngine && mediaPlayerElement?.MediaPlayer == null)
                    return;

                if (_flyoutVolumeSlider == null)
                    return;

                double volume = Math.Max(0, Math.Min(100, _flyoutVolumeSlider.Value)) / 100.0;
                if (_useCompatibilityPlaybackEngine)
                {
                    if (_vlcMediaPlayer != null)
                        _vlcMediaPlayer.Volume = (int)Math.Round(volume * 100.0);
                }
                else
                {
                    mediaPlayerElement.MediaPlayer.Volume = volume;
                }

                if (volume > 0)
                    _lastNonZeroVolume = volume;

                UpdateVolumeUI(volume);
                SaveVolume(volume);
            }
            catch { }
        }

        private void FlyoutMuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_useCompatibilityPlaybackEngine && mediaPlayerElement?.MediaPlayer == null)
                    return;

                double currentVolume = _useCompatibilityPlaybackEngine
                    ? Math.Max(0, Math.Min(100, _vlcMediaPlayer?.Volume ?? 100)) / 100.0
                    : mediaPlayerElement.MediaPlayer.Volume;

                if (currentVolume > 0)
                {
                    _lastNonZeroVolume = currentVolume;

                    if (_flyoutVolumeSlider != null)
                        _flyoutVolumeSlider.Value = 0;
                }
                else
                {
                    double restoreVolume = _lastNonZeroVolume > 0 ? _lastNonZeroVolume : 1.0;

                    if (_flyoutVolumeSlider != null)
                        _flyoutVolumeSlider.Value = Math.Max(0, Math.Min(100, restoreVolume * 100.0));
                }
            }
            catch { }
        }

        private void ApplySavedVolume()
        {
            try
            {
                double savedVolume = 1.0;

                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(VIDEO_VOLUME_KEY, out object value))
                {
                    if (value is double d)
                        savedVolume = d;
                    else if (value is float f)
                        savedVolume = f;
                    else if (value is string s && double.TryParse(s, out var parsed))
                        savedVolume = parsed;
                }

                savedVolume = Math.Max(0, Math.Min(1, savedVolume));

                if (savedVolume > 0)
                    _lastNonZeroVolume = savedVolume;

                if (mediaPlayerElement?.MediaPlayer != null)
                {
                    mediaPlayerElement.MediaPlayer.Volume = savedVolume;
                    mediaPlayerElement.MediaPlayer.IsMuted = false;
                }

                if (_vlcMediaPlayer != null)
                    _vlcMediaPlayer.Volume = (int)Math.Round(savedVolume * 100.0);

                if (_flyoutVolumeSlider != null)
                    _flyoutVolumeSlider.Value = savedVolume * 100.0;

                UpdateVolumeUI(savedVolume);
            }
            catch { }
        }

        private void SaveVolume(double volume)
        {
            try
            {
                WStorage.ApplicationData.Current.LocalSettings.Values[VIDEO_VOLUME_KEY] = volume;
            }
            catch { }
        }

        private string LoadSavedSurroundMode()
        {
            try
            {
                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(VIDEO_SURROUND_MODE_KEY, out object value) &&
                    value is string savedMode)
                {
                    return NormalizeSurroundMode(savedMode);
                }
            }
            catch { }

            return SurroundModeAuto;
        }

        private void SaveSurroundMode(string mode)
        {
            try
            {
                WStorage.ApplicationData.Current.LocalSettings.Values[VIDEO_SURROUND_MODE_KEY] = NormalizeSurroundMode(mode);
            }
            catch { }
        }

        private void SurroundModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string selectedMode = NormalizeSurroundMode(_surroundModeComboBox?.SelectedValue as string);
                _preferredSurroundMode = selectedMode;
                SaveSurroundMode(selectedMode);

                _selectedAudioStream = GetPreferredAudioStream(_detectedAudioStreams, _preferredSurroundMode);
                if (_selectedAudioStream != null)
                {
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} selected: {FormatAudioStreamSummary(_selectedAudioStream)}";
                }
                else
                {
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} is selected, but this video does not expose a matching audio track.";
                }

                TryAutoSelectBestAudioTrack();
                UpdateSurroundModeStatusText();
                RefreshAudioTrackComboBox();
            }
            catch { }
        }

        private void AudioTrackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (!_audioTrackUiReady)
                    return;

                if (_audioTrackComboBox?.SelectedItem is not AudioStreamInfo selected)
                    return;

                _selectedAudioStream = selected;
                _audioInfoStatus = $"Selected: {FormatAudioStreamSummary(selected)}";
                WriteVideoAudioDiagnostics("Audio track manually selected: " + FormatAudioStreamSummary(selected));

                TryAutoSelectBestAudioTrack();
                ConfigureDirectAudioSupportForSelectedStream();
                UpdateSurroundModeStatusText();
            }
            catch { }
        }

        private void RefreshAudioTrackComboBox()
        {
            try
            {
                if (_audioTrackComboBox == null)
                    return;

                _audioTrackUiReady = false;

                if (_detectedAudioStreams == null || _detectedAudioStreams.Count == 0)
                {
                    _audioTrackComboBox.ItemsSource = null;
                    _audioTrackComboBox.PlaceholderText = "No audio tracks detected";
                    _audioTrackComboBox.IsEnabled = false;
                    return;
                }

                _audioTrackComboBox.ItemsSource = _detectedAudioStreams;
                _audioTrackComboBox.SelectedItem = _selectedAudioStream;
                _audioTrackComboBox.IsEnabled = true;
                WriteVideoAudioDiagnostics("Audio track picker refreshed with " + _detectedAudioStreams.Count + " detected stream(s). Selected: " + (_selectedAudioStream == null ? "none" : FormatAudioStreamSummary(_selectedAudioStream)));
            }
            catch { }
            finally
            {
                _audioTrackUiReady = true;
            }
        }

        private void UpdateSurroundModeStatusText()
        {
            try
            {
                if (_surroundModeStatusText == null)
                    return;

                if (_selectedAudioStream != null)
                {
                    _surroundModeStatusText.Text =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)}: {FormatAudioStreamSummary(_selectedAudioStream)}";
                    return;
                }

                _surroundModeStatusText.Text = string.IsNullOrWhiteSpace(_audioInfoStatus)
                    ? $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)}: no audio track detected yet."
                    : _audioInfoStatus;
            }
            catch { }
        }

        private void UpdateVolumeUI(double volume)
        {
            try
            {
                int percent = (int)Math.Round(volume * 100.0);

                if (_flyoutVolumeText != null)
                    _flyoutVolumeText.Text = percent + "%";

                if (_flyoutMuteButton != null)
                    _flyoutMuteButton.Content = percent == 0 ? "Unmute" : "Mute";

                if (SoundButton != null)
                    SoundButton.Content = percent == 0 ? "Sound" : $"Sound {percent}%";
            }
            catch { }
        }

        private void WriteVideoAudioDiagnostics(string message)
        {
            try
            {
                DiagnosticLogService.WriteLine("[VideoAudio] " + message);
            }
            catch { }
        }

        private static string FormatVideoFileForLog(WStorage.StorageFile file)
        {
            if (file == null)
                return "(no file)";

            var path = string.IsNullOrWhiteSpace(file.Path) ? "(no path)" : file.Path;
            return file.Name + " | " + path;
        }

        private string BuildAudioDiagnosticsSnapshot()
        {
            var builder = new StringBuilder();
            builder.AppendLine("File: " + FormatVideoFileForLog(_currentFile));
            builder.AppendLine("Preferred surround mode: " + _preferredSurroundMode);
            builder.AppendLine("Audio status: " + (_audioInfoStatus ?? ""));
            builder.AppendLine("Detected audio streams: " + (_detectedAudioStreams == null ? 0 : _detectedAudioStreams.Count));
            builder.AppendLine("Selected audio stream: " + (_selectedAudioStream == null ? "none" : FormatAudioStreamSummary(_selectedAudioStream)));
            builder.Append(FormatAudioStreamsForLog(_detectedAudioStreams));
            return builder.ToString().TrimEnd();
        }

        private string BuildPlaybackSettingsSnapshot(MediaPlayer player)
        {
            var builder = new StringBuilder();
            try
            {
                builder.AppendLine("MediaPlayer volume: " + (player == null ? "n/a" : Math.Round(player.Volume * 100.0) + "%"));
                builder.AppendLine("MediaPlayer muted: " + (player == null ? "n/a" : player.IsMuted.ToString()));

                var session = player?.PlaybackSession;
                if (session != null)
                {
                    builder.AppendLine("Playback state: " + session.PlaybackState);
                    builder.AppendLine("Position: " + FormatTime(session.Position));
                    builder.AppendLine("Duration: " + (session.NaturalDuration.TotalSeconds > 0 ? FormatTime(session.NaturalDuration) : "unknown"));
                    builder.AppendLine("Can seek: " + session.CanSeek);
                }

                var item = _currentPlaybackItem;
                var tracks = item?.AudioTracks;
                if (tracks != null)
                    builder.AppendLine("Windows audio tracks exposed: " + tracks.Count + ", selected index: " + tracks.SelectedIndex);
                else
                    builder.AppendLine("Windows audio tracks exposed: none yet");
            }
            catch (Exception ex)
            {
                builder.AppendLine("Playback settings snapshot failed: " + ex.Message);
            }

            return builder.ToString().TrimEnd();
        }

        private static string FormatAudioStreamsForLog(IReadOnlyList<AudioStreamInfo> streams)
        {
            if (streams == null || streams.Count == 0)
                return "Audio streams: none";

            var builder = new StringBuilder();
            builder.AppendLine("Audio streams:");

            foreach (var stream in streams)
            {
                builder.Append("  - ");
                builder.Append("ffprobe stream ");
                builder.Append(stream.StreamIndex);
                builder.Append(", track ");
                builder.Append(stream.AudioTrackNumber + 1);
                builder.Append(": ");
                builder.Append(FormatAudioStreamSummary(stream));
                builder.Append("; likely Windows playable: ");
                builder.Append(IsLikelyWindowsPlayableAudioStream(stream));
                builder.Append("; reliable Windows audio: ");
                builder.Append(IsReliableWindowsAudioStream(stream));
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static string TrimForLog(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            if (text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength) + "...";
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            try
            {
                try
                {
                    if (mediaPlayerElement?.MediaPlayer != null)
                    {
                        ApplySavedVolume();
                    }
                }
                catch { }

                if (e?.Parameter is WStorage.StorageFile file)
                {
                    QueueActivatedVideoLoad(file);
                }
                else if (e?.Parameter is string path && !string.IsNullOrWhiteSpace(path))
                {
                    QueueActivatedVideoLoad(path);
                }
            }
            catch { }
        }

        private void QueueActivatedVideoLoad(object parameter)
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Yield();

                    if (parameter is WStorage.StorageFile file)
                    {
                        await LoadAndPlayAsync(file);
                    }
                    else if (parameter is string path && !string.IsNullOrWhiteSpace(path))
                    {
                        var fileFromPath = await WStorage.StorageFile.GetFileFromPathAsync(path);
                        await LoadAndPlayAsync(fileFromPath);
                    }
                }
                catch (Exception ex)
                {
                    WriteVideoAudioDiagnostics("Queued video activation load failed: " + ex.Message);
                }
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            try
            {
                if (isFullScreen)
                {
                    var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    StopNvidiaOverlaySuppression();

                    var mainWindow = App.MainWindow as MainWindow;
                    if (mainWindow?.SidebarColumnReference != null)
                        mainWindow.SidebarColumnReference.Width = new GridLength(250);

                    isFullScreen = false;
                }

                ForceSaveResumePositionNow_Video();

                _positionTimer?.Stop();
                hideControlsTimer?.Stop();
                _videoBadgeHideTimer?.Stop();
                _discordPresenceTimer?.Stop();

                var mp = mediaPlayerElement?.MediaPlayer;
                if (mp != null)
                {
                    try { SaveVolume(mp.Volume); } catch { }
                    try { mp.Pause(); } catch { }
                    try { mp.Source = null; } catch { }
                    try { mp.PlaybackSession.Position = TimeSpan.Zero; } catch { }
                }

                if (_vlcMediaPlayer != null)
                {
                    try { SaveVolume(Math.Max(0, Math.Min(100, _vlcMediaPlayer.Volume)) / 100.0); } catch { }
                    try { _vlcMediaPlayer.Stop(); } catch { }
                    try { _vlcAudioMedia?.Dispose(); _vlcAudioMedia = null; } catch { }
                }

                try { mediaPlayerElement.Source = null; } catch { }
                try { mediaPlayerElement.Visibility = Visibility.Visible; } catch { }

                _currentPlaybackItem = null;
                _useCompatibilityPlaybackEngine = false;
                _vlcReadyForSeek = false;
                _vlcPlaybackEnded = false;
                try { VideoFormatBadge.Visibility = Visibility.Collapsed; } catch { }
                _mediaReadyForSeek = false;
                _codecPromptAlreadyShownForCurrentFile = false;
                _lastCodecPromptedPath = null;
                _videoSupportPromptAlreadyShownForCurrentFile = false;
                _lastVideoSupportPromptedPath = null;
                _userPausedDiscordPresence = false;
                _forceStartFromBeginningOnNextLoad = false;
                ResetDiscordPlaybackClock();

                try { AppPlaybackService.Instance.ClearIfKind(AppPlaybackService.MediaKind.Video); } catch { }
                ClearDiscordVideoPresence();
            }
            catch { }

            base.OnNavigatedFrom(e);
        }

        private static string MakeKey(string path)
        {
            try
            {
                using var sha1 = SHA1.Create();
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(path ?? ""));
                return VIDEO_POS_PREFIX + Convert.ToHexString(bytes);
            }
            catch
            {
                return VIDEO_POS_PREFIX + (path ?? "").GetHashCode().ToString();
            }
        }

        private static double GetSavedPositionSeconds(string path)
        {
            try
            {
                var key = MakeKey(path);
                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object val))
                {
                    if (val is double d) return d;
                    if (val is float f) return f;
                    if (val is string s && double.TryParse(s, out var p)) return p;
                }
            }
            catch { }
            return 0;
        }

        private static void SavePositionSeconds(string path, double seconds)
        {
            try
            {
                var key = MakeKey(path);
                WStorage.ApplicationData.Current.LocalSettings.Values[key] = seconds;
            }
            catch { }
        }

        private static string MakeCodecStateKey(string path)
        {
            try
            {
                using var sha1 = SHA1.Create();
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(path ?? ""));
                return VIDEO_CODEC_STATE_PREFIX + Convert.ToHexString(bytes);
            }
            catch
            {
                return VIDEO_CODEC_STATE_PREFIX + (path ?? "").GetHashCode().ToString();
            }
        }

        private static string GetSavedCodecState(string path)
        {
            try
            {
                var key = MakeCodecStateKey(path);
                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object val))
                {
                    if (val is string s)
                        return s;
                }
            }
            catch { }

            return null;
        }

        private static void SaveCodecState(string path, string state)
        {
            try
            {
                var key = MakeCodecStateKey(path);
                WStorage.ApplicationData.Current.LocalSettings.Values[key] = state ?? "";
            }
            catch { }
        }

        private static void ClearSavedCodecState(string path)
        {
            try
            {
                var key = MakeCodecStateKey(path);
                WStorage.ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            catch { }
        }

        private static string NormalizePackagePrefix(string prefix)
        {
            try
            {
                return (prefix ?? "").Trim().TrimEnd('_');
            }
            catch
            {
                return prefix ?? "";
            }
        }

        private async System.Threading.Tasks.Task<bool> IsCodecExtensionInstalledAsync(string packagePrefix)
        {
            try
            {
                return await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var normalizedPrefix = NormalizePackagePrefix(packagePrefix);
                        if (string.IsNullOrWhiteSpace(normalizedPrefix))
                            return false;

                        var manager = new WDeployment.PackageManager();

                        foreach (var pkg in manager.FindPackagesForUser(string.Empty))
                        {
                            try
                            {
                                var name = pkg?.Id?.Name ?? "";
                                var familyName = pkg?.Id?.FamilyName ?? "";
                                var fullName = pkg?.Id?.FullName ?? "";

                                if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                                    familyName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                                    fullName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    return false;
                });
            }
            catch
            {
                return false;
            }
        }

        private void MaybeSaveResumePosition(double durationSeconds, double posSeconds)
        {
            try
            {
                if (_currentFile == null || string.IsNullOrWhiteSpace(_currentFile.Path)) return;

                var state = mediaPlayerElement?.MediaPlayer?.PlaybackSession?.PlaybackState ?? MediaPlaybackState.None;
                if (_useCompatibilityPlaybackEngine)
                {
                    if (!(_vlcMediaPlayer?.IsPlaying ?? false) && !_userPausedDiscordPresence)
                        return;
                }
                else
                {
                    if (state != MediaPlaybackState.Playing && state != MediaPlaybackState.Paused)
                        return;
                }

                if (durationSeconds <= 0) return;
                if (posSeconds < 1) return;

                if ((durationSeconds - posSeconds) < 2.0)
                    return;

                var now = DateTime.UtcNow;
                if ((now - _lastPosSaveUtc).TotalSeconds < 1.5) return;
                if (_lastSavedPosSeconds >= 0 && Math.Abs(posSeconds - _lastSavedPosSeconds) < 1.0) return;

                _lastPosSaveUtc = now;
                _lastSavedPosSeconds = posSeconds;

                SavePositionSeconds(_currentFile.Path, posSeconds);
            }
            catch { }
        }
    }
}

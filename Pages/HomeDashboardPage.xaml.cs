using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

using WStorage = global::Windows.Storage;
using WPickers = global::Windows.Storage.Pickers;
using WFileProps = global::Windows.Storage.FileProperties;
using WPerms = global::Windows.Storage.AccessCache;

using WinRT.Interop;
using Zink.Services.NativeCalling;
using Zink.Services;
using Zink.Services.Social;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;

namespace Zink.Pages
{
    public sealed partial class HomeDashboardPage : Page
    {
        public sealed class RecentItem
        {
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public string RightText { get; set; } = "";
            public string? PayloadPath { get; set; }
            public string? PayloadToken { get; set; }
            public string? Kind { get; set; }
        }

        private sealed class MissedCallNotification
        {
            public string CallId { get; set; } = "";
            public string CallerName { get; set; } = "";
            public DateTimeOffset WhenUtc { get; set; }
        }

        private readonly ObservableCollection<RecentItem> _recent = new();
        private RecentItem? _lastPlayable;
        private readonly DispatcherTimer _nowPlayingTimer = new();
        private string _lastThumbIdentity = "";
        private string _lastSpotifyArtLookupIdentity = "";
        private GlobalSystemMediaTransportControlsSessionManager? _mediaSessionManager;
        private bool _spotifyDesktopRefreshInFlight;
        private string _lastSpotifyDesktopDisplay = "";
        private DateTimeOffset _lastSpotifyDiagnosticAtUtc;
        private DateTimeOffset _lastSpotifyDesktopRefreshQueuedUtc = DateTimeOffset.MinValue;
        private readonly List<MissedCallNotification> _missedCalls = new();

        private static readonly HashSet<string> MusicExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".wma", ".alac", ".aiff", ".opus"
        };

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".m2ts", ".3gp"
        };

        private static WStorage.ApplicationDataContainer Settings => WStorage.ApplicationData.Current.LocalSettings;

        private const string LS_LastKind = "HomeDash_LastKind";
        private const string LS_LastPath = "HomeDash_LastPath";
        private const string LS_LastTitle = "HomeDash_LastTitle";
        private const string LS_LastSubtitle = "HomeDash_LastSubtitle";
        private const string LS_LastToken = "HomeDash_LastToken";
        private const string LS_SharedRadioVolume = "RadioPageVolume";

        private const string K_ShowHeroInsights = "Dash_ShowHeroInsights";
        private const string K_ShowPowerTools = "Dash_ShowPowerTools";
        private const string K_ShowRecentActivity = "Dash_ShowRecentActivity";
        private const string K_QuickStartSeen = "Dash_QuickStartSeen";
        private const string K_QuickStartMode = "Dash_QuickStartMode";
        private static string K_Tile(string id) => $"Dash_Tile_{id}";

        private bool _isUpdatingHomeRadioVolumeUi;

        public HomeDashboardPage()
        {
            InitializeComponent();

            RecentList.ItemsSource = _recent;

            ActivityHub.EnsureLoaded();
            ActivityHub.Changed -= ActivityHub_Changed;
            ActivityHub.Changed += ActivityHub_Changed;

            AppPlaybackService.Instance.NowPlayingChanged -= AppPlaybackService_NowPlayingChanged;
            AppPlaybackService.Instance.NowPlayingChanged += AppPlaybackService_NowPlayingChanged;

            AppPlaybackService.Instance.IsPlayingChanged -= AppPlaybackService_IsPlayingChanged;
            AppPlaybackService.Instance.IsPlayingChanged += AppPlaybackService_IsPlayingChanged;

            DiagnosticLogService.StateChanged -= DiagnosticLogService_StateChanged;
            DiagnosticLogService.StateChanged += DiagnosticLogService_StateChanged;

            SpotifyControllerService.Instance.TrackChanged -= SpotifyControllerService_TrackChanged;
            SpotifyControllerService.Instance.TrackChanged += SpotifyControllerService_TrackChanged;

            SpotifyControllerService.Instance.PlayingChanged -= SpotifyControllerService_PlayingChanged;
            SpotifyControllerService.Instance.PlayingChanged += SpotifyControllerService_PlayingChanged;

            NativeSignalingBridge.Instance.EnsureHooked();
            NativeSignalingBridge.Instance.IncomingCallReceived -= NativeSignalingBridge_IncomingCallReceived;
            NativeSignalingBridge.Instance.IncomingCallReceived += NativeSignalingBridge_IncomingCallReceived;

            Loaded += HomeDashboardPage_Loaded;
            Unloaded += HomeDashboardPage_Unloaded;

            _nowPlayingTimer.Interval = TimeSpan.FromSeconds(1);
            _nowPlayingTimer.Tick += NowPlayingTimer_Tick;

            ApplyDashboardCustomisation();

            SetHeroEmptyState();
            RestoreHeroFromSettings();
            InitializeHomeRadioVolumeUi();

            RefreshFromActivityHub();
            RefreshNowPlayingFromServices();
            RefreshNotifications();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            ApplyDashboardCustomisation();
            RestoreHeroFromSettings();
            InitializeHomeRadioVolumeUi();
            RefreshFromActivityHub();
            RefreshNowPlayingFromServices();
            RefreshNotifications();
        }

        private void HomeDashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _nowPlayingTimer.Start();
                InitializeHomeRadioVolumeUi();
                RefreshNowPlayingFromServices();
                RefreshNotifications();
                RefreshQuickStartMode();
                _ = ShowQuickStartDialogIfNeededAsync();
            }
            catch { }
        }

        private void HomeDashboardPage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _nowPlayingTimer.Stop();
            }
            catch { }

            try
            {
                SpotifyControllerService.Instance.TrackChanged -= SpotifyControllerService_TrackChanged;
                SpotifyControllerService.Instance.PlayingChanged -= SpotifyControllerService_PlayingChanged;
                DiagnosticLogService.StateChanged -= DiagnosticLogService_StateChanged;
                NativeSignalingBridge.Instance.IncomingCallReceived -= NativeSignalingBridge_IncomingCallReceived;
            }
            catch { }
        }

        private void NativeSignalingBridge_IncomingCallReceived(object? sender, IncomingCallEventArgs e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    var caller = GetCallerDisplayName(e.FromDisplayName, e.FromUsername, e.FromUserId);
                    if (!_missedCalls.Any(call => string.Equals(call.CallId, e.CallId, StringComparison.Ordinal)))
                    {
                        _missedCalls.Insert(0, new MissedCallNotification
                        {
                            CallId = e.CallId,
                            CallerName = caller,
                            WhenUtc = DateTimeOffset.UtcNow
                        });
                    }

                    if (_missedCalls.Count > 5)
                        _missedCalls.RemoveRange(5, _missedCalls.Count - 5);

                    RefreshNotifications();
                });
            }
            catch { }
        }

        private void NowPlayingTimer_Tick(object? sender, object e)
        {
            RefreshNowPlayingFromServices();
        }

        private void ActivityHub_Changed(object? sender, EventArgs e)
        {
            RefreshFromActivityHub();
            RefreshNowPlayingFromServices();
            RefreshNotifications();
        }

        private void DiagnosticLogService_StateChanged(object? sender, EventArgs e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(RefreshNotifications);
            }
            catch { }
        }

        private void AppPlaybackService_NowPlayingChanged(object? sender, EventArgs e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    RefreshNowPlayingFromServices();
                });
            }
            catch { }
        }

        private void AppPlaybackService_IsPlayingChanged(object? sender, EventArgs e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    RefreshNowPlayingFromServices();
                });
            }
            catch { }
        }

        private void SpotifyControllerService_TrackChanged(object? sender, SpotifyControllerService.TrackInfo e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    RefreshNowPlayingFromServices();
                });
            }
            catch { }
        }

        private void SpotifyControllerService_PlayingChanged(object? sender, bool e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    RefreshNowPlayingFromServices();
                });
            }
            catch { }
        }

        private void RefreshFromActivityHub()
        {
            try
            {
                _recent.Clear();

                foreach (var a in ActivityHub.Recent)
                {
                    var kind = ToDashKind(a.Type, a.Payload);

                    _recent.Add(new RecentItem
                    {
                        Title = a.Title ?? "",
                        Subtitle = a.Subtitle ?? "",
                        PayloadPath = a.Payload,
                        Kind = kind,
                        RightText = FormatWhen(a.When)
                    });
                }

                if (!HasLiveNowPlaying())
                {
                    var latestPlayable = _recent.FirstOrDefault(IsPlayableForHero);

                    if (latestPlayable != null)
                    {
                        var changed =
                            _lastPlayable == null ||
                            !string.Equals(_lastPlayable.Kind, latestPlayable.Kind, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(_lastPlayable.Title, latestPlayable.Title, StringComparison.Ordinal) ||
                            !string.Equals(_lastPlayable.Subtitle, latestPlayable.Subtitle, StringComparison.Ordinal) ||
                            !string.Equals(_lastPlayable.PayloadPath, latestPlayable.PayloadPath, StringComparison.Ordinal);

                        if (changed)
                            SetHeroPlayableState(latestPlayable, persist: true);
                    }
                }

                try
                {
                    RefreshNotifications();
                }
                catch { }
            }
            catch
            {
            }
        }

        private void RefreshNowPlayingFromServices()
        {
            try
            {
                if (TryRefreshFromSpotifyController())
                    return;

                QueueSpotifyDesktopRefresh();

                if (TryRefreshFromAppPlaybackService())
                    return;

                if (_lastPlayable != null)
                {
                    if (string.Equals(_lastPlayable.Kind, "radio", StringComparison.OrdinalIgnoreCase))
                    {
                        HeroKindText.Text = "Radio";
                        HeroTypeValue.Text = "Radio";
                        HeroPlaybackValue.Text = "Live";
                        HeroDurationValue.Text = "Live";
                        HeroMetaText.Text = "Live stream";
                    }
                    else if (string.Equals(_lastPlayable.Kind, "spotify", StringComparison.OrdinalIgnoreCase))
                    {
                        HeroKindText.Text = "Spotify";
                        HeroTypeValue.Text = "Spotify";
                        HeroPlaybackValue.Text = ResumeButton.IsEnabled ? "Ready" : "Stopped";
                        HeroDurationValue.Text = "-";
                        HeroMetaText.Text = "Spotify playback";
                    }
                    else
                    {
                        HeroPlaybackValue.Text = ResumeButton.IsEnabled ? "Ready" : "Stopped";
                    }

                    RefreshHomeRadioVolumeUi(string.Equals(_lastPlayable.Kind, "radio", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    RefreshHomeRadioVolumeUi(false);
                }
            }
            catch { }
        }

        private bool TryRefreshFromSpotifyController()
        {
            try
            {
                var spotify = SpotifyControllerService.Instance;
                var current = spotify?.Current;

                if (spotify == null || !spotify.IsAttached || current == null)
                    return false;

                var hasState =
                    !string.IsNullOrWhiteSpace(current.Title) ||
                    !string.IsNullOrWhiteSpace(current.Artist) ||
                    !string.IsNullOrWhiteSpace(current.Album);

                if (!hasState)
                    return false;

                var primary = string.IsNullOrWhiteSpace(current.Title) ? "Spotify" : current.Title;

                var secondaryParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(current.Artist))
                    secondaryParts.Add(current.Artist);
                if (!string.IsNullOrWhiteSpace(current.Album))
                    secondaryParts.Add(current.Album);

                var secondary = secondaryParts.Count > 0
                    ? string.Join(" - ", secondaryParts)
                    : "Spotify is active in Zink";

                HeroTitle.Text = primary;
                HeroSubtitle.Text = secondary;
                HeroKindText.Text = "Spotify";
                HeroTypeValue.Text = "Spotify";
                HeroPlaybackValue.Text = spotify.IsPlaying ? "Playing" : "Paused";

                var duration = TimeSpan.FromSeconds(Math.Max(0, current.DurationSec));
                var position = TimeSpan.FromSeconds(Math.Max(0, current.PositionSec));

                if (duration > TimeSpan.Zero)
                {
                    HeroMetaText.Text = $"{FormatClock(position)} - {FormatClock(duration)}";
                    HeroDurationValue.Text = FormatClock(duration);
                }
                else if (position > TimeSpan.Zero)
                {
                    HeroMetaText.Text = FormatClock(position);
                    HeroDurationValue.Text = "-";
                }
                else
                {
                    HeroMetaText.Text = "Spotify playback";
                    HeroDurationValue.Text = "-";
                }

                ResumeButton.IsEnabled = true;

                _ = RefreshHeroArtworkFromSpotifyAsync(current.Artist, current.Title, current.Album, current.ImageUrl);

                _lastPlayable = new RecentItem
                {
                    Title = primary,
                    Subtitle = secondary,
                    RightText = spotify.IsPlaying ? "Now" : "Paused",
                    Kind = "spotify"
                };

                SaveHeroToSettings(_lastPlayable);
                RefreshHomeRadioVolumeUi(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void QueueSpotifyDesktopRefresh()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSpotifyDesktopRefreshQueuedUtc < TimeSpan.FromSeconds(5))
                return;

            if (_spotifyDesktopRefreshInFlight)
                return;

            _lastSpotifyDesktopRefreshQueuedUtc = now;
            _spotifyDesktopRefreshInFlight = true;
            _ = RefreshSpotifyDesktopNowPlayingAsync();
        }

        private async Task RefreshSpotifyDesktopNowPlayingAsync()
        {
            try
            {
                _mediaSessionManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var spotifySession = await FindSpotifySessionAsync(_mediaSessionManager);

                if (spotifySession == null)
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        TryRefreshFromSpotifyProcessFallback();
                    });
                    return;
                }

                var mediaProperties = await spotifySession.TryGetMediaPropertiesAsync();
                var playbackInfo = spotifySession.GetPlaybackInfo();
                var timeline = spotifySession.GetTimelineProperties();

                DispatcherQueue?.TryEnqueue(async () =>
                {
                    var title = string.IsNullOrWhiteSpace(mediaProperties.Title) ? "Spotify" : mediaProperties.Title;
                    var artist = string.IsNullOrWhiteSpace(mediaProperties.Artist) ? "Unknown artist" : mediaProperties.Artist;
                    var album = string.IsNullOrWhiteSpace(mediaProperties.AlbumTitle) ? "" : mediaProperties.AlbumTitle;
                    var status = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        ? "Playing"
                        : playbackInfo.PlaybackStatus.ToString();

                    var subtitleParts = new List<string> { artist };
                    if (!string.IsNullOrWhiteSpace(album))
                        subtitleParts.Add(album);

                    HeroTitle.Text = title;
                    HeroSubtitle.Text = string.Join(" - ", subtitleParts);
                    HeroKindText.Text = "Spotify";
                    HeroTypeValue.Text = "Spotify";
                    HeroPlaybackValue.Text = status;

                    var duration = timeline.EndTime - timeline.StartTime;
                    var position = timeline.Position - timeline.StartTime;
                    if (duration > TimeSpan.Zero)
                    {
                        HeroMetaText.Text = $"{FormatClock(position)} - {FormatClock(duration)}";
                        HeroDurationValue.Text = FormatClock(duration);
                    }
                    else
                    {
                        HeroMetaText.Text = "Spotify desktop app";
                        HeroDurationValue.Text = "-";
                    }

                    ResumeButton.IsEnabled = true;
                    RefreshHomeRadioVolumeUi(false);

                    _lastPlayable = new RecentItem
                    {
                        Title = title,
                        Subtitle = HeroSubtitle.Text,
                        RightText = status,
                        Kind = "spotify"
                    };

                    SaveHeroToSettings(_lastPlayable);

                    var displayKey = $"{title}|{artist}|{album}";
                    if (!string.Equals(displayKey, _lastSpotifyDesktopDisplay, StringComparison.Ordinal))
                    {
                        _lastSpotifyDesktopDisplay = displayKey;
                        await SetSpotifyDesktopThumbnailAsync(mediaProperties.Thumbnail);
                    }
                });
            }
            catch (Exception ex)
            {
                WriteSpotifyDiagnosticsThrottled("Desktop session refresh failed: " + ex.Message);
                DispatcherQueue?.TryEnqueue(() =>
                {
                    TryRefreshFromSpotifyProcessFallback();
                });
            }
            finally
            {
                _spotifyDesktopRefreshInFlight = false;
            }
        }

        private async Task<GlobalSystemMediaTransportControlsSession?> FindSpotifySessionAsync(
            GlobalSystemMediaTransportControlsSessionManager manager)
        {
            GlobalSystemMediaTransportControlsSession? currentPlayingFallback = null;
            var current = manager.GetCurrentSession();
            var diagnostics = new StringBuilder();

            foreach (var session in manager.GetSessions())
            {
                var source = session.SourceAppUserModelId ?? "";
                var status = session.GetPlaybackInfo()?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                var title = "";
                var artist = "";

                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    title = properties.Title ?? "";
                    artist = properties.Artist ?? "";
                }
                catch { }

                diagnostics.Append(source)
                    .Append(" | ")
                    .Append(status)
                    .Append(" | ")
                    .Append(title)
                    .Append(" | ")
                    .AppendLine(artist);

                if (source.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                    IsLikelySpotifySession(source, title, artist))
                {
                    return session;
                }

                if (session == current &&
                    status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist)))
                {
                    currentPlayingFallback = session;
                }
            }

            WriteSpotifyDiagnosticsThrottled("No Spotify-named media session. Available sessions:\n" + diagnostics);
            return currentPlayingFallback;
        }

        private static bool IsLikelySpotifySession(string source, string title, string artist)
        {
            if (source.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                return true;

            if (source.Contains("com.squirrel", StringComparison.OrdinalIgnoreCase) &&
                source.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                return true;

            return !string.IsNullOrWhiteSpace(title) &&
                !string.IsNullOrWhiteSpace(artist) &&
                !source.Contains("Zink", StringComparison.OrdinalIgnoreCase) &&
                (source.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
                 source.Contains("Shell", StringComparison.OrdinalIgnoreCase) ||
                 source.Contains("Windows", StringComparison.OrdinalIgnoreCase));
        }

        private bool TryRefreshFromSpotifyProcessFallback()
        {
            try
            {
                var title = Process.GetProcessesByName("Spotify")
                    .Select(process =>
                    {
                        try { return process.MainWindowTitle; }
                        catch { return ""; }
                    })
                    .FirstOrDefault(IsUsableSpotifyWindowTitle);

                if (string.IsNullOrWhiteSpace(title))
                    return false;

                var trackTitle = title;
                var artist = "Spotify desktop app";
                var separatorIndex = title.IndexOf(" - ", StringComparison.Ordinal);
                if (separatorIndex > 0 && separatorIndex + 3 < title.Length)
                {
                    artist = title[..separatorIndex].Trim();
                    trackTitle = title[(separatorIndex + 3)..].Trim();
                }

                HeroTitle.Text = trackTitle;
                HeroSubtitle.Text = artist;
                HeroKindText.Text = "Spotify";
                HeroTypeValue.Text = "Spotify";
                HeroPlaybackValue.Text = "Playing";
                HeroDurationValue.Text = "-";
                HeroMetaText.Text = "Spotify desktop app";
                ResumeButton.IsEnabled = true;
                RefreshHomeRadioVolumeUi(false);
                HeroThumb.Source = null;
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;

                _lastPlayable = new RecentItem
                {
                    Title = trackTitle,
                    Subtitle = artist,
                    RightText = "Playing",
                    Kind = "spotify"
                };

                SaveHeroToSettings(_lastPlayable);
                return true;
            }
            catch (Exception ex)
            {
                WriteSpotifyDiagnosticsThrottled("Process fallback failed: " + ex.Message);
                return false;
            }
        }

        private static bool IsUsableSpotifyWindowTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            var text = title.Trim();
            return !string.Equals(text, "Spotify", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Spotify Free", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Spotify Premium", StringComparison.OrdinalIgnoreCase);
        }

        private async Task SetSpotifyDesktopThumbnailAsync(IRandomAccessStreamReference? thumbnail)
        {
            try
            {
                if (thumbnail == null)
                {
                    HeroThumb.Source = null;
                    HeroThumb.Visibility = Visibility.Collapsed;
                    HeroThumbFallback.Visibility = Visibility.Visible;
                    return;
                }

                using var stream = await thumbnail.OpenReadAsync();
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                HeroThumb.Source = image;
                HeroThumb.Visibility = Visibility.Visible;
                HeroThumbFallback.Visibility = Visibility.Collapsed;
                _lastThumbIdentity = "spotify-desktop|" + _lastSpotifyDesktopDisplay;
            }
            catch
            {
                HeroThumb.Source = null;
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
            }
        }

        private void WriteSpotifyDiagnosticsThrottled(string message)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSpotifyDiagnosticAtUtc < TimeSpan.FromMinutes(1))
                return;

            _lastSpotifyDiagnosticAtUtc = now;
            DiagnosticLogService.WriteLine("[HomeDashboard:Spotify] " + message);
        }

        private async Task RefreshHeroArtworkFromSpotifyAsync(string? artist, string? title, string? album, string? fallbackImageUrl)
        {
            try
            {
                var lookupIdentity = $"{artist}|{title}|{album}|{fallbackImageUrl}";
                if (string.Equals(lookupIdentity, _lastSpotifyArtLookupIdentity, StringComparison.Ordinal))
                    return;

                _lastSpotifyArtLookupIdentity = lookupIdentity;

                string artistImageUrl = "";
                try
                {
                    if (!string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(album))
                    {
                        artistImageUrl = await SpotifyAuthHelper.GetArtistImageUrlAsync(
                            artist ?? "",
                            title ?? "",
                            album ?? "");
                    }
                }
                catch
                {
                    artistImageUrl = "";
                }

                var chosenImageUrl = !string.IsNullOrWhiteSpace(artistImageUrl)
                    ? artistImageUrl
                    : (fallbackImageUrl ?? "");

                var identity = $"spotify|{chosenImageUrl}";
                if (string.Equals(identity, _lastThumbIdentity, StringComparison.Ordinal))
                    return;

                if (!string.IsNullOrWhiteSpace(chosenImageUrl))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.UriSource = new Uri(chosenImageUrl);
                        HeroThumb.Source = bmp;
                        HeroThumb.Visibility = Visibility.Visible;
                        HeroThumbFallback.Visibility = Visibility.Collapsed;
                        _lastThumbIdentity = identity;
                        return;
                    }
                    catch
                    {
                    }
                }

                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
                _lastThumbIdentity = identity;
            }
            catch
            {
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
            }
        }

        private bool TryRefreshFromAppPlaybackService()
        {
            try
            {
                var appPlayback = AppPlaybackService.Instance;
                var player = MediaPlayerSingleton.Instance;

                if (appPlayback == null)
                    return false;

                if (!HasLiveNowPlaying())
                    return false;

                var kind = appPlayback.CurrentKind switch
                {
                    AppPlaybackService.MediaKind.Music => "music",
                    AppPlaybackService.MediaKind.Video => "video",
                    AppPlaybackService.MediaKind.Radio => "radio",
                    _ => "page"
                };

                var primary = (appPlayback.PrimaryText ?? "").Trim();
                var secondary = (appPlayback.SecondaryText ?? "").Trim();

                if (string.IsNullOrWhiteSpace(primary))
                {
                    primary = kind switch
                    {
                        "music" => "Music",
                        "video" => "Video",
                        "radio" => appPlayback.CurrentStationTitle ?? "Radio",
                        _ => "Nothing playing yet"
                    };
                }

                if (kind == "radio")
                {
                    if (string.IsNullOrWhiteSpace(primary))
                        primary = appPlayback.CurrentStationTitle ?? "Radio";

                    if (string.IsNullOrWhiteSpace(secondary))
                    {
                        var artist = appPlayback.CurrentArtist ?? "";
                        var title = appPlayback.CurrentTitle ?? "";

                        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                            secondary = $"{artist} - {title}";
                        else if (!string.IsNullOrWhiteSpace(title))
                            secondary = title;
                        else if (!string.IsNullOrWhiteSpace(artist))
                            secondary = artist;
                    }
                }

                HeroTitle.Text = primary;
                HeroSubtitle.Text = string.IsNullOrWhiteSpace(secondary)
                    ? GetDefaultSubtitleForKind(kind)
                    : secondary;

                HeroKindText.Text = GetKindDisplay(kind);
                HeroTypeValue.Text = GetKindDisplay(kind);

                if (kind == "radio")
                {
                    HeroPlaybackValue.Text = appPlayback.IsPlaying ? "Live" : "Stopped";
                    HeroDurationValue.Text = "Live";

                    var elapsed = appPlayback.Elapsed;
                    if (elapsed > TimeSpan.Zero)
                        HeroMetaText.Text = $"{FormatClock(elapsed)} elapsed";
                    else
                        HeroMetaText.Text = "Live stream";
                }
                else
                {
                    var session = player?.PlaybackSession;
                    var playbackState = session?.PlaybackState;
                    HeroPlaybackValue.Text = playbackState switch
                    {
                        global::Windows.Media.Playback.MediaPlaybackState.Playing => "Playing",
                        global::Windows.Media.Playback.MediaPlaybackState.Paused => "Paused",
                        global::Windows.Media.Playback.MediaPlaybackState.Buffering => "Buffering",
                        global::Windows.Media.Playback.MediaPlaybackState.Opening => "Opening",
                        _ => (appPlayback.IsPlaying ? "Playing" : "Stopped")
                    };

                    var duration = session?.NaturalDuration ?? TimeSpan.Zero;
                    var position = session?.Position ?? appPlayback.Elapsed;

                    if (duration > TimeSpan.Zero)
                    {
                        HeroMetaText.Text = $"{FormatClock(position)} - {FormatClock(duration)}";
                        HeroDurationValue.Text = FormatClock(duration);
                    }
                    else if (position > TimeSpan.Zero)
                    {
                        HeroMetaText.Text = FormatClock(position);
                        HeroDurationValue.Text = "-";
                    }
                    else
                    {
                        HeroMetaText.Text = GetHeroMetaText(kind);
                        HeroDurationValue.Text = "-";
                    }
                }

                ResumeButton.IsEnabled = true;

                var artUri = kind == "radio"
                    ? (appPlayback.CurrentArtworkUri ?? appPlayback.CurrentStationLogoUri)
                    : (appPlayback.GenericArtworkUri ?? appPlayback.CurrentArtworkUri);

                _ = RefreshHeroArtworkFromServicesAsync(kind, artUri);

                _lastPlayable = BuildRecentItemFromLiveState(kind, primary, HeroSubtitle.Text);

                if (_lastPlayable != null)
                    SaveHeroToSettings(_lastPlayable);

                RefreshHomeRadioVolumeUi(kind == "radio");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task RefreshHeroArtworkFromServicesAsync(string? kind, Uri? artworkUri)
        {
            try
            {
                var identity = $"{kind}|uri|{artworkUri}";
                if (string.Equals(identity, _lastThumbIdentity, StringComparison.Ordinal))
                    return;

                if (artworkUri != null)
                {
                    try
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        bmp.UriSource = artworkUri;
                        HeroThumb.Source = bmp;
                        HeroThumb.Visibility = Visibility.Visible;
                        HeroThumbFallback.Visibility = Visibility.Collapsed;
                        _lastThumbIdentity = identity;
                        return;
                    }
                    catch
                    {
                    }
                }

                if (_lastPlayable != null)
                {
                    await RefreshHeroThumbnailIfNeededAsync(_lastPlayable.Kind, _lastPlayable.PayloadPath, _lastPlayable.PayloadToken);
                    return;
                }

                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
                _lastThumbIdentity = identity;
            }
            catch
            {
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
            }
        }

        private RecentItem? BuildRecentItemFromLiveState(string kind, string primary, string secondary)
        {
            try
            {
                if (kind == "radio")
                {
                    return new RecentItem
                    {
                        Title = primary,
                        Subtitle = secondary,
                        RightText = "Live",
                        Kind = "radio",
                        PayloadPath = null,
                        PayloadToken = null
                    };
                }

                var candidate = _recent.FirstOrDefault(r =>
                    string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Title, primary, StringComparison.Ordinal));

                if (candidate != null)
                {
                    return new RecentItem
                    {
                        Title = candidate.Title,
                        Subtitle = string.IsNullOrWhiteSpace(secondary) ? candidate.Subtitle : secondary,
                        RightText = candidate.RightText,
                        Kind = candidate.Kind,
                        PayloadPath = candidate.PayloadPath,
                        PayloadToken = candidate.PayloadToken
                    };
                }

                var anyPlayable = _recent.FirstOrDefault(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase));
                if (anyPlayable != null)
                {
                    return new RecentItem
                    {
                        Title = primary,
                        Subtitle = string.IsNullOrWhiteSpace(secondary) ? anyPlayable.Subtitle : secondary,
                        RightText = anyPlayable.RightText,
                        Kind = anyPlayable.Kind,
                        PayloadPath = anyPlayable.PayloadPath,
                        PayloadToken = anyPlayable.PayloadToken
                    };
                }

                return new RecentItem
                {
                    Title = primary,
                    Subtitle = secondary,
                    RightText = "Now",
                    Kind = kind
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadBool(string key, bool defaultValue)
        {
            try
            {
                if (Settings.Values.TryGetValue(key, out var v) && v is bool b)
                    return b;
            }
            catch { }
            return defaultValue;
        }

        private static void WriteBool(string key, bool value)
        {
            try { Settings.Values[key] = value; } catch { }
        }

        private static string ReadString(string key, string defaultValue)
        {
            try
            {
                if (Settings.Values.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            catch { }

            return defaultValue;
        }

        private static void WriteString(string key, string value)
        {
            try { Settings.Values[key] = value; } catch { }
        }

        private void ApplyDashboardCustomisation()
        {
            try
            {
                HeroInsightsSection.Visibility = ReadBool(K_ShowHeroInsights, true) ? Visibility.Visible : Visibility.Collapsed;
                PowerToolsSection.Visibility = ReadBool(K_ShowPowerTools, true) ? Visibility.Visible : Visibility.Collapsed;
                RecentActivitySection.Visibility = ReadBool(K_ShowRecentActivity, true) ? Visibility.Visible : Visibility.Collapsed;

                SetTileVisibility(TileBtn_MusicPlayer, "MusicPlayer");
                SetTileVisibility(TileBtn_VideoPlayer, "VideoPlayer");
                SetTileVisibility(TileBtn_Radio, "Radio");

                SetTileVisibility(TileBtn_VideoLibrary, "VideoLibrary");
                SetTileVisibility(TileBtn_MusicLibrary, "MusicLibrary");

                SetTileVisibility(TileBtn_Equalizer, "Equalizer");
                SetTileVisibility(TileBtn_Visualizer, "Visualizer");

                SetTileVisibility(TileBtn_Settings, "Settings");
                SetTileVisibility(TileBtn_VersionHistory, "VersionHistory");
                SetTileVisibility(TileBtn_ZinkConnect, "ZinkConnect");
                SetTileVisibility(TileBtn_Streaming, "Streaming");
                SetTileVisibility(TileBtn_LeaveReview, "LeaveReview");

                SetTileVisibility(TileBtn_Spotify, "Spotify");
                SetTileVisibility(TileBtn_Twitch, "Twitch");
                SetTileVisibility(TileBtn_Xbox, "Xbox");
                SetTileVisibility(TileBtn_Discord, "Discord");
                SetTileVisibility(TileBtn_YouTube, "YouTube");

                SetTileVisibility(TileBtn_Customise, "Customise");
                RefreshQuickStartMode();
            }
            catch { }
        }

        private void SetTileVisibility(Button btn, string id)
        {
            if (btn == null) return;
            btn.Visibility = ReadBool(K_Tile(id), true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetHeroEmptyState()
        {
            try
            {
                HeroTitle.Text = "Your Zink session starts here";
                HeroSubtitle.Text = "Start playback, jump into a call, share your screen, open gaming tools, or send diagnostics when something feels off.";
                HeroKindText.Text = "Idle";
                HeroMetaText.Text = "Nothing active";
                HeroTypeValue.Text = "Nothing";
                HeroPlaybackValue.Text = "Stopped";
                HeroDurationValue.Text = "-";
                ResumeButton.IsEnabled = false;
                ResetClearButton.IsEnabled = false;
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
                if (HomeRadioVolumePanel != null)
                    HomeRadioVolumePanel.Visibility = Visibility.Collapsed;
                if (HomeRadioVolumeValueText != null)
                    HomeRadioVolumeValueText.Text = "100%";
                _lastPlayable = null;
                _lastThumbIdentity = "";
                _lastSpotifyArtLookupIdentity = "";
            }
            catch { }
        }

        private void SaveHeroToSettings(RecentItem item)
        {
            try
            {
                Settings.Values[LS_LastKind] = item.Kind ?? "";
                Settings.Values[LS_LastPath] = item.PayloadPath ?? "";
                Settings.Values[LS_LastTitle] = item.Title ?? "";
                Settings.Values[LS_LastSubtitle] = item.Subtitle ?? "";
                Settings.Values[LS_LastToken] = item.PayloadToken ?? "";
            }
            catch { }
        }

        private void ClearHeroFromSettings()
        {
            try
            {
                Settings.Values.Remove(LS_LastKind);
                Settings.Values.Remove(LS_LastPath);
                Settings.Values.Remove(LS_LastTitle);
                Settings.Values.Remove(LS_LastSubtitle);
                Settings.Values.Remove(LS_LastToken);
            }
            catch { }
        }

        private void RestoreHeroFromSettings()
        {
            try
            {
                var kind = Settings.Values[LS_LastKind] as string;
                var path = Settings.Values[LS_LastPath] as string;
                var title = Settings.Values[LS_LastTitle] as string;
                var subtitle = Settings.Values[LS_LastSubtitle] as string;
                var token = Settings.Values[LS_LastToken] as string;

                if (string.IsNullOrWhiteSpace(kind))
                    return;

                if ((kind == "music" || kind == "video") && string.IsNullOrWhiteSpace(path))
                    return;

                if (kind == "music" || kind == "video")
                {
                    var hasToken = !string.IsNullOrWhiteSpace(token);
                    var hasPathAccess = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

                    if (!hasToken && !hasPathAccess)
                    {
                        ClearHeroFromSettings();
                        return;
                    }
                }

                var item = new RecentItem
                {
                    Kind = kind,
                    PayloadPath = path,
                    PayloadToken = token,
                    Title = string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(path)
                        ? Path.GetFileNameWithoutExtension(path)
                        : (title ?? ""),
                    Subtitle = string.IsNullOrWhiteSpace(subtitle) ? "Resume playback" : subtitle!,
                    RightText = "Resume"
                };

                SetHeroPlayableState(item, persist: false);
            }
            catch { }
        }

        private void SetHeroPlayableState(RecentItem item, bool persist = true)
        {
            HeroTitle.Text = item.Title;
            HeroSubtitle.Text = string.IsNullOrWhiteSpace(item.Subtitle) ? "Ready to resume" : item.Subtitle;
            HeroKindText.Text = GetKindDisplay(item.Kind);
            HeroMetaText.Text = GetHeroMetaText(item.Kind);
            HeroTypeValue.Text = GetKindDisplay(item.Kind);
            HeroPlaybackValue.Text = "Ready";
            HeroDurationValue.Text = string.Equals(item.Kind, "radio", StringComparison.OrdinalIgnoreCase) ? "Live" : "-";
            ResumeButton.IsEnabled = true;
            ResetClearButton.IsEnabled = true;
            _lastPlayable = item;

            if (persist)
                SaveHeroToSettings(item);

            RefreshHomeRadioVolumeUi(string.Equals(item.Kind, "radio", StringComparison.OrdinalIgnoreCase));
            _ = RefreshHeroThumbnailIfNeededAsync(item.Kind, item.PayloadPath, item.PayloadToken);
        }

        private void InitializeHomeRadioVolumeUi()
        {
            try
            {
                var volume = ReadSharedRadioVolume();
                ApplyVolumeToActivePlayback(volume);
                ApplyHomeRadioVolumeToUi(volume);
                RefreshHomeRadioVolumeUi();
            }
            catch
            {
                var volume = ReadSharedRadioVolume();
                ApplyVolumeToActivePlayback(volume);
                ApplyHomeRadioVolumeToUi(volume);
            }
        }

        private void RefreshHomeRadioVolumeUi(bool? isRadioOverride = null)
        {
            try
            {
                var appPlayback = AppPlaybackService.Instance;
                var isRadioPlaying = isRadioOverride
                    ?? (appPlayback != null && appPlayback.CurrentKind == AppPlaybackService.MediaKind.Radio);

                if (HomeRadioVolumePanel != null)
                    HomeRadioVolumePanel.Visibility = isRadioPlaying ? Visibility.Visible : Visibility.Collapsed;

                if (!isRadioPlaying)
                    return;

                var volume = ReadSharedRadioVolume();
                ApplyVolumeToActivePlayback(volume);
                ApplyHomeRadioVolumeToUi(volume);
            }
            catch
            {
            }
        }

        private void HomeRadioVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingHomeRadioVolumeUi)
                return;

            try
            {
                var volume = ClampVolume(e.NewValue / 100d);

                SaveSharedRadioVolume(volume);
                ApplyVolumeToActivePlayback(volume);
                ApplyHomeRadioVolumeToUi(volume);
            }
            catch
            {
            }
        }

        private void ApplyHomeRadioVolumeToUi(double volume)
        {
            try
            {
                _isUpdatingHomeRadioVolumeUi = true;

                var percent = Math.Max(0, Math.Min(100, (int)Math.Round(ClampVolume(volume) * 100d)));

                if (HomeRadioVolumeSlider != null)
                    HomeRadioVolumeSlider.Value = percent;

                if (HomeRadioVolumeValueText != null)
                    HomeRadioVolumeValueText.Text = $"{percent}%";
            }
            catch
            {
            }
            finally
            {
                _isUpdatingHomeRadioVolumeUi = false;
            }
        }

        private static double ClampVolume(double volume)
        {
            if (volume < 0d) return 0d;
            if (volume > 1d) return 1d;
            return volume;
        }

        private static double ReadSharedRadioVolume()
        {
            try
            {
                var serviceVolume = AppPlaybackService.Instance.RadioVolume;
                return ClampVolume(serviceVolume);
            }
            catch
            {
            }

            try
            {
                if (Settings.Values.TryGetValue(LS_SharedRadioVolume, out var value))
                {
                    if (value is double d)
                        return ClampVolume(d);

                    if (value is float f)
                        return ClampVolume(f);

                    if (value is int i)
                        return ClampVolume(i / 100d);

                    if (value is string s && double.TryParse(s, out var parsed))
                        return ClampVolume(parsed > 1d ? parsed / 100d : parsed);
                }
            }
            catch
            {
            }

            return 1.0d;
        }

        private static void SaveSharedRadioVolume(double volume)
        {
            try
            {
                volume = ClampVolume(volume);
                Settings.Values[LS_SharedRadioVolume] = volume;
            }
            catch
            {
            }

            try
            {
                AppPlaybackService.Instance.SetRadioVolume(volume, notifyApplier: false);
            }
            catch
            {
            }
        }

        private void ApplyVolumeToActivePlayback(double volume)
        {
            try
            {
                volume = ClampVolume(volume);

                var appPlayback = AppPlaybackService.Instance;
                if (appPlayback != null)
                {
                    TrySetVolumeOnTarget(appPlayback, volume);

                    var appPlaybackTarget = GetVolumeTargetFromObject(appPlayback);
                    if (appPlaybackTarget != null)
                        TrySetVolumeOnTarget(appPlaybackTarget, volume);

                    TryInvokeVolumeMethod(appPlayback, volume);
                }

                var singletonPlayer = MediaPlayerSingleton.Instance;
                if (singletonPlayer != null)
                    TrySetVolumeOnTarget(singletonPlayer, volume);
            }
            catch
            {
            }
        }

        private double? TryGetActivePlaybackVolume()
        {
            try
            {
                var appPlayback = AppPlaybackService.Instance;
                if (appPlayback != null)
                {
                    var serviceVolume = TryGetVolumeFromTarget(appPlayback);
                    if (serviceVolume.HasValue)
                        return serviceVolume.Value;

                    var serviceTarget = GetVolumeTargetFromObject(appPlayback);
                    var serviceTargetVolume = TryGetVolumeFromTarget(serviceTarget);
                    if (serviceTargetVolume.HasValue)
                        return serviceTargetVolume.Value;
                }

                var singletonPlayer = MediaPlayerSingleton.Instance;
                var singletonVolume = TryGetVolumeFromTarget(singletonPlayer);
                if (singletonVolume.HasValue)
                    return singletonVolume.Value;
            }
            catch
            {
            }

            return null;
        }

        private static object? GetVolumeTargetFromObject(object source)
        {
            try
            {
                var type = source.GetType();

                foreach (var propertyName in new[]
                {
                    "Player",
                    "MediaPlayer",
                    "CurrentPlayer",
                    "PlaybackPlayer",
                    "SharedPlayer",
                    "RadioPlayer",
                    "ActivePlayer"
                })
                {
                    try
                    {
                        var property = type.GetProperty(propertyName);
                        if (property != null && property.CanRead)
                        {
                            var value = property.GetValue(source);
                            if (value != null)
                                return value;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static void TryInvokeVolumeMethod(object target, double volume)
        {
            try
            {
                var type = target.GetType();

                foreach (var methodName in new[]
                {
                    "SetVolume",
                    "ApplyVolume",
                    "UpdateVolume",
                    "SetPlayerVolume",
                    "SetRadioVolume"
                })
                {
                    try
                    {
                        var method = type.GetMethod(methodName);
                        if (method == null)
                            continue;

                        var parameters = method.GetParameters();
                        if (parameters.Length != 1)
                            continue;

                        var parameterType = parameters[0].ParameterType;

                        if (parameterType == typeof(double))
                            method.Invoke(target, new object[] { volume });
                        else if (parameterType == typeof(float))
                            method.Invoke(target, new object[] { (float)volume });
                        else if (parameterType == typeof(int))
                            method.Invoke(target, new object[] { (int)Math.Round(volume * 100d) });
                        else
                            continue;

                        return;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void TrySetVolumeOnTarget(object? target, double volume)
        {
            try
            {
                if (target == null)
                    return;

                volume = ClampVolume(volume);

                if (target is global::Windows.Media.Playback.MediaPlayer mediaPlayer)
                {
                    mediaPlayer.Volume = volume;
                    mediaPlayer.IsMuted = volume <= 0d;
                    return;
                }

                var type = target.GetType();

                foreach (var propertyName in new[]
                {
                    "Volume",
                    "CurrentVolume",
                    "PlayerVolume",
                    "PlaybackVolume",
                    "RadioVolume",
                    "LastVolume",
                    "SavedVolume"
                })
                {
                    try
                    {
                        var volumeProperty = type.GetProperty(propertyName);
                        if (volumeProperty == null || !volumeProperty.CanWrite)
                            continue;

                        if (volumeProperty.PropertyType == typeof(double))
                            volumeProperty.SetValue(target, volume);
                        else if (volumeProperty.PropertyType == typeof(float))
                            volumeProperty.SetValue(target, (float)volume);
                        else if (volumeProperty.PropertyType == typeof(int))
                            volumeProperty.SetValue(target, (int)Math.Round(volume * 100d));
                    }
                    catch
                    {
                    }
                }

                foreach (var propertyName in new[] { "IsMuted", "Muted" })
                {
                    try
                    {
                        var mutedProperty = type.GetProperty(propertyName);
                        if (mutedProperty != null && mutedProperty.CanWrite && mutedProperty.PropertyType == typeof(bool))
                            mutedProperty.SetValue(target, volume <= 0d);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static double? TryGetVolumeFromTarget(object? target)
        {
            try
            {
                if (target == null)
                    return null;

                if (target is global::Windows.Media.Playback.MediaPlayer mediaPlayer)
                    return ClampVolume(mediaPlayer.Volume);

                var type = target.GetType();

                foreach (var propertyName in new[]
                {
                    "Volume",
                    "CurrentVolume",
                    "PlayerVolume",
                    "PlaybackVolume",
                    "RadioVolume",
                    "LastVolume",
                    "SavedVolume"
                })
                {
                    try
                    {
                        var volumeProperty = type.GetProperty(propertyName);
                        if (volumeProperty == null || !volumeProperty.CanRead)
                            continue;

                        var value = volumeProperty.GetValue(target);
                        if (value is double d)
                            return ClampVolume(d);
                        if (value is float f)
                            return ClampVolume(f);
                        if (value is int i)
                            return ClampVolume(i / 100d);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private async Task RefreshHeroThumbnailIfNeededAsync(string? kind, string? path, string? token)
        {
            var identity = $"{kind}|file|{path}|{token}";
            if (string.Equals(identity, _lastThumbIdentity, StringComparison.Ordinal))
                return;

            _lastThumbIdentity = identity;
            await TrySetHeroThumbnailAsync(kind, path, token);
        }

        private async Task TrySetHeroThumbnailAsync(string? kind, string? path, string? token)
        {
            try
            {
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;

                if (string.Equals(kind, "radio", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (kind != "music" && kind != "video")
                    return;

                global::Windows.Storage.StorageFile? file = null;

                if (!string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        file = await WPerms.StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
                    }
                    catch
                    {
                        file = null;
                    }
                }

                if (file == null && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    file = await WStorage.StorageFile.GetFileFromPathAsync(path);
                }

                if (file == null)
                    return;

                var thumbMode = kind == "music"
                    ? WFileProps.ThumbnailMode.MusicView
                    : WFileProps.ThumbnailMode.VideosView;

                var thumb = await file.GetThumbnailAsync(thumbMode, 512);
                if (thumb != null)
                {
                    var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    HeroThumb.Source = bmp;
                    HeroThumb.Visibility = Visibility.Visible;
                    HeroThumbFallback.Visibility = Visibility.Collapsed;
                }

                if (kind == "music")
                {
                    try
                    {
                        var props = await file.Properties.GetMusicPropertiesAsync();
                        if (props.Duration > TimeSpan.Zero && HeroDurationValue.Text == "-")
                        {
                            HeroDurationValue.Text = FormatClock(props.Duration);
                        }
                    }
                    catch { }
                }
                else if (kind == "video")
                {
                    try
                    {
                        var props = await file.Properties.GetVideoPropertiesAsync();
                        if (props.Duration > TimeSpan.Zero && HeroDurationValue.Text == "-")
                        {
                            HeroDurationValue.Text = FormatClock(props.Duration);
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
            }
        }

        private void NavigateFromRecent(RecentItem item)
        {
            var path = item.PayloadPath;

            switch (item.Kind)
            {
                case "music":
                    App.MainWindow.MainFrame.Navigate(typeof(MusicPlayerPage), path);
                    break;

                case "video":
                    App.MainWindow.MainFrame.Navigate(typeof(VideoPlayerPage), path);
                    break;

                case "radio":
                    App.MainWindow.MainFrame.Navigate(typeof(RadioPage));
                    break;

                case "spotify":
                    if (!TryNavigateToAny(
                        "Zink.Pages.SpotifyPage",
                        "Zink.Pages.SpotifyLoginPage",
                        "Zink.Pages.SpotifyHomePage",
                        "Zink.Pages.SpotifyBrowserPage"))
                    {
                        try { App.MainWindow.MainFrame.Navigate(typeof(SpotifyLoginPage)); } catch { }
                    }
                    break;
            }
        }

        private void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPlayable == null)
                RestoreHeroFromSettings();

            if (_lastPlayable == null)
                return;

            if (string.IsNullOrWhiteSpace(_lastPlayable.Kind) && !string.IsNullOrWhiteSpace(_lastPlayable.PayloadPath))
                _lastPlayable.Kind = DetectKindFromPath(_lastPlayable.PayloadPath);

            NavigateFromRecent(_lastPlayable);
        }

        private void ResetClearButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var payloadPath = _lastPlayable?.PayloadPath;
                var kind = _lastPlayable?.Kind;
                var token = _lastPlayable?.PayloadToken;

                if (string.IsNullOrWhiteSpace(payloadPath))
                    payloadPath = Settings.Values[LS_LastPath] as string;

                if (string.IsNullOrWhiteSpace(kind))
                    kind = Settings.Values[LS_LastKind] as string;

                if (string.IsNullOrWhiteSpace(token))
                    token = Settings.Values[LS_LastToken] as string;

                if (!string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        var futureAccessList = WPerms.StorageApplicationPermissions.FutureAccessList;
                        if (futureAccessList.ContainsItem(token))
                            futureAccessList.Remove(token);
                    }
                    catch { }
                }

                ActivityHub.RemovePayload(payloadPath, ToActivityHubType(kind));
                ClearHeroFromSettings();
                SetHeroEmptyState();
            }
            catch { }
        }

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new WPickers.FileOpenPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeFilter.Add("*");

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                    return;

                var kind = DetectKindFromPath(file.Path);

                string token = "";
                try { token = WPerms.StorageApplicationPermissions.FutureAccessList.Add(file); } catch { }

                var item = new RecentItem
                {
                    Title = file.DisplayName,
                    Subtitle = kind == "music" ? "Opened music file" :
                               kind == "video" ? "Opened video file" :
                               "Opened file",
                    RightText = "Now",
                    PayloadPath = file.Path,
                    PayloadToken = token,
                    Kind = kind
                };

                try
                {
                    ActivityHub.Record(
                        kind == "music" ? ActivityHub.ActivityKind.Music :
                        kind == "video" ? ActivityHub.ActivityKind.Video :
                        ActivityHub.ActivityKind.PageVisit,
                        title: item.Title,
                        subtitle: item.Subtitle,
                        payload: item.PayloadPath ?? "",
                        listenedSeconds: 0,
                        watchedSeconds: 0,
                        imageUri: ""
                    );
                }
                catch { }

                SetHeroPlayableState(item, persist: true);
                NavigateFromRecent(item);
            }
            catch { }
        }

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new WPickers.FolderPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeFilter.Add("*");

                var folder = await picker.PickSingleFolderAsync();
                if (folder == null)
                    return;

                // Original behavior unchanged
            }
            catch { }
        }

        private void ViewLibrary_Click(object sender, RoutedEventArgs e)
        {
            try { App.MainWindow.MainFrame.Navigate(typeof(MusicLibraryPage)); } catch { }
        }

        private async Task ShowQuickStartDialogIfNeededAsync()
        {
            try
            {
                if (ReadBool(K_QuickStartSeen, false))
                    return;

                WriteBool(K_QuickStartSeen, true);
                await ShowQuickStartModeDialogAsync();
            }
            catch { }
        }

        private async void ChangeQuickStartMode_Click(object sender, RoutedEventArgs e)
        {
            await ShowQuickStartModeDialogAsync();
        }

        private async Task ShowQuickStartModeDialogAsync()
        {
            try
            {
                var current = ReadString(K_QuickStartMode, "creator");

                var group = new StackPanel { Spacing = 10 };
                group.Children.Add(new TextBlock
                {
                    Text = "Choose the starting path Zink should suggest. Every feature stays available in the sidebar and dashboard.",
                    TextWrapping = TextWrapping.WrapWholeWords
                });

                var modes = new[]
                {
                    ("creator", "Creator", "Recording, clips, streaming, and sharing."),
                    ("media", "Media", "Music, video, radio, Spotify, and libraries."),
                    ("gaming", "Gaming", "FPS tools, Xbox, cloud gaming, and capture."),
                    ("social", "Social", "Zink Connect, calls, messages, and web spaces.")
                };

                foreach (var mode in modes)
                {
                    group.Children.Add(new RadioButton
                    {
                        Content = $"{mode.Item2} - {mode.Item3}",
                        Tag = mode.Item1,
                        GroupName = "QuickStartMode",
                        IsChecked = string.Equals(current, mode.Item1, StringComparison.OrdinalIgnoreCase)
                    });
                }

                var dialog = new ContentDialog
                {
                    Title = "Set up Zink",
                    Content = group,
                    PrimaryButtonText = "Apply",
                    CloseButtonText = "Keep default",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    RefreshQuickStartMode();
                    return;
                }

                foreach (var child in group.Children.OfType<RadioButton>())
                {
                    if (child.IsChecked == true && child.Tag is string selected)
                    {
                        WriteString(K_QuickStartMode, selected);
                        break;
                    }
                }

                RefreshQuickStartMode();
            }
            catch { }
        }

        private void RefreshQuickStartMode()
        {
            try
            {
                var mode = ReadString(K_QuickStartMode, "creator").ToLowerInvariant();

                switch (mode)
                {
                    case "media":
                        SetQuickStartText(
                            "Media hub setup",
                            "Start with radio, libraries, and Spotify so Zink feels useful in the first minute.",
                            "Listen live", "Open radio", "\uE7F6", "#FFD56A",
                            "Build library", "Import media", "\uE8F1", "#5AE8E0",
                            "Spotify", "Now playing", "\uE768", "#7BF5B5");
                        break;

                    case "gaming":
                        SetQuickStartText(
                            "Gaming setup",
                            "Jump into cloud gaming, FPS tools, and capture without hiding the rest of Zink.",
                            "FPS tools", "Overlay and capture", "\uE7FC", "#7BF5B5",
                            "Recordings", "Saved clips", "\uE8B7", "#5AA7FF",
                            "Xbox", "Gaming hub", "\uE7FC", "#5AE8E0");
                        break;

                    case "social":
                        SetQuickStartText(
                            "Social setup",
                            "Use Zink Connect for calls, messages, screen sharing, and browser spaces.",
                            "Connect", "Call or share", "\uE774", "#5AE8E0",
                            "Messages", "Open chats", "\uE8BD", "#5AA7FF",
                            "Discord", "Presence", "\uE13D", "#FFD56A");
                        break;

                    default:
                        SetQuickStartText(
                            "Creator setup",
                            "Start with capture, streaming, and Zink Connect for the clearest install value.",
                            "Record", "Capture a clip", "\uE722", "#5AA7FF",
                            "Library", "Saved clips", "\uE8B7", "#5AE8E0",
                            "Connect", "Call or share", "\uE774", "#7BF5B5");
                        break;
                }
            }
            catch { }
        }

        private void SetQuickStartText(
            string title,
            string subtitle,
            string primaryTitle,
            string primarySubtitle,
            string primaryIcon,
            string primaryColor,
            string secondaryTitle,
            string secondarySubtitle,
            string secondaryIcon,
            string secondaryColor,
            string thirdTitle,
            string thirdSubtitle,
            string thirdIcon,
            string thirdColor)
        {
            QuickStartTitle.Text = title;
            QuickStartSubtitle.Text = subtitle;

            QuickStartPrimaryTitle.Text = primaryTitle;
            QuickStartPrimarySubtitle.Text = primarySubtitle;
            QuickStartPrimaryIcon.Glyph = primaryIcon;
            QuickStartPrimaryIcon.Foreground = ColorBrushFromHex(primaryColor);

            QuickStartSecondaryTitle.Text = secondaryTitle;
            QuickStartSecondarySubtitle.Text = secondarySubtitle;
            QuickStartSecondaryIcon.Glyph = secondaryIcon;
            QuickStartSecondaryIcon.Foreground = ColorBrushFromHex(secondaryColor);

            QuickStartThirdTitle.Text = thirdTitle;
            QuickStartThirdSubtitle.Text = thirdSubtitle;
            QuickStartThirdIcon.Glyph = thirdIcon;
            QuickStartThirdIcon.Foreground = ColorBrushFromHex(thirdColor);
        }

        private void QuickStartPrimary_Click(object sender, RoutedEventArgs e)
        {
            switch (ReadString(K_QuickStartMode, "creator").ToLowerInvariant())
            {
                case "media":
                    App.MainWindow.MainFrame.Navigate(typeof(RadioPage));
                    break;
                case "gaming":
                    App.MainWindow.MainFrame.Navigate(typeof(FpsRecorderPage));
                    break;
                case "social":
                    App.MainWindow.MainFrame.Navigate(typeof(ZinkConnectPage));
                    break;
                default:
                    App.MainWindow.MainFrame.Navigate(typeof(RecorderPage));
                    break;
            }
        }

        private void QuickStartSecondary_Click(object sender, RoutedEventArgs e)
        {
            switch (ReadString(K_QuickStartMode, "creator").ToLowerInvariant())
            {
                case "media":
                    App.MainWindow.MainFrame.Navigate(typeof(MusicLibraryPage));
                    break;
                case "gaming":
                    App.MainWindow.MainFrame.Navigate(typeof(RecordingsLibraryPage));
                    break;
                case "social":
                    App.MainWindow.MainFrame.Navigate(typeof(Social.MessagesPage));
                    break;
                default:
                    App.MainWindow.MainFrame.Navigate(typeof(RecordingsLibraryPage));
                    break;
            }
        }

        private void QuickStartThird_Click(object sender, RoutedEventArgs e)
        {
            switch (ReadString(K_QuickStartMode, "creator").ToLowerInvariant())
            {
                case "media":
                    Spotify_Click(sender, e);
                    break;
                case "gaming":
                    App.MainWindow.MainFrame.Navigate(typeof(XboxPage));
                    break;
                case "social":
                    Discord_Click(sender, e);
                    break;
                default:
                    App.MainWindow.MainFrame.Navigate(typeof(ZinkConnectPage));
                    break;
            }
        }

        private void ClearRecent_Click(object sender, RoutedEventArgs e)
        {
            try { ActivityHub.Clear(); } catch { }

            ClearHeroFromSettings();
            SetHeroEmptyState();
            RefreshFromActivityHub();
        }

        private void RecentList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is RecentItem item)
            {
                if (string.IsNullOrWhiteSpace(item.Kind) && !string.IsNullOrWhiteSpace(item.PayloadPath))
                    item.Kind = DetectKindFromPath(item.PayloadPath);

                if (item.Kind == "music" || item.Kind == "video" || item.Kind == "radio" || item.Kind == "spotify")
                    SetHeroPlayableState(item, persist: true);
                else
                    SetHeroPlayableState(item, persist: false);

                NavigateFromRecent(item);
            }
        }

        private void MusicPlayer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(MusicPlayerPage));
        private void VideoPlayer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VideoPlayerPage));
        private void Radio_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RadioPage));
        private void VideoLibrary_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VideoLibraryPage));
        private void MusicLibrary_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(MusicLibraryPage));
        private void Equalizer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(EqualizerPage));
        private void Visualizer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VisualizerPage));
        private void Settings_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(SettingsPage));
        private void VersionHistory_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VersionHistoryPage));
        private void ZinkConnect_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(ZinkConnectPage));
        private void Streaming_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(StreamingPage));
        private void LeaveReview_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(ReviewPage));
        private void PulseCalls_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(Social.CallPage));
        private void PulseStream_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(Social.CallPage));
        private void PulseFps_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(FpsRecorderPage));
        private void PulseFriends_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(Social.FriendsPage));
        private void PulseMessages_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(Social.MessagesPage));
        private void PulseProfile_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(Social.ProfilePage));
        private void PulseLogs_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(SettingsPage));
        private void PulseSpotifyBeta_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(SpotifyBetaPage));

        private void Spotify_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidates = new[]
                {
                    "Zink.Pages.SpotifyPage",
                    "Zink.Pages.SpotifyLoginPage",
                    "Zink.Pages.SpotifyHomePage",
                    "Zink.Pages.SpotifyBrowserPage"
                };

                foreach (var name in candidates)
                {
                    var t = Type.GetType(name);
                    if (t != null)
                    {
                        App.MainWindow.MainFrame.Navigate(t);
                        return;
                    }
                }
            }
            catch { }
        }

        private async void Xbox_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.XboxPage",
                "Zink.Pages.XboxHomePage"
            );

            if (!ok)
                await ShowMissingPageDialog("Xbox", "Zink.Pages.XboxPage");
        }

        private async void Discord_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.DiscordPage",
                "Zink.Pages.DiscordHomePage"
            );

            if (!ok)
                await ShowMissingPageDialog("Discord", "Zink.Pages.DiscordPage");
        }

        private async void YouTube_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.YouTubePage",
                "Zink.Pages.YouTubeHomePage",
                "Zink.Pages.YouTubeBrowserPage"
            );

            if (!ok)
                await ShowMissingPageDialog("YouTube", "Zink.Pages.YouTubePage");
        }

        private async void Customise_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow.MainFrame.Navigate(typeof(AppCustomizationPage));
            await Task.CompletedTask;
        }

        private void HomeSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            try
            {
                var q = (args?.QueryText ?? "").Trim();
                if (string.IsNullOrWhiteSpace(q))
                    return;

                App.MainWindow.MainFrame.Navigate(typeof(SearchResultsPage), q);
            }
            catch { }
        }

        private static string FormatWhen(DateTimeOffset when)
        {
            try
            {
                var local = when.ToLocalTime();
                var age = DateTimeOffset.Now - local;

                if (age.TotalMinutes < 1) return "Now";
                if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
                if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
                return local.ToString("dd MMM");
            }
            catch
            {
                return "";
            }
        }

        private static string FormatShortTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}h {t.Minutes}m";
            return $"{Math.Max(0, t.Minutes)}m";
        }

        private static string ToDashKind(string? activityType, string? payload)
        {
            if (string.Equals(activityType, "Music", StringComparison.OrdinalIgnoreCase)) return "music";
            if (string.Equals(activityType, "Video", StringComparison.OrdinalIgnoreCase)) return "video";
            if (string.Equals(activityType, "Radio", StringComparison.OrdinalIgnoreCase)) return "radio";

            if (!string.IsNullOrWhiteSpace(payload))
                return DetectKindFromPath(payload);

            return "page";
        }

        private static string? ToActivityHubType(string? kind)
        {
            return kind?.ToLowerInvariant() switch
            {
                "music" => ActivityHub.ActivityKind.Music.ToString(),
                "video" => ActivityHub.ActivityKind.Video.ToString(),
                "radio" => ActivityHub.ActivityKind.Radio.ToString(),
                _ => null
            };
        }

        private static string DetectKindFromPath(string path)
        {
            try
            {
                var ext = Path.GetExtension(path);
                if (MusicExt.Contains(ext)) return "music";
                if (VideoExt.Contains(ext)) return "video";
            }
            catch { }
            return "page";
        }

        private bool TryNavigateToAny(params string[] typeNames)
        {
            foreach (var name in typeNames)
            {
                try
                {
                    var t = Type.GetType(name);
                    if (t != null)
                    {
                        App.MainWindow.MainFrame.Navigate(t);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private async Task ShowMissingPageDialog(string featureName, string expectedType)
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = $"{featureName} page missing",
                    Content = $"Could not find the page type:\n{expectedType}\n\nMake sure the page exists in your project.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };

                await dlg.ShowAsync();
            }
            catch { }
        }

        private async void Twitch_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.TwitchPage"
            );

            if (!ok)
                await ShowMissingPageDialog("Twitch", "Zink.Pages.TwitchPage");
        }

        private static bool IsPlayableForHero(RecentItem? item)
        {
            if (item == null)
                return false;

            if (string.Equals(item.Kind, "radio", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Kind, "spotify", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(item.Kind, "music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(item.PayloadToken) ||
                       !string.IsNullOrWhiteSpace(item.PayloadPath);
            }

            return false;
        }

        private static string GetKindDisplay(string? kind)
        {
            return kind?.ToLowerInvariant() switch
            {
                "music" => "Music",
                "video" => "Film / Video",
                "radio" => "Radio",
                "spotify" => "Spotify",
                _ => "Idle"
            };
        }

        private static string GetHeroMetaText(string? kind)
        {
            return kind?.ToLowerInvariant() switch
            {
                "music" => "Music playback",
                "video" => "Video playback",
                "radio" => "Live stream",
                "spotify" => "Spotify playback",
                _ => "Nothing active"
            };
        }

        private static string GetDefaultSubtitleForKind(string? kind)
        {
            return kind?.ToLowerInvariant() switch
            {
                "music" => "Music is active in Zink",
                "video" => "Video is active in Zink",
                "radio" => "Radio is active in Zink",
                "spotify" => "Spotify is active in Zink",
                _ => "Play music, radio or a film and it will appear here"
            };
        }

        private bool HasLiveNowPlaying()
        {
            try
            {
                var spotify = SpotifyControllerService.Instance;
                if (spotify != null &&
                    spotify.IsAttached &&
                    (!string.IsNullOrWhiteSpace(spotify.Current?.Title) ||
                     !string.IsNullOrWhiteSpace(spotify.Current?.Artist) ||
                     !string.IsNullOrWhiteSpace(spotify.Current?.Album)))
                {
                    return true;
                }

                var appPlayback = AppPlaybackService.Instance;
                if (appPlayback == null)
                    return false;

                if (appPlayback.CurrentKind == AppPlaybackService.MediaKind.None)
                    return false;

                if (!string.IsNullOrWhiteSpace(appPlayback.PrimaryText))
                    return true;

                if (appPlayback.CurrentKind == AppPlaybackService.MediaKind.Radio &&
                    !string.IsNullOrWhiteSpace(appPlayback.CurrentStationTitle))
                    return true;
            }
            catch { }

            return false;
        }

        private async void RefreshNotifications()
        {
            try
            {
                var items = new List<(string Title, string Detail, string Accent, string Icon)>();

                await AddFriendRequestNotificationsAsync(items);
                await AddMessageNotificationsAsync(items);
                AddMissedCallNotifications(items);

                items.Add((
                    DiagnosticLogService.IsEnabled ? "Diagnostics logging is on" : "Diagnostics logging is off",
                    DiagnosticLogService.IsEnabled
                        ? "Writing this device to " + DiagnosticLogService.CurrentLogPath
                        : "Turn logging on in Settings before testing calls.",
                    DiagnosticLogService.IsEnabled ? "#65D887" : "#FFB84D",
                    "\uE946"
                ));

                items.Add((
                    "GPU screen share logging",
                    "Receiver now logs H.264 input format, decoder output, RTP frames and preview fallback frames.",
                    "#5AB4FF",
                    "\uE7F4"
                ));

                if (!HasLiveNowPlaying())
                {
                    items.Add((
                        "No live playback detected",
                        "Start Spotify, music, radio or video playback and the dashboard will update automatically.",
                        "#B9C9D6",
                        "\uE768"
                    ));
                }

                NotificationsCountText.Text = items.Count.ToString();
                NotificationsBadge.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                NotificationsList.Items.Clear();

                foreach (var item in items)
                {
                    var row = new Border
                    {
                        CornerRadius = new CornerRadius(18),
                        Padding = new Thickness(12),
                        Margin = new Thickness(0, 0, 0, 8),
                        BorderThickness = new Thickness(1),
                        BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
                    };

                    var grid = new Grid
                    {
                        ColumnSpacing = 10
                    };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var iconHost = new Border
                    {
                        Width = 32,
                        Height = 32,
                        CornerRadius = new CornerRadius(16),
                        Background = ColorBrushFromHex(item.Accent),
                        VerticalAlignment = VerticalAlignment.Top,
                        Child = new FontIcon
                        {
                            Glyph = item.Icon,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                            FontSize = 14,
                            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x08, 0x0D, 0x12)),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };

                    var text = new StackPanel { Spacing = 3 };
                    text.Children.Add(new TextBlock
                    {
                        Text = item.Title,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                    text.Children.Add(new TextBlock
                    {
                        Text = item.Detail,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xB7, 0xC8, 0xD2, 0xDC)),
                        FontSize = 12,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        MaxLines = 3
                    });

                    Grid.SetColumn(text, 1);
                    grid.Children.Add(iconHost);
                    grid.Children.Add(text);
                    row.Child = grid;
                    NotificationsList.Items.Add(row);
                }
            }
            catch
            {
            }
        }

        private async Task AddFriendRequestNotificationsAsync(List<(string Title, string Detail, string Accent, string Icon)> items)
        {
            try
            {
                var requests = await SocialManager.Instance.Api.GetRequestsAsync();
                foreach (var request in requests
                    .Where(r => string.IsNullOrWhiteSpace(r.Status) || r.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.CreatedUtc)
                    .Take(4))
                {
                    var name = GetCallerDisplayName(request.FromDisplayName, request.FromUsername, request.FromUserId);
                    items.Add((
                        "New friend request",
                        $"{name} wants to connect with you on Zink Social.",
                        "#7BF5B5",
                        "\uE715"
                    ));
                }
            }
            catch
            {
            }
        }

        private async Task AddMessageNotificationsAsync(List<(string Title, string Detail, string Accent, string Icon)> items)
        {
            try
            {
                var conversations = await LocalMessageStore.Instance.LoadAsync();
                foreach (var conversation in conversations
                    .Select(c => new
                    {
                        Conversation = c,
                        Message = c.Messages.Where(m => !m.IsFromMe).OrderByDescending(m => m.SentUtc).FirstOrDefault()
                    })
                    .Where(x => x.Message != null)
                    .OrderByDescending(x => x.Message!.SentUtc)
                    .Take(4))
                {
                    var sender = GetConversationTitle(conversation.Conversation);
                    items.Add((
                        "New message",
                        $"{sender}: {conversation.Message!.Text}",
                        "#5AB4FF",
                        "\uE8BD"
                    ));
                }
            }
            catch
            {
            }
        }

        private void AddMissedCallNotifications(List<(string Title, string Detail, string Accent, string Icon)> items)
        {
            foreach (var call in _missedCalls.Take(4))
            {
                items.Add((
                    "Missed call",
                    $"You missed a call from {call.CallerName} {FormatRelativeTime(call.WhenUtc)}.",
                    "#FF6F86",
                    "\uE717"
                ));
            }
        }

        private static string GetCallerDisplayName(string displayName, string username, long userId)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName;

            if (!string.IsNullOrWhiteSpace(username))
                return "@" + username;

            return userId > 0 ? $"User {userId}" : "Someone";
        }

        private static string GetConversationTitle(SavedConversation conversation)
        {
            if (!string.IsNullOrWhiteSpace(conversation.DisplayName))
                return conversation.DisplayName;

            if (!string.IsNullOrWhiteSpace(conversation.Username))
                return conversation.Username;

            return conversation.TargetUserId > 0 ? $"User {conversation.TargetUserId}" : "Someone";
        }

        private static string FormatRelativeTime(DateTimeOffset whenUtc)
        {
            var elapsed = DateTimeOffset.UtcNow - whenUtc;
            if (elapsed.TotalSeconds < 60)
                return "just now";
            if (elapsed.TotalMinutes < 60)
                return $"{Math.Max(1, (int)elapsed.TotalMinutes)} min ago";
            if (elapsed.TotalHours < 24)
                return $"{Math.Max(1, (int)elapsed.TotalHours)} hr ago";

            return whenUtc.ToLocalTime().ToString("MMM d, HH:mm");
        }

        private static Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrushFromHex(string hex)
        {
            try
            {
                var value = hex.TrimStart('#');
                var r = Convert.ToByte(value.Substring(0, 2), 16);
                var g = Convert.ToByte(value.Substring(2, 2), 16);
                var b = Convert.ToByte(value.Substring(4, 2), 16);
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, r, g, b));
            }
            catch
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0xB9, 0xC9, 0xD6));
            }
        }

        private static string FormatClock(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return t.ToString(@"hh\:mm\:ss");

            return t.ToString(@"mm\:ss");
        }
    }
}

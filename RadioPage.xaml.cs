using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using Windows.Media.Core;
using Windows.Media.Playback;
using Zink.Services;
using Microsoft.UI.Xaml.Navigation;

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Web;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;

using Microsoft.UI.Windowing;
using WinRT.Interop;
using Windows.Storage;

// alias Windows.Graphics to avoid Zink.Windows collisions
using WinGraphics = global::Windows.Graphics;

namespace Zink
{
    public sealed partial class RadioPage : Page
    {
        private WebViewWindow webViewWindow;

        public ObservableCollection<RadioStation> RadioStations { get; set; }
        public ObservableCollection<RadioStation> AllStationsOrdered { get; } = new();

        private IcyNowPlayingWatcher _nowPlayingWatcher;
        private CancellationTokenSource _artCts;

        private string _lastArtist = "";
        private string _lastTitle = "";
        private Uri? _lastArtworkUri = null;
        private DateTime _lastMetaPushUtc = DateTime.MinValue;

        private string _currentStationTitle = "—";
        private string _currentStreamQuality = "Unknown";
        private static MediaPlayer _mp;
        private static WeakReference<RadioPage>? _lastInstance;

        private readonly SemaphoreSlim _stationSwitchLock = new(1, 1);
        private int _switchVersion = 0;
        private CancellationTokenSource _directReconnectCts;
        private int _directReconnectAttempts;
        private bool _directReconnectPending;
        private global::Windows.Foundation.TypedEventHandler<MediaPlayer, object> _directMediaOpenedHandler;
        private global::Windows.Foundation.TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> _directMediaFailedHandler;
        private global::Windows.Foundation.TypedEventHandler<MediaPlayer, object> _directMediaEndedHandler;
        private global::Windows.Foundation.TypedEventHandler<MediaPlaybackSession, object> _directPlaybackStateChangedHandler;
        private DispatcherTimer _radioDiagnosticTimer;
        private string _diagnosticStationTitle = "";
        private string _diagnosticStreamUrl = "";

        private const double META_MIN_INTERVAL_SECONDS = 1.5;
        private static readonly Encoding IcyEncoding = Encoding.GetEncoding("ISO-8859-1");

        private DispatcherTimer _elapsedTimer;
        private DispatcherTimer _servicePollTimer;

        private bool _everHadMeta = false;
        private Uri? _lastImageShownUri = null;

        private const string RadioVolumeSettingKey = "RadioPageVolume";
        private static double? _sharedRadioVolume = null;

        private double _currentVolume = 1.0;
        private bool _isApplyingVolumeFromCode = false;
        private bool _volumeReady = false;

        private static readonly HashSet<string> WebViewStations = new(StringComparer.OrdinalIgnoreCase)
        {
            "Absolute Radio","Gem 106","Premier Christian Radio","Jazz FM","MKFM",
            "Capital Xtra","Capital Extra","Radio Essex"
        };

        private static readonly HashSet<string> _leftColumnAnchors = new(StringComparer.OrdinalIgnoreCase)
        {
            "Capital FM","Heart","Heart Milton Keynes","Kiss FM","Smooth Radio","Magic Radio",
            "BBC Radio 1","BBC Radio 2","BBC Radio 3","BBC Radio 5 Live","BBC Radio 1Xtra"
        };

        private static readonly HttpClient _safeHttp = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });

        static RadioPage()
        {
            try
            {
                _safeHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Zink/1.0 (+https://zink.app)");
                _safeHttp.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }
            catch { }
        }

        public RadioPage()
        {
            this.InitializeComponent();
            _lastInstance = new WeakReference<RadioPage>(this);

            _currentVolume = LoadSavedVolume();

            if (_mp == null)
            {
                _mp = new MediaPlayer
                {
                    AutoPlay = false,
                    AudioCategory = MediaPlayerAudioCategory.Media
                };
            }

            _mp.Volume = _currentVolume;

            try
            {
                AppPlaybackService.Instance.RegisterRadioVolumeApplier(ApplySharedRadioVolumeFromService, pushCurrentValue: false);
            }
            catch { }

            Loaded += (_, __) =>
            {
                try
                {
                    if (Player != null && Player.MediaPlayer != _mp)
                        Player.SetMediaPlayer(_mp);
                }
                catch { }

                RestoreSavedVolumeState();

                TryWireServiceEvents();
                RefreshUiFromService(force: true);
                StartElapsedTimer();
                StartServicePollTimer();
                UpdateLikeButtonState();
                UpdateStationAndQualityText();
            };

            Unloaded += (_, __) =>
            {
                SaveCurrentVolume();
                TryUnwireServiceEvents();
                StopElapsedTimer();
                StopServicePollTimer();
            };

            RadioStations = new ObservableCollection<RadioStation>
            {
                new RadioStation { Title = "Capital FM", Image = "ms-appx:///Assets/Radio/capitalfm.png", StreamUrl = "https://media-ssl.musicradio.com/CapitalUK" },
                new RadioStation { Title = "Heart", Image = "ms-appx:///Assets/Radio/heartfm.png",   StreamUrl = "https://media-ssl.musicradio.com/HeartUK" },
                new RadioStation { Title = "Heart Milton Keynes", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/HeartMiltonKeynesMP3" },
                new RadioStation { Title = "Kiss FM", Image = "ms-appx:///Assets/Radio/kissfm.png",  StreamUrl = "https://media-ssl.musicradio.com/Kiss" },
                new RadioStation { Title = "Smooth Radio", Image = "ms-appx:///Assets/Radio/smoothradio.png", StreamUrl = "https://media-ssl.musicradio.com/SmoothUK" },

                new RadioStation { Title = "Magic Radio", Image = "ms-appx:///Assets/Radio/magicradio.png", StreamUrl = "https://planetradio.co.uk/magic/player/" },

                new RadioStation { Title = "BBC Radio 1", Image = "ms-appx:///Assets/Radio/bbcradio1.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_one" },
                new RadioStation { Title = "BBC Radio 4", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_fourfm" },
                new RadioStation { Title = "BBC Radio 4 Extra", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_four_extra" },
                new RadioStation { Title = "BBC Radio 2", Image = "ms-appx:///Assets/Radio/bbcradio2.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_two" },
                new RadioStation { Title = "BBC Radio 3", Image = "ms-appx:///Assets/Radio/bbcradio3.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_three" },
                new RadioStation { Title = "BBC Radio 5 Live", Image = "ms-appx:///Assets/Radio/bbcradio5live.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_five_live" },
                new RadioStation { Title = "BBC Radio 5 Sports Extra", Image = "ms-appx:///Assets/Radio/bbcradio5live.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_five_live_sports_extra" },
                new RadioStation { Title = "BBC Radio 6 Music", Image = "ms-appx:///Assets/Radio/bbcradio1.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_6music" },
                new RadioStation { Title = "BBC Radio 1Xtra", Image = "ms-appx:///Assets/Radio/bbc1xtra.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_1xtra" },
                new RadioStation { Title = "BBC Asian Network", Image = "ms-appx:///Assets/Radio/bbc1xtra.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_asian_network" },

                new RadioStation { Title = "Hits Radio", Image = "ms-appx:///Assets/Radio/hitsradio.png", StreamUrl = "https://hellorayo.co.uk/hits-radio/play?stationId=352" },
                new RadioStation { Title = "Greatest Hits Radio", Image = "ms-appx:///Assets/Radio/greatesthitsradio.png", StreamUrl = "https://planetradio.co.uk/greatest-hits/player/" },

                new RadioStation { Title = "talkSPORT", Image = "ms-appx:///Assets/Radio/talksport.png", StreamUrl = "https://radio.talksport.com/stream" },

                new RadioStation { Title = "Absolute Radio", Image = "ms-appx:///Assets/Radio/absolute.png", StreamUrl = "https://planetradio.co.uk/absolute/player/" },
                new RadioStation { Title = "Classic FM", Image = "ms-appx:///Assets/Radio/classicfm.png", StreamUrl = "https://media-ssl.musicradio.com/ClassicFM" },
                new RadioStation { Title = "Radio X", Image = "ms-appx:///Assets/Radio/radiox.png", StreamUrl = "https://media-ssl.musicradio.com/RadioXUK" },
                new RadioStation { Title = "Radio X Classic Rock", Image = "ms-appx:///Assets/Radio/radiox.png", StreamUrl = "https://media-ssl.musicradio.com/RadioXClassicRock" },
                new RadioStation { Title = "Radio X Chilled", Image = "ms-appx:///Assets/Radio/radiox.png", StreamUrl = "https://media-ssl.musicradio.com/RadioXChilled" },

                new RadioStation { Title = "Gem 106", Image = "ms-appx:///Assets/Radio/gem106.png", StreamUrl = "https://planetradio.co.uk/gem/player/" },
                new RadioStation { Title = "Premier Christian Radio", Image = "ms-appx:///Assets/Radio/premier.png", StreamUrl = "https://www.premier.plus/" },
                new RadioStation { Title = "BBC Radio Derby", Image = "ms-appx:///Assets/Radio/radioderby.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_derby" },
                new RadioStation { Title = "BBC Radio London", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_london" },
                new RadioStation { Title = "BBC Radio Manchester", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_manchester" },
                new RadioStation { Title = "BBC Radio Scotland", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_scotland_fm" },
                new RadioStation { Title = "BBC Radio Wales", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_wales_fm" },
                new RadioStation { Title = "BBC Radio Cymru", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_cymru" },
                new RadioStation { Title = "BBC Radio Ulster", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_ulster" },

                new RadioStation { Title = "Jazz FM", Image = "ms-appx:///Assets/Radio/jazzfm.png", StreamUrl = "https://planetradio.co.uk/jazz-fm/player/" },
                new RadioStation { Title = "MKFM", Image = "ms-appx:///Assets/Radio/mkfm.png", StreamUrl = "https://www.mkfm.com/on-air/radioplayer/" },

                new RadioStation { Title = "BBC World Service", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_world_service" },
                new RadioStation { Title = "LBC", Image = "ms-appx:///Assets/Radio/lbc.png", StreamUrl = "https://media-ssl.musicradio.com/LBCUK" },
                new RadioStation { Title = "Times Radio", Image = "ms-appx:///Assets/Radio/timesradio.png", StreamUrl = "https://timesradio.wireless.radio/stream" },
                new RadioStation { Title = "Capital Dance", Image = "ms-appx:///Assets/Radio/capitaldance.png", StreamUrl = "https://media-ssl.musicradio.com/CapitalDance" },
                new RadioStation { Title = "Capital Anthems", Image = "ms-appx:///Assets/Radio/capitalfm.png", StreamUrl = "https://media-ssl.musicradio.com/CapitalAnthems" },
                new RadioStation { Title = "Capital Chill", Image = "ms-appx:///Assets/Radio/capitalfm.png", StreamUrl = "https://media-ssl.musicradio.com/CapitalChill" },
                new RadioStation { Title = "Capital Scotland", Image = "ms-appx:///Assets/Radio/capitalfm.png", StreamUrl = "https://media-ssl.musicradio.com/CapitalScotland" },
                new RadioStation { Title = "Heart 70s", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/Heart70s" },
                new RadioStation { Title = "Heart 80s", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/Heart80s" },
                new RadioStation { Title = "Heart 90s", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/Heart90s" },
                new RadioStation { Title = "Heart Dance", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/HeartDance" },
                new RadioStation { Title = "Heart Love", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/HeartLove" },
                new RadioStation { Title = "Smooth Chill", Image = "ms-appx:///Assets/Radio/smoothfm.png", StreamUrl = "https://media-ssl.musicradio.com/SmoothChill" },
                new RadioStation { Title = "Smooth Country", Image = "ms-appx:///Assets/Radio/smoothfm.png", StreamUrl = "https://media-ssl.musicradio.com/SmoothCountry" },
                new RadioStation { Title = "Smooth 70s", Image = "ms-appx:///Assets/Radio/smoothfm.png", StreamUrl = "https://media-ssl.musicradio.com/Smooth70s" },
                new RadioStation { Title = "Smooth 80s", Image = "ms-appx:///Assets/Radio/smoothfm.png", StreamUrl = "https://media-ssl.musicradio.com/Smooth80s" },
                new RadioStation { Title = "Smooth Relax", Image = "ms-appx:///Assets/Radio/smoothfm.png", StreamUrl = "https://media-ssl.musicradio.com/SmoothRelax" },
                new RadioStation { Title = "Gold Radio", Image = "ms-appx:///Assets/Radio/classicfm.png", StreamUrl = "https://media-ssl.musicradio.com/Gold" },
                new RadioStation { Title = "LBC News", Image = "ms-appx:///Assets/Radio/lbc.png", StreamUrl = "https://media-ssl.musicradio.com/LBCNewsUK" },

                new RadioStation { Title = "Capital Xtra", Image = "ms-appx:///Assets/Radio/capitalxtra.png", StreamUrl = "https://www.globalplayer.com/live/capitalxtra/uk/" },
                new RadioStation { Title = "Radio Essex", Image = "ms-appx:///Assets/Radio/radioessex.png", StreamUrl = "https://www.radioessex.com/player/" },
                new RadioStation { Title = "Kisstory", Image = "ms-appx:///Assets/Radio/kissfm.png", StreamUrl = "https://listen-kisstory.sharp-stream.com/kisstory.mp3" },
                new RadioStation { Title = "Kiss Fresh", Image = "ms-appx:///Assets/Radio/kissfm.png", StreamUrl = "https://listen-kissfresh.sharp-stream.com/kissfresh.mp3" },
                new RadioStation { Title = "Kerrang! Radio", Image = "ms-appx:///Assets/Radio/absolute.png", StreamUrl = "https://listen-kerrang.sharp-stream.com/kerrang.mp3" },
                new RadioStation { Title = "Planet Rock", Image = "ms-appx:///Assets/Radio/absolute.png", StreamUrl = "https://listen-planetrock.sharp-stream.com/planetrock.mp3" },
                new RadioStation { Title = "Scala Radio", Image = "ms-appx:///Assets/Radio/classicfm.png", StreamUrl = "https://listen-scalafm.sharp-stream.com/scalaradio.mp3" },
                new RadioStation { Title = "Union JACK Radio", Image = "ms-appx:///Assets/Radio/radiox.png", StreamUrl = "https://streaming.radio.co/sd8ab6ff39/listen" },
                new RadioStation { Title = "Virgin Radio UK", Image = "ms-appx:///Assets/Radio/timesradio.png", StreamUrl = "https://radio.virginradio.co.uk/stream" },
                new RadioStation { Title = "Virgin Radio Anthems", Image = "ms-appx:///Assets/Radio/timesradio.png", StreamUrl = "https://radio.virginradio.co.uk/anthems" },
                new RadioStation { Title = "Virgin Radio Chilled", Image = "ms-appx:///Assets/Radio/timesradio.png", StreamUrl = "https://radio.virginradio.co.uk/chilled" },
                new RadioStation { Title = "talkRADIO", Image = "ms-appx:///Assets/Radio/talksport.png", StreamUrl = "https://radio.talkradio.co.uk/stream" }
            };

            BuildAllStationsOrdered();

            try
            {
                var s = AppPlaybackService.Instance;
                s.PlayRequested += () => TryEnqueueOnUi(() =>
                {
                    RadioLog("service-play-request", flush: true);
                    _mp?.Play();
                    ApplyCurrentVolumeToAllPlayback(updateSlider: false);
                    s.SetIsPlaying(true);
                });
                s.PauseRequested += () => TryEnqueueOnUi(() =>
                {
                    RadioLog("service-pause-request", flush: true);
                    _mp?.Pause();
                    s.SetIsPlaying(false);
                });
                s.StopRequested += () => TryEnqueueOnUi(() =>
                {
                    RadioLog("service-stop-request", flush: true);
                    StopDirectPlaybackRecovery();
                    if (_mp != null)
                    {
                        _mp.Pause();
                        _mp.Source = null;
                    }
                    s.SetIsPlaying(false);
                    ResetStreamQuality();
                });
            }
            catch { }
        }

        private static void ApplySharedRadioVolumeFromService(double volume)
        {
            try
            {
                volume = Math.Clamp(volume, 0.0, 1.0);
                _sharedRadioVolume = volume;

                try
                {
                    ApplicationData.Current.LocalSettings.Values[RadioVolumeSettingKey] = volume;
                }
                catch { }

                try
                {
                    if (_mp != null)
                        _mp.Volume = volume;
                }
                catch { }

                try
                {
                    if (_lastInstance != null && _lastInstance.TryGetTarget(out var page) && page != null)
                    {
                        page.TryEnqueueOnUi(() =>
                        {
                            page._currentVolume = volume;
                            page.ApplyCurrentVolumeToAllPlayback(updateSlider: true);
                        });
                    }
                }
                catch { }
            }
            catch { }
        }

        private void RestoreSavedVolumeState()
        {
            _volumeReady = false;
            _currentVolume = LoadSavedVolume();
            ApplyCurrentVolumeToAllPlayback(updateSlider: true);
            SaveCurrentVolume();
            _volumeReady = true;
        }

        private bool TryEnqueueOnUi(Action action)
        {
            try
            {
                var dispatcher = this.DispatcherQueue ?? App.MainWindow?.DispatcherQueue;
                if (dispatcher == null)
                    return false;

                return dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                    }
                    catch { }
                });
            }
            catch
            {
                return false;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RadioLog("page-navigated-to", $"parameter={e.Parameter}");

            try
            {
                if (Player != null && Player.MediaPlayer != _mp)
                    Player.SetMediaPlayer(_mp);
            }
            catch { }

            RestoreSavedVolumeState();
            RefreshUiFromService(force: true);
            StartElapsedTimer();
            StartServicePollTimer();
            TryWireServiceEvents();
            UpdateStationAndQualityText();

            if (e.Parameter is string stationTitle && !string.IsNullOrWhiteSpace(stationTitle))
            {
                var st = RadioStations?.FirstOrDefault(s =>
                    string.Equals(s.Title, stationTitle, StringComparison.OrdinalIgnoreCase));

                if (st != null)
                {
                    TryEnqueueOnUi(() =>
                    {
                        PlayStation_Click(new Button { DataContext = st }, new RoutedEventArgs());
                    });
                }
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            RadioLog("page-navigated-from", $"backgroundKeepAlive={RadioBackgroundKeepAlive.Enabled}; widgetKeepAlive={RadioWidgetKeepAlive.IsOpen}", flush: true);
            SaveCurrentVolume();
            StopElapsedTimer();
            StopServicePollTimer();
            TryUnwireServiceEvents();
        }

        private void BuildAllStationsOrdered()
        {
            AllStationsOrdered.Clear();

            foreach (var s in RadioStations)
                if (_leftColumnAnchors.Contains(s.Title))
                    AllStationsOrdered.Add(s);

            foreach (var s in RadioStations)
                if (!_leftColumnAnchors.Contains(s.Title))
                    AllStationsOrdered.Add(s);
        }

        private async void PlayStation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not RadioStation station)
                return;

            RadioLog("station-selected", $"title={station.Title}; mode={(WebViewStations.Contains(station.Title) || station.Title.StartsWith("BBC", StringComparison.OrdinalIgnoreCase) ? "webview" : "native")}; url={station.StreamUrl}", flush: true);

            int myVersion;

            await _stationSwitchLock.WaitAsync();
            try
            {
                myVersion = ++_switchVersion;

                ForceStopAllPlayback();
                await Task.Delay(120);

                NowPlayingText.Text = "Loading...";
                TrySetImage(new Uri(station.Image));
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                _lastArtist = "";
                _lastTitle = "";
                _lastArtworkUri = null;
                _lastMetaPushUtc = DateTime.MinValue;
                _everHadMeta = false;
                _lastImageShownUri = null;

                SetCurrentStationLabel(station.Title ?? "—");
                SetStreamQuality("Detecting...");
                PushStationToService(station, isPlaying: false);
                UpdateLikeButtonState();
            }
            finally
            {
                _stationSwitchLock.Release();
            }

            if (station.Title.StartsWith("BBC", StringComparison.OrdinalIgnoreCase))
            {
                var result = await new ContentDialog
                {
                    Title = "For the BBC Radio stations, Sign In is Required",
                    Content = "To play the BBC radio stations, you must be signed in to your BBC account.",
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();

                if (myVersion != _switchVersion) return;

                if (result == ContentDialogResult.Primary)
                {
                    webViewWindow = new WebViewWindow(station.Title, station.StreamUrl);
                    ApplyCurrentVolumeToAllPlayback(updateSlider: false);

                    try
                    {
                        ActivityHub.Record(
                            ActivityHub.ActivityKind.Radio,
                            title: station.Title ?? "",
                            subtitle: "Web player",
                            payload: station.Title ?? "",
                            listenedSeconds: 0,
                            imageUri: station.Image ?? ""
                        );
                    }
                    catch { }

                    NotificationService.Instance.Show("Radio Started", $"Now playing: {station.Title}");
                    NowPlayingText.Text = station.Title;
                    TryPushNowPlaying(station, "", station.Title);
                    SetCurrentStationLabel(station.Title);
                    SetStreamQuality("Web player - broadcaster controlled");
                    SetDiscordStationPresence(station);
                }
                else
                {
                    NowPlayingText.Text = "BBC station cancelled.";
                    TrySetPlaying(false);
                    SetCurrentStationLabel("—");
                    ResetStreamQuality();
                }

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                UpdateLikeButtonState();
                return;
            }

            if (station.Title.Equals("Magic Radio", StringComparison.OrdinalIgnoreCase))
            {
                var result = await new ContentDialog
                {
                    Title = "Sign In required",
                    Content = "For the Magic Radio station you need to sign in as its required by the broadcaster.",
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();

                if (myVersion != _switchVersion) return;

                if (result == ContentDialogResult.Primary)
                {
                    webViewWindow = new WebViewWindow(station.Title, station.StreamUrl);
                    ApplyCurrentVolumeToAllPlayback(updateSlider: false);

                    try
                    {
                        ActivityHub.Record(
                            ActivityHub.ActivityKind.Radio,
                            title: station.Title ?? "",
                            subtitle: "Web player",
                            payload: station.Title ?? "",
                            listenedSeconds: 0,
                            imageUri: station.Image ?? ""
                        );
                    }
                    catch { }

                    NotificationService.Instance.Show("Radio Started", $"Now playing: {station.Title}");
                    NowPlayingText.Text = station.Title;
                    TryPushNowPlaying(station, "", station.Title);
                    SetCurrentStationLabel(station.Title);
                    SetStreamQuality("Web player - broadcaster controlled");
                    SetDiscordStationPresence(station);
                }
                else
                {
                    NowPlayingText.Text = "Magic Radio cancelled.";
                    TrySetPlaying(false);
                    SetCurrentStationLabel("—");
                    ResetStreamQuality();
                }

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                UpdateLikeButtonState();
                return;
            }

            if (station.Title.Equals("Hits Radio", StringComparison.OrdinalIgnoreCase))
            {
                var result = await new ContentDialog
                {
                    Title = "Sign In required",
                    Content = "To play Hits Radio, sign in on the next page.",
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();

                if (myVersion != _switchVersion) return;

                if (result == ContentDialogResult.Primary)
                {
                    webViewWindow = new WebViewWindow(station.Title, station.StreamUrl);
                    ApplyCurrentVolumeToAllPlayback(updateSlider: false);

                    try
                    {
                        ActivityHub.Record(
                            ActivityHub.ActivityKind.Radio,
                            title: station.Title ?? "",
                            subtitle: "Web player",
                            payload: station.Title ?? "",
                            listenedSeconds: 0,
                            imageUri: station.Image ?? ""
                        );
                    }
                    catch { }

                    NotificationService.Instance.Show("Radio Started", $"Now playing: {station.Title}");
                    NowPlayingText.Text = station.Title;
                    TryPushNowPlaying(station, "", station.Title);
                    SetCurrentStationLabel(station.Title);
                    SetStreamQuality("Web player - broadcaster controlled");
                    SetDiscordStationPresence(station);
                }
                else
                {
                    NowPlayingText.Text = "Hits Radio cancelled.";
                    TrySetPlaying(false);
                    SetCurrentStationLabel("—");
                    ResetStreamQuality();
                }

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                UpdateLikeButtonState();
                return;
            }

            if (station.Title.Equals("Greatest Hits Radio", StringComparison.OrdinalIgnoreCase))
            {
                var result = await new ContentDialog
                {
                    Title = "Sign In required",
                    Content = "To play Greatest Hits Radio, sign in on the next page.",
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();

                if (myVersion != _switchVersion) return;

                if (result == ContentDialogResult.Primary)
                {
                    webViewWindow = new WebViewWindow(station.Title, station.StreamUrl);
                    ApplyCurrentVolumeToAllPlayback(updateSlider: false);

                    try
                    {
                        ActivityHub.Record(
                            ActivityHub.ActivityKind.Radio,
                            title: station.Title ?? "",
                            subtitle: "Web player",
                            payload: station.Title ?? "",
                            listenedSeconds: 0,
                            imageUri: station.Image ?? ""
                        );
                    }
                    catch { }

                    NotificationService.Instance.Show("Radio Started", $"Now playing: {station.Title}");
                    NowPlayingText.Text = station.Title;
                    TryPushNowPlaying(station, "", station.Title);
                    SetCurrentStationLabel(station.Title);
                    SetStreamQuality("Web player - broadcaster controlled");
                    SetDiscordStationPresence(station);
                }
                else
                {
                    NowPlayingText.Text = "Greatest Hits cancelled.";
                    TrySetPlaying(false);
                    SetCurrentStationLabel("—");
                    ResetStreamQuality();
                }

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                UpdateLikeButtonState();
                return;
            }

            if (WebViewStations.Contains(station.Title))
            {
                if (myVersion != _switchVersion) return;

                webViewWindow = new WebViewWindow(station.Title, station.StreamUrl);
                ApplyCurrentVolumeToAllPlayback(updateSlider: false);

                try
                {
                    ActivityHub.Record(
                        ActivityHub.ActivityKind.Radio,
                        title: station.Title ?? "",
                        subtitle: "Web player",
                        payload: station.Title ?? "",
                        listenedSeconds: 0,
                        imageUri: station.Image ?? ""
                    );
                }
                catch { }

                NotificationService.Instance.Show("Radio Started", $"Now playing: {station.Title}");
                NowPlayingText.Text = station.Title;
                TryPushNowPlaying(station, "", station.Title);
                SetCurrentStationLabel(station.Title);
                SetStreamQuality("Web player - broadcaster controlled");
                SetDiscordStationPresence(station);

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                UpdateLikeButtonState();
                return;
            }

            TryPlayDirect(station, versionGuard: myVersion);
        }

        private void TryPlayDirect(RadioStation station, int versionGuard)
        {
            var url = station.StreamUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                FinalFail("No stream URL.");
                return;
            }

            CancelPendingDirectReconnect();
            _directReconnectAttempts = 0;

            DetachDirectPlaybackHandlers();
            _diagnosticStationTitle = station.Title ?? "";
            _diagnosticStreamUrl = url;
            RadioLog("native-start-request", $"version={versionGuard}", flush: true);

            void OnOpened(MediaPlayer sender, object args)
            {
                if (versionGuard != _switchVersion) return;

                RadioLog("media-opened", $"version={versionGuard}; attempts={_directReconnectAttempts}", flush: true);

                TryEnqueueOnUi(() =>
                {
                    NowPlayingText.Text = station.Title;
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                    TrySetPlaying(true);
                    SetCurrentStationLabel(station.Title);
                    SetDiscordStationPresence(station);

                    TrySetImage(new Uri(station.Image));
                    UpdateLikeButtonState();
                    ApplyCurrentVolumeToAllPlayback(updateSlider: false);
                    _directReconnectAttempts = 0;
                    _directReconnectPending = false;

                    try
                    {
                        ActivityHub.Record(
                            ActivityHub.ActivityKind.Radio,
                            title: station.Title ?? "",
                            subtitle: "Direct stream",
                            payload: station.Title ?? "",
                            listenedSeconds: 0,
                            imageUri: station.Image ?? ""
                        );
                    }
                    catch { }
                });

                StartIcyWatcher(url, station);
                _ = DetectAndSetDirectStreamQualityAsync(station, url, versionGuard);
            }

            void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
            {
                if (versionGuard != _switchVersion) return;

                var hr = args.ExtendedErrorCode?.HResult ?? 0;
                RadioLog("media-failed", $"error={args.Error}; message={args.ErrorMessage}; hresult=0x{hr:X8}; attempts={_directReconnectAttempts}", flush: true);

                if (TryScheduleDirectReconnect(station, url, versionGuard, $"failed: {args.Error}"))
                    return;

                TryEnqueueOnUi(() =>
                {
                    NowPlayingText.Text = $"Failed: {args.Error} (0x{hr:X8})";
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                    TrySetPlaying(false);
                    SetCurrentStationLabel("—");
                    ResetStreamQuality();
                    UpdateLikeButtonState();
                });

                StopDirectPlaybackRecovery();
            }

            void OnEnded(MediaPlayer sender, object args)
            {
                if (versionGuard != _switchVersion) return;
                RadioLog("media-ended", $"attempts={_directReconnectAttempts}", flush: true);
                TryScheduleDirectReconnect(station, url, versionGuard, "stream ended");
            }

            _directMediaOpenedHandler = OnOpened;
            _directMediaFailedHandler = OnFailed;
            _directMediaEndedHandler = OnEnded;
            _directPlaybackStateChangedHandler = (session, args) =>
                RadioLog("playback-state-changed", $"newState={session.PlaybackState}", flush: true);
            _mp.MediaOpened += _directMediaOpenedHandler;
            _mp.MediaFailed += _directMediaFailedHandler;
            _mp.MediaEnded += _directMediaEndedHandler;
            _mp.PlaybackSession.PlaybackStateChanged += _directPlaybackStateChangedHandler;

            // Subscribe before assigning the source. Some radio endpoints open or fail
            // quickly enough that subscribing afterwards can miss the only recovery event.
            _mp.Source = MediaSource.CreateFromUri(new Uri(url));
            ApplyCurrentVolumeToAllPlayback(updateSlider: false);
            _mp.Play();
            StartRadioDiagnosticHeartbeat();
            RadioLog("native-play-called", $"version={versionGuard}", flush: true);

            NotificationService.Instance.Show("Radio Started", $"Now playing: {station.Title}");
            TryPushNowPlaying(station, "", station.Title);

            void FinalFail(string msg)
            {
                TryEnqueueOnUi(() =>
                {
                    NowPlayingText.Text = msg;
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                    TrySetPlaying(false);
                    SetCurrentStationLabel("—");
                    ResetStreamQuality();
                    UpdateLikeButtonState();
                });
            }
        }

        private bool TryScheduleDirectReconnect(RadioStation station, string url, int versionGuard, string reason)
        {
            if (station == null || string.IsNullOrWhiteSpace(url) || versionGuard != _switchVersion)
                return false;

            if (_directReconnectPending)
                return true;

            CancelPendingDirectReconnect();
            _directReconnectCts = new CancellationTokenSource();
            _directReconnectPending = true;
            var token = _directReconnectCts.Token;
            var attempt = ++_directReconnectAttempts;
            RadioLog("reconnect-scheduled", $"attempt={attempt}; reason={reason}", flush: true);

            TryEnqueueOnUi(() =>
            {
                NowPlayingText.Text = $"{station.Title} reconnecting...";
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                SetCurrentStationLabel(station.Title);
                TrySetPlaying(false);
            });

            _ = ReconnectDirectStationAsync(station, url, versionGuard, attempt, reason, token);
            return true;
        }

        private async Task ReconnectDirectStationAsync(RadioStation station, string url, int versionGuard, int attempt, string reason, CancellationToken token)
        {
            try
            {
                var delaySeconds = Math.Min(30, Math.Max(1, attempt * 2));
                RadioLog("reconnect-waiting", $"attempt={attempt}; delaySeconds={delaySeconds}; reason={reason}");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                if (token.IsCancellationRequested || versionGuard != _switchVersion)
                    return;

                TryEnqueueOnUi(() =>
                {
                    try
                    {
                        _directReconnectPending = false;
                        RadioLog("reconnect-source-replace", $"attempt={attempt}; reason={reason}", flush: true);
                        _mp.Source = MediaSource.CreateFromUri(new Uri(url));
                        ApplyCurrentVolumeToAllPlayback(updateSlider: false);
                        _mp.Play();
                        NowPlayingText.Text = station.Title;
                        LoadingRing.IsActive = false;
                        LoadingRing.Visibility = Visibility.Collapsed;
                        TrySetPlaying(true);
                        SetCurrentStationLabel(station.Title);
                        SetDiscordStationPresence(station);
                        UpdateLikeButtonState();
                    }
                    catch
                    {
                        RadioLog("reconnect-source-replace-threw", $"attempt={attempt}; reason={reason}", flush: true);
                        TryScheduleDirectReconnect(station, url, versionGuard, reason);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                RadioLog("reconnect-cancelled", $"attempt={attempt}; reason={reason}");
            }
        }

        private void CancelPendingDirectReconnect()
        {
            try
            {
                _directReconnectCts?.Cancel();
                _directReconnectCts?.Dispose();
            }
            catch { }

            _directReconnectCts = null;
            _directReconnectPending = false;
        }

        private void DetachDirectPlaybackHandlers()
        {
            try
            {
                if (_directMediaOpenedHandler != null) _mp.MediaOpened -= _directMediaOpenedHandler;
                if (_directMediaFailedHandler != null) _mp.MediaFailed -= _directMediaFailedHandler;
                if (_directMediaEndedHandler != null) _mp.MediaEnded -= _directMediaEndedHandler;
                if (_directPlaybackStateChangedHandler != null) _mp.PlaybackSession.PlaybackStateChanged -= _directPlaybackStateChangedHandler;
            }
            catch { }

            _directMediaOpenedHandler = null;
            _directMediaFailedHandler = null;
            _directMediaEndedHandler = null;
            _directPlaybackStateChangedHandler = null;
        }

        private void StopDirectPlaybackRecovery()
        {
            CancelPendingDirectReconnect();
            DetachDirectPlaybackHandlers();
            try { _radioDiagnosticTimer?.Stop(); } catch { }
        }

        private void StartRadioDiagnosticHeartbeat()
        {
            if (_radioDiagnosticTimer == null)
            {
                _radioDiagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _radioDiagnosticTimer.Tick += (_, __) => RadioLog("heartbeat");
            }

            _radioDiagnosticTimer.Start();
        }

        private void RadioLog(string eventName, string details = null, bool flush = false)
        {
            try
            {
                DiagnosticLogService.EnsureLogFile();

                var state = "unavailable";
                var source = "unknown";
                var position = "unknown";
                try
                {
                    state = _mp?.PlaybackSession?.PlaybackState.ToString() ?? "no-player";
                    source = _mp?.Source == null ? "null" : _mp.Source.GetType().Name;
                    position = _mp?.PlaybackSession?.Position.ToString() ?? "unknown";
                }
                catch { }

                var connectivity = "unknown";
                try
                {
                    connectivity = global::Windows.Networking.Connectivity.NetworkInformation
                        .GetInternetConnectionProfile()?.GetNetworkConnectivityLevel().ToString() ?? "no-profile";
                }
                catch { }

                var message = $"[Radio] event={eventName}; station={_diagnosticStationTitle}; state={state}; source={source}; position={position}; reconnectPending={_directReconnectPending}; reconnectAttempts={_directReconnectAttempts}; switchVersion={_switchVersion}; servicePlaying={AppPlaybackService.Instance.IsPlaying}; widgetKeepAlive={RadioWidgetKeepAlive.IsOpen}; backgroundKeepAlive={RadioBackgroundKeepAlive.Enabled}; connectivity={connectivity}; url={_diagnosticStreamUrl}";
                if (!string.IsNullOrWhiteSpace(details))
                    message += "; " + details;

                DiagnosticLogService.WriteLine(message);
                if (flush)
                    DiagnosticLogService.Flush();
            }
            catch { }
        }
        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isApplyingVolumeFromCode || !_volumeReady)
                return;

            try
            {
                _currentVolume = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
                SaveCurrentVolume();
                ApplyCurrentVolumeToAllPlayback(updateSlider: false);
            }
            catch { }
        }

        private void ApplyCurrentVolumeToAllPlayback(bool updateSlider)
        {
            try
            {
                if (_mp != null)
                    _mp.Volume = _currentVolume;
            }
            catch { }

            try
            {
                webViewWindow?.SetVolume(_currentVolume);
            }
            catch { }

            try
            {
                if (updateSlider && VolumeSlider != null)
                {
                    _isApplyingVolumeFromCode = true;
                    VolumeSlider.Value = Math.Round(_currentVolume * 100.0);
                    _isApplyingVolumeFromCode = false;
                }
            }
            catch
            {
                _isApplyingVolumeFromCode = false;
            }

            UpdateVolumeText(_currentVolume * 100.0);
        }

        private void UpdateVolumeText(double value)
        {
            try
            {
                if (VolumeText != null)
                    VolumeText.Text = $"{Math.Round(value):0}%";
            }
            catch { }
        }

        private double LoadSavedVolume()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue(RadioVolumeSettingKey, out object value) && value != null)
                {
                    double parsed;
                    if (value is double d)
                        parsed = d;
                    else if (value is float f)
                        parsed = f;
                    else if (value is int i)
                        parsed = i / 100.0;
                    else if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        parsed = 1.0;

                    parsed = Math.Clamp(parsed, 0.0, 1.0);
                    _sharedRadioVolume = parsed;
                    return parsed;
                }
            }
            catch { }

            try
            {
                if (_mp != null)
                {
                    var mpVolume = Math.Clamp(_mp.Volume, 0.0, 1.0);
                    _sharedRadioVolume = mpVolume;
                    return mpVolume;
                }
            }
            catch { }

            try
            {
                var serviceVolume = Math.Clamp(AppPlaybackService.Instance.RadioVolume, 0.0, 1.0);
                _sharedRadioVolume = serviceVolume;
                return serviceVolume;
            }
            catch { }

            try
            {
                if (_sharedRadioVolume.HasValue)
                    return Math.Clamp(_sharedRadioVolume.Value, 0.0, 1.0);
            }
            catch { }

            _sharedRadioVolume = 1.0;
            return 1.0;
        }

        private void SaveCurrentVolume()
        {
            try
            {
                _currentVolume = Math.Clamp(_currentVolume, 0.0, 1.0);
                _sharedRadioVolume = _currentVolume;
                ApplicationData.Current.LocalSettings.Values[RadioVolumeSettingKey] = _currentVolume;
            }
            catch { }

            try
            {
                if (_mp != null)
                    _mp.Volume = _currentVolume;
            }
            catch { }

            try
            {
                AppPlaybackService.Instance.SetRadioVolume(_currentVolume, notifyApplier: false);
            }
            catch { }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            RadioBackgroundKeepAlive.Enabled = false;
            try
            {
                if (EnableBackgroundButton != null)
                {
                    EnableBackgroundButton.IsEnabled = true;
                    EnableBackgroundButton.Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new FontIcon{ Glyph = "\uE7C3" },
                            new TextBlock{ Text = "play the current station in the background of this app" }
                        }
                    };
                }
            }
            catch { }

            StopPlayback();
            NowPlayingText.Text = "Stopped";
            NowPlayingImage.Source = null;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            TrySetPlaying(false);
            SetCurrentStationLabel("—");
            ResetStreamQuality();
            ClearDiscordPresence();
            UpdateLikeButtonState();

            try { AppPlaybackService.Instance.UpdateNowPlaying("", "", null, null); } catch { }
        }

        private async void LikeSongButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LikeSongButton.IsEnabled = false;

                var artist = _lastArtist?.Trim() ?? "";
                var title = _lastTitle?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
                {
                    (artist, title) = ParseFromNowPlayingText(NowPlayingText?.Text);
                }

                if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
                {
                    await new ContentDialog
                    {
                        Title = "No song to save",
                        Content = "I couldn't detect the current track yet. Try again in a few seconds once metadata appears.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                    return;
                }

                var album = "";
                var artUrl = _lastArtworkUri?.ToString() ?? "";
                var stationName = _currentStationTitle ?? "—";

                await LikedRadioLikesService.Instance.AddOrUpdateAsync(title, artist, album, artUrl, stationName);

                try { NotificationService.Instance.Show("Saved", $"Added: {artist} - {title}"); }
                catch
                {
                    await new ContentDialog
                    {
                        Title = "Saved",
                        Content = $"{artist} - {title} added to Liked radio songs.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                }
            }
            catch
            {
                try { NotificationService.Instance.Show("Error", "Couldn't save this song."); }
                catch
                {
                    await new ContentDialog
                    {
                        Title = "Error",
                        Content = "Couldn't save this song.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                }
            }
            finally
            {
                UpdateLikeButtonState();
            }
        }

        private async void EnableBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            var hasStation = !string.IsNullOrWhiteSpace(_currentStationTitle) && _currentStationTitle != "—";
            var isPlaying = false;
            try { isPlaying = AppPlaybackService.Instance.IsPlaying; } catch { }
            if (!isPlaying)
            {
                var session = _mp?.PlaybackSession;
                isPlaying = session?.PlaybackState == MediaPlaybackState.Playing || _mp?.Source != null;
            }

            if (!hasStation || !isPlaying)
            {
                await new ContentDialog
                {
                    Title = "Nothing to keep playing",
                    Content = "Start a radio station first, then enable background playback.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();
                return;
            }

            RadioBackgroundKeepAlive.Enabled = true;

            try
            {
                EnableBackgroundButton.IsEnabled = false;
                EnableBackgroundButton.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon{ Glyph = "\uE73E" },
                        new TextBlock{ Text = "Background playback enabled" }
                    }
                };
            }
            catch { }

            RefreshUiFromService(force: true);

            try { NotificationService.Instance.Show("Background playback", "This station will continue playing as you browse the app."); }
            catch { }
        }

        private async void OpenMiniPlayer_Click(object sender, RoutedEventArgs e)
        {
            bool isPlaying = false;
            try { isPlaying = AppPlaybackService.Instance.IsPlaying; } catch { }
            if (!isPlaying)
            {
                var session = _mp?.PlaybackSession;
                isPlaying = session?.PlaybackState == MediaPlaybackState.Playing || _mp?.Source != null;
            }

            if (isPlaying)
            {
                RadioWidgetKeepAlive.IsOpen = true;
                MiniRadioWidgetWindow.ShowSingleton();
                var wnd = MiniRadioWidgetWindow.Current;
                if (wnd != null)
                {

                    wnd.Closed -= MiniClosed;
                    wnd.Closed += MiniClosed;
                }
            }
            else
            {
                var dlg = new ContentDialog
                {
                    Title = "No station is playing",
                    Content = "Start a radio station first, then open the mini player.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dlg.ShowAsync();
            }

            void MiniClosed(object s, WindowEventArgs e)
            {
                RadioWidgetKeepAlive.IsOpen = false;
                try { AppPlaybackService.Instance.RequestStop(); } catch { }
                try { StopPlayback(); } catch { }
                SetCurrentStationLabel("—");
                ResetStreamQuality();
                UpdateLikeButtonState();
            }
        }

        private void StopPlayback()
        {
            if (RadioWidgetKeepAlive.IsOpen || RadioBackgroundKeepAlive.Enabled)
            {
                RadioLog("stop-playback-skipped", "keep-alive is active", flush: true);
                return;
            }

            RadioLog("stop-playback", "page stop path clearing native source", flush: true);
            StopDirectPlaybackRecovery();
            try { _mp?.Pause(); } catch { }
            try { if (_mp != null) _mp.Source = null; } catch { }
            try { webViewWindow?.StopStream(); } catch { }
            try { _nowPlayingWatcher?.Dispose(); _nowPlayingWatcher = null; } catch { }
            try { _artCts?.Cancel(); _artCts?.Dispose(); _artCts = null; } catch { }

            _lastArtist = "";
            _lastTitle = "";
            _lastArtworkUri = null;
            _everHadMeta = false;
            _lastImageShownUri = null;

            SetCurrentStationLabel("—");
            ResetStreamQuality();
        }

        private void ForceStopAllPlayback()
        {
            RadioLog("force-stop-all", "station switch path clearing all playback", flush: true);
            StopDirectPlaybackRecovery();
            _directReconnectAttempts = 0;
            try { RadioWidgetKeepAlive.IsOpen = false; } catch { }
            try { MiniRadioWidgetWindow.Current?.Close(); } catch { }
            try { _mp?.Pause(); } catch { }
            try { if (_mp != null) _mp.Source = null; } catch { }
            try { webViewWindow?.StopStream(); webViewWindow = null; } catch { }
            try { _nowPlayingWatcher?.Dispose(); _nowPlayingWatcher = null; } catch { }
            try { _artCts?.Cancel(); _artCts?.Dispose(); _artCts = null; } catch { }

            _lastArtist = "";
            _lastTitle = "";
            _lastArtworkUri = null;
            _everHadMeta = false;
            _lastImageShownUri = null;

            ResetStreamQuality();
        }

        private void TryWireServiceEvents()
        {
            try
            {
                var s = AppPlaybackService.Instance;
                s.NowPlayingChanged -= Service_NowPlayingChanged;
                s.IsPlayingChanged -= Service_IsPlayingChanged;
                s.NowPlayingChanged += Service_NowPlayingChanged;
                s.IsPlayingChanged += Service_IsPlayingChanged;
            }
            catch { }
        }

        private void TryUnwireServiceEvents()
        {
            try
            {
                var s = AppPlaybackService.Instance;
                s.NowPlayingChanged -= Service_NowPlayingChanged;
                s.IsPlayingChanged -= Service_IsPlayingChanged;
            }
            catch { }
        }

        private void Service_NowPlayingChanged(object sender, EventArgs e)
        {
            RefreshUiFromService(force: true);
        }

        private void Service_IsPlayingChanged(object sender, EventArgs e)
        {
            RefreshUiFromService(force: false);
        }

        private void RefreshUiFromService(bool force)
        {
            var s = AppPlaybackService.Instance;

            string station = s.CurrentStationTitle?.Trim() ?? "—";
            string artist = s.CurrentArtist?.Trim() ?? "";
            string title = s.CurrentTitle?.Trim() ?? "";
            Uri? artUri = s.CurrentArtworkUri;

            bool stationChanged = !string.Equals(station, _currentStationTitle, StringComparison.OrdinalIgnoreCase);
            bool metaChanged = !string.Equals(artist, _lastArtist, StringComparison.OrdinalIgnoreCase) ||
                               !string.Equals(title, _lastTitle, StringComparison.OrdinalIgnoreCase);
            bool artArrived = (artUri != null);
            bool artChanged = artArrived && (_lastArtworkUri == null || !_lastArtworkUri.Equals(artUri));

            if (!force && !stationChanged && !metaChanged && !artChanged)
            {
                if (artArrived && (_lastImageShownUri == null || !_lastImageShownUri.Equals(artUri)))
                    TrySetImage(artUri);
                return;
            }

            if (!string.IsNullOrWhiteSpace(station))
                SetCurrentStationLabel(station);

            if (metaChanged)
                _everHadMeta = !string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(title);

            _lastArtist = artist;
            _lastTitle = title;
            if (artArrived && artChanged) _lastArtworkUri = artUri;

            TryEnqueueOnUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(_lastArtist) || !string.IsNullOrWhiteSpace(_lastTitle))
                    NowPlayingText.Text = string.IsNullOrWhiteSpace(_lastArtist) ? _lastTitle : $"{_lastArtist} - {_lastTitle}";
                else
                    NowPlayingText.Text = _currentStationTitle;

                if (artArrived)
                {
                    var target = _lastArtworkUri ?? artUri;
                    if (target != null && (_lastImageShownUri == null || !_lastImageShownUri.Equals(target)))
                        TrySetImage(target);
                }
                else
                {
                    var st = GetStationByTitle(_currentStationTitle);
                    if (st != null && !string.IsNullOrWhiteSpace(st.Image))
                    {
                        var logo = new Uri(st.Image);
                        if (_lastImageShownUri == null || !_lastImageShownUri.Equals(logo))
                            TrySetImage(logo);
                    }
                }

                UpdateLikeButtonState();
                UpdateStationAndQualityText();
            });
        }

        private void PushStationToService(RadioStation station, bool isPlaying)
        {
            try
            {
                AppPlaybackService.Instance.SetStationPlaying(
                    stationId: station.Title?.Replace(" ", "_") ?? "",
                    stationTitle: station.Title,
                    stationLogoUri: SafeUri(station.Image),
                    isPlaying: isPlaying);
            }
            catch { }
        }

        private void TryPushNowPlaying(RadioStation station, string artist, string title, Uri? artwork = null)
        {
            try
            {
                _lastArtist = artist?.Trim() ?? "";
                _lastTitle = title?.Trim() ?? "";
                if (artwork != null) _lastArtworkUri = artwork;

                AppPlaybackService.Instance.UpdateNowPlaying(
                    artist: _lastArtist,
                    title: string.IsNullOrWhiteSpace(_lastTitle) ? (station?.Title ?? "") : _lastTitle,
                    duration: null,
                    artistImageUri: _lastArtworkUri
                );

                if (!string.IsNullOrWhiteSpace(_lastArtist) || !string.IsNullOrWhiteSpace(_lastTitle))
                {
                    UpdateDiscordTrackPresence(station, _lastArtist, _lastTitle);
                }
                else
                {
                    SetDiscordStationPresence(station);
                }

                TryEnqueueOnUi(() =>
                {
                    if (!string.IsNullOrWhiteSpace(_lastArtist) || !string.IsNullOrWhiteSpace(_lastTitle))
                        NowPlayingText.Text = string.IsNullOrWhiteSpace(_lastArtist) ? _lastTitle : $"{_lastArtist} - {_lastTitle}";
                    else
                        NowPlayingText.Text = _currentStationTitle;

                    if (_lastArtworkUri != null &&
                        (_lastImageShownUri == null || !_lastImageShownUri.Equals(_lastArtworkUri)))
                    {
                        TrySetImage(_lastArtworkUri);
                    }

                    UpdateStationAndQualityText();
                });
            }
            catch { }
        }

        private void StartIcyWatcher(string streamUrl, RadioStation station)
        {
            try
            {
                _nowPlayingWatcher?.Dispose();
                _nowPlayingWatcher = new IcyNowPlayingWatcher(streamUrl);
                _nowPlayingWatcher.NowPlayingChanged += (a, t) =>
                {
                    try { _artCts?.Cancel(); } catch { }
                    _artCts = new CancellationTokenSource();

                    string artist = a?.Trim() ?? "";
                    string title = t?.Trim() ?? "";
                    var segment = ClassifyRadioSegment(artist, title, station?.Title);

                    if (segment == RadioSegment.Adverts)
                    {
                        artist = "";
                        title = "Playing adverts";
                    }
                    else if (segment == RadioSegment.DjTalking)
                    {
                        artist = "";
                        title = "DJ talking";
                    }

                    bool trackChanged =
                        !string.Equals(artist, _lastArtist, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(title, _lastTitle, StringComparison.OrdinalIgnoreCase);

                    if (trackChanged && segment != RadioSegment.DjTalking)
                        _lastArtworkUri = null;

                    var segmentArtwork = segment == RadioSegment.Adverts
                        ? new Uri("ms-appx:///Assets/Radio/ad-break.png")
                        : null;

                    TryPushNowPlaying(station, artist, title, segmentArtwork);

                    if (segment == RadioSegment.Song)
                        _ = LookupAndApplyArtworkAsync(artist, title, station, _artCts.Token);
                };
                _nowPlayingWatcher.Start();
            }
            catch { }
        }

        private enum RadioSegment
        {
            Song,
            DjTalking,
            Adverts
        }

        private static RadioSegment ClassifyRadioSegment(string artist, string title, string stationTitle = null)
        {
            string metadata = $"{artist} {title}".Trim();

            if (ContainsAny(metadata,
                "advert", "commercial break", "ad break", "sponsored",
                "sponsor message", "promotion", "promo break", "ad insertion",
                "ad replacement", "advertising"))
            {
                return RadioSegment.Adverts;
            }

            if (!string.IsNullOrWhiteSpace(stationTitle) &&
                string.Equals(metadata, stationTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return RadioSegment.DjTalking;
            }

            if (ContainsAny(metadata,
                "dj", "presenter", "in the studio", "the studio",
                "breakfast show", "morning show", "drive time", "drivetime",
                "radio show", "on air", "phone-in", "phone in",
                "traffic update", "travel update", "weather update",
                "news update", "headlines"))
            {
                return RadioSegment.DjTalking;
            }

            return RadioSegment.Song;
        }

        private static bool ContainsAny(string value, params string[] markers)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return markers.Any(marker =>
                value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private async Task LookupAndApplyArtworkAsync(string artist, string title, RadioStation station, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return;

            try
            {
                ct.ThrowIfCancellationRequested();

                var term = HttpUtility.UrlEncode($"{artist} {title}");
                var url = $"https://itunes.apple.com/search?media=music&limit=1&term={term}";
                using var resp = await _safeHttp.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                const string key = "\"artworkUrl100\":\"";
                var start = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return;
                start += key.Length;
                var end = json.IndexOf('"', start);
                if (end <= start) return;

                var art100 = json.Substring(start, end - start).Replace("\\/", "/");
                var high = art100.Replace("100x100", "600x600");
                var artUri = SafeUri(high) ?? SafeUri(art100);
                if (artUri == null) return;

                ct.ThrowIfCancellationRequested();
                if (!string.Equals(artist, _lastArtist, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(title, _lastTitle, StringComparison.OrdinalIgnoreCase))
                    return;

                _lastArtworkUri = artUri;

                TryPushNowPlaying(station, artist, title, artUri);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private async Task DetectAndSetDirectStreamQualityAsync(RadioStation station, string streamUrl, int versionGuard)
        {
            try
            {
                var quality = await ProbeStreamQualityAsync(streamUrl);

                if (versionGuard != _switchVersion)
                    return;

                TryEnqueueOnUi(() =>
                {
                    if (versionGuard != _switchVersion)
                        return;

                    SetStreamQuality(quality);
                });
            }
            catch
            {
                if (versionGuard != _switchVersion)
                    return;

                TryEnqueueOnUi(() =>
                {
                    if (versionGuard != _switchVersion)
                        return;

                    SetStreamQuality("Direct stream - quality unavailable");
                });
            }
        }

        private async Task<string> ProbeStreamQualityAsync(string streamUrl)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            request.Headers.TryAddWithoutValidation("User-Agent", "Zink/1.0");

            using var response = await _safeHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            string codec = GuessCodec(streamUrl, mediaType);
            int? bitrate = TryReadBitrateFromHeaders(response);

            bool looksLikePlaylist =
                streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("x-mpegurl", StringComparison.OrdinalIgnoreCase);

            if (looksLikePlaylist)
            {
                try
                {
                    using var playlistResp = await _safeHttp.GetAsync(streamUrl);
                    if (playlistResp.IsSuccessStatusCode)
                    {
                        string playlist = await playlistResp.Content.ReadAsStringAsync();
                        var playlistBitrate = TryReadBandwidthFromM3u8(playlist);
                        if (playlistBitrate.HasValue)
                            bitrate = playlistBitrate;

                        var playlistCodec = TryReadCodecFromM3u8(playlist);
                        if (!string.IsNullOrWhiteSpace(playlistCodec))
                            codec = playlistCodec;
                    }
                }
                catch { }

                return BuildQualityLabel("Direct HLS", codec, bitrate);
            }

            if (string.IsNullOrWhiteSpace(codec))
                codec = "Unknown codec";

            return BuildQualityLabel("Direct stream", codec, bitrate);
        }

        private int? TryReadBitrateFromHeaders(HttpResponseMessage response)
        {
            try
            {
                if (response.Headers.TryGetValues("icy-br", out var brValues))
                {
                    var raw = brValues.FirstOrDefault();
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kbps) && kbps > 0)
                        return kbps;
                }
            }
            catch { }

            try
            {
                if (response.Content.Headers.ContentType?.Parameters != null)
                {
                    foreach (var p in response.Content.Headers.ContentType.Parameters)
                    {
                        if (string.Equals(p.Name?.Trim(), "bitrate", StringComparison.OrdinalIgnoreCase))
                        {
                            var value = (p.Value ?? "").Trim().Trim('"');
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bitsPerSecond) && bitsPerSecond > 0)
                                return bitsPerSecond >= 1000 ? bitsPerSecond / 1000 : bitsPerSecond;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private int? TryReadBandwidthFromM3u8(string playlist)
        {
            if (string.IsNullOrWhiteSpace(playlist))
                return null;

            int best = 0;
            const string token = "BANDWIDTH=";
            int index = 0;

            while (index < playlist.Length)
            {
                int hit = playlist.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                    break;

                hit += token.Length;
                int end = hit;
                while (end < playlist.Length && char.IsDigit(playlist[end]))
                    end++;

                if (end > hit)
                {
                    var number = playlist.Substring(hit, end - hit);
                    if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bps) && bps > best)
                        best = bps;
                }

                index = end;
            }

            return best > 0 ? best / 1000 : null;
        }

        private string TryReadCodecFromM3u8(string playlist)
        {
            if (string.IsNullOrWhiteSpace(playlist))
                return null;

            const string token = "CODECS=\"";
            int hit = playlist.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
                return null;

            hit += token.Length;
            int end = playlist.IndexOf('"', hit);
            if (end <= hit)
                return null;

            var codecs = playlist.Substring(hit, end - hit);
            if (codecs.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
                return "AAC";
            if (codecs.Contains("mp3", StringComparison.OrdinalIgnoreCase))
                return "MP3";
            if (codecs.Contains("opus", StringComparison.OrdinalIgnoreCase))
                return "Opus";

            return codecs;
        }

        private string GuessCodec(string url, string mediaType)
        {
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                if (mediaType.Contains("aac", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Contains("audio/aac", StringComparison.OrdinalIgnoreCase))
                    return "AAC";

                if (mediaType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
                    return "MP3";

                if (mediaType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Contains("opus", StringComparison.OrdinalIgnoreCase))
                    return "Opus";

                if (mediaType.Contains("flac", StringComparison.OrdinalIgnoreCase))
                    return "FLAC";
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (url.Contains(".aac", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("aac", StringComparison.OrdinalIgnoreCase))
                    return "AAC";

                if (url.Contains(".mp3", StringComparison.OrdinalIgnoreCase))
                    return "MP3";

                if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    return "HLS";

                if (url.Contains(".ogg", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("opus", StringComparison.OrdinalIgnoreCase))
                    return "Opus";

                if (url.Contains(".flac", StringComparison.OrdinalIgnoreCase))
                    return "FLAC";
            }

            return "Unknown codec";
        }

        private string BuildQualityLabel(string sourceType, string codec, int? bitrateKbps)
        {
            if (bitrateKbps.HasValue && bitrateKbps.Value > 0)
                return $"{sourceType} - {codec} - ~{bitrateKbps.Value} kbps";

            return $"{sourceType} - {codec}";
        }

        private void SetStreamQuality(string quality)
        {
            _currentStreamQuality = string.IsNullOrWhiteSpace(quality) ? "Unknown" : quality.Trim();
            UpdateStationAndQualityText();
        }

        private void ResetStreamQuality()
        {
            _currentStreamQuality = "Unknown";
            UpdateStationAndQualityText();
        }

        private void UpdateStationAndQualityText()
        {
            try
            {
                if (CurrentStationText != null)
                    CurrentStationText.Text = $"The now playing station: {_currentStationTitle}\nStream quality: {_currentStreamQuality}";
            }
            catch { }
        }

        private string GetDiscordStationAssetKey(string? stationTitle)
        {
            return "zink_1024";
        }

        private void SetDiscordStationPresence(RadioStation station)
        {
            try
            {
                if (station == null || string.IsNullOrWhiteSpace(station.Title))
                    return;

                DiscordPresenceService.Instance.SetRadioPresence(
                    stationName: station.Title,
                    songTitle: null,
                    artistName: null,
                    stationAssetKey: GetDiscordStationAssetKey(station.Title)
                );
            }
            catch { }
        }

        private void UpdateDiscordTrackPresence(RadioStation station, string artist, string title)
        {
            try
            {
                if (station == null || string.IsNullOrWhiteSpace(station.Title))
                    return;

                DiscordPresenceService.Instance.UpdateRadioTrack(
                    stationName: station.Title,
                    songTitle: string.IsNullOrWhiteSpace(title) ? null : title,
                    artistName: string.IsNullOrWhiteSpace(artist) ? null : artist,
                    stationAssetKey: GetDiscordStationAssetKey(station.Title)
                );
            }
            catch { }
        }

        private void ClearDiscordPresence()
        {
            try
            {
                DiscordPresenceService.Instance.Clear();
            }
            catch { }
        }

        private void SetCurrentStationLabel(string text)
        {
            try
            {
                _currentStationTitle = string.IsNullOrWhiteSpace(text) ? "—" : text;
                UpdateStationAndQualityText();
            }
            catch { }
        }

        private static Uri? SafeUri(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;
            try { return new Uri(uri); } catch { return null; }
        }

        private (string artist, string title) ParseFromNowPlayingText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return ("", "");
            var sep = text.IndexOf(" - ");
            if (sep > 0) return (text.Substring(0, sep).Trim(), text[(sep + 3)..].Trim());
            return ("", text.Trim());
        }

        private RadioStation GetStationByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            return RadioStations?.FirstOrDefault(s => string.Equals(s?.Title, title, StringComparison.OrdinalIgnoreCase));
        }

        private void TrySetImage(Uri uri)
        {
            try
            {
                if (_lastImageShownUri != null && _lastImageShownUri.Equals(uri))
                    return;

                var bmp = new BitmapImage();
                bmp.UriSource = uri;
                NowPlayingImage.Source = bmp;
                _lastImageShownUri = uri;
            }
            catch
            {
                try
                {
                    NowPlayingImage.Source = new BitmapImage(uri);
                    _lastImageShownUri = uri;
                }
                catch { }
            }
        }

        private void TrySetPlaying(bool isPlaying)
        {
            try { AppPlaybackService.Instance.SetIsPlaying(isPlaying); } catch { }
        }

        private void UpdateLikeButtonState()
        {
            try
            {
                if (LikeSongButton == null) return;
                bool hasStation = !string.IsNullOrWhiteSpace(_currentStationTitle) && _currentStationTitle != "—";
                bool hasMeta = _everHadMeta || !string.IsNullOrWhiteSpace(_lastArtist) || !string.IsNullOrWhiteSpace(_lastTitle);
                LikeSongButton.IsEnabled = hasStation && (hasMeta || !string.IsNullOrWhiteSpace(NowPlayingText?.Text));
            }
            catch { }
            finally
            {
                try { LikeSongButton.IsEnabled = true; } catch { }
            }
        }

        private void StartElapsedTimer()
        {
            try
            {
                if (_elapsedTimer == null)
                {
                    _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _elapsedTimer.Tick += (_, __) =>
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(_currentStationTitle) || _currentStationTitle == "—")
                                return;

                            AppPlaybackService.Instance.UpdateNowPlaying(
                                artist: _lastArtist,
                                title: string.IsNullOrWhiteSpace(_lastTitle) ? _currentStationTitle : _lastTitle,
                                duration: null,
                                artistImageUri: _lastArtworkUri
                            );

                            if (_lastArtworkUri != null &&
                                (_lastImageShownUri == null || !_lastImageShownUri.Equals(_lastArtworkUri)))
                            {
                                TrySetImage(_lastArtworkUri);
                            }
                        }
                        catch { }
                    };
                }
                _elapsedTimer.Start();
            }
            catch { }
        }

        private void StopElapsedTimer()
        {
            try { _elapsedTimer?.Stop(); } catch { }
        }

        private void StartServicePollTimer()
        {
            try
            {
                if (_servicePollTimer == null)
                {
                    _servicePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                    _servicePollTimer.Tick += (_, __) => RefreshUiFromService(force: false);
                }
                _servicePollTimer.Start();
            }
            catch { }
        }

        private void StopServicePollTimer()
        {
            try { _servicePollTimer?.Stop(); } catch { }
        }

        private sealed class IcyNowPlayingWatcher : IDisposable
        {
            private readonly string _url;
            private CancellationTokenSource _cts;
            public event Action<string, string> NowPlayingChanged;

            public IcyNowPlayingWatcher(string url) => _url = url;

            public void Start()
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(_cts.Token));
            }

            private async Task LoopAsync(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }, true);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Icy-MetaData", "1");
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("Zink/1.0");

                        using var resp = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct);
                        resp.EnsureSuccessStatusCode();

                        if (!resp.Headers.TryGetValues("icy-metaint", out var values))
                        {
                            await Task.Delay(5000, ct);
                            continue;
                        }

                        var interval = int.Parse(values.First());
                        using var stream = await resp.Content.ReadAsStreamAsync(ct);

                        var buf = new byte[interval];
                        while (!ct.IsCancellationRequested)
                        {
                            int read = 0;
                            while (read < interval)
                            {
                                var n = await stream.ReadAsync(buf, read, interval - read, ct);
                                if (n == 0) throw new EndOfStreamException();
                                read += n;
                            }

                            int metaLenByte = stream.ReadByte();
                            if (metaLenByte < 0) break;

                            int metaLen = metaLenByte * 16;
                            if (metaLen == 0) continue;

                            var metaBuf = new byte[metaLen];
                            read = 0;
                            while (read < metaLen)
                            {
                                var n = await stream.ReadAsync(metaBuf, read, metaLen - read, ct);
                                if (n == 0) break;
                                read += n;
                            }

                            var meta = IcyEncoding.GetString(metaBuf).TrimEnd('\0');

                            // Advert systems often put their marker in StreamUrl or another
                            // field while leaving StreamTitle unchanged or empty.
                            if (ContainsAny(meta,
                                "advert", "commercial break", "ad break", "sponsored",
                                "ad insertion", "ad replacement", "advertising"))
                            {
                                NowPlayingChanged?.Invoke("", "Playing adverts");
                                continue;
                            }

                            var title = ParseStreamTitle(meta, out bool streamTitleWasPresent);
                            if (streamTitleWasPresent)
                            {
                                if (string.IsNullOrWhiteSpace(title))
                                {
                                    // An explicitly cleared title is different from a zero-length
                                    // ICY block (which only means "no metadata change").
                                    NowPlayingChanged?.Invoke("", "DJ talking");
                                }
                                else
                                {
                                    var (artist, song) = SplitArtistTitle(title);
                                    NowPlayingChanged?.Invoke(artist, song);
                                }
                            }
                        }
                    }
                    catch
                    {
                        try { await Task.Delay(3000, ct); } catch { }
                    }
                }
            }

            private static string ParseStreamTitle(string meta, out bool wasPresent)
            {
                const string key = "StreamTitle='";
                var i = meta.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                wasPresent = i >= 0;
                if (!wasPresent) return null;
                i += key.Length;
                var j = meta.IndexOf("';", i, StringComparison.Ordinal);
                if (j < 0) j = meta.Length;
                return meta.Substring(i, j - i).Trim();
            }

            private static (string artist, string title) SplitArtistTitle(string s)
            {
                var sep = s.IndexOf(" - ");
                if (sep > 0) return (s.Substring(0, sep).Trim(), s[(sep + 3)..].Trim());
                return ("", s);
            }

            public void Dispose()
            {
                try { _cts?.Cancel(); } catch { }
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        public class RadioStation
        {
            public string Title { get; set; }
            public string Image { get; set; }
            public string StreamUrl { get; set; }
        }

        internal static class RadioWidgetKeepAlive { public static bool IsOpen { get; set; } }
        internal static class RadioBackgroundKeepAlive { public static bool Enabled { get; set; } }
    }
}

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace Zink.Services
{
    /// <summary>
    /// Single source of truth for now-playing.
    /// - Supports INotifyPropertyChanged (bindings)
    /// - Exposes legacy events + Current* properties so existing pages/widgets keep working.
    /// </summary>
    public sealed class AppPlaybackService : INotifyPropertyChanged
    {
        public static AppPlaybackService Instance { get; } = new();

        public enum MediaKind
        {
            None,
            Music,
            Video,
            Radio
        }

        private const string RadioVolumeSettingKey = "RadioPageVolume";

        private AppPlaybackService()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _radioVolume = ReadSavedRadioVolume();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, __) => RaiseOnUI(() => OnPropertyChanged(nameof(Elapsed)));
            _tick.Start();
        }

        private readonly DispatcherQueue? _dispatcher;
        private readonly DispatcherTimer _tick;

        // ================= RADIO backing fields (kept) =================
        private string _stationTitle = "—";
        private Uri? _stationLogoUri;

        private string _artistName = "";
        private string _songTitle = "";
        private TimeSpan? _duration = null;   // null => live
        private Uri? _artistImageUri = null;

        private DateTimeOffset _startedAt = default;
        private TimeSpan _elapsedBeforePause = TimeSpan.Zero;
        private bool _isPlaying;

        // ================= SHARED RADIO VOLUME =================
        private double _radioVolume = 1.0;
        private Action<double>? _radioVolumeApplier;

        public double RadioVolume
        {
            get
            {
                SyncRadioVolumeFromSettings();
                return _radioVolume;
            }
            set => SetRadioVolume(value);
        }

        public event EventHandler? RadioVolumeChanged;

        public void SetRadioVolume(double volume)
            => SetRadioVolume(volume, notifyApplier: true);

        public void SetRadioVolume(double volume, bool notifyApplier)
        {
            RaiseOnUI(() =>
            {
                volume = NormalizeVolume(volume);

                SyncRadioVolumeFromSettings();
                bool changed = Math.Abs(_radioVolume - volume) > 0.0001;

                _radioVolume = volume;
                SaveRadioVolume(_radioVolume);

                if (changed)
                    OnPropertyChanged(nameof(RadioVolume));

                if (notifyApplier)
                {
                    try
                    {
                        _radioVolumeApplier?.Invoke(_radioVolume);
                    }
                    catch { }
                }

                if (changed)
                    SafeFire(RadioVolumeChanged);
            });
        }

        public void RegisterRadioVolumeApplier(Action<double> applier, bool pushCurrentValue = true)
        {
            RaiseOnUI(() =>
            {
                _radioVolumeApplier = applier;

                if (!pushCurrentValue)
                    return;

                SyncRadioVolumeFromSettings();

                try
                {
                    _radioVolumeApplier?.Invoke(_radioVolume);
                }
                catch { }
            });
        }

        private void SyncRadioVolumeFromSettings()
        {
            try
            {
                _radioVolume = ReadSavedRadioVolume();
            }
            catch
            {
                _radioVolume = NormalizeVolume(_radioVolume);
            }
        }

        private static double ReadSavedRadioVolume()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue(RadioVolumeSettingKey, out var value) && value != null)
                {
                    if (value is double d)
                        return NormalizeVolume(d);

                    if (value is float f)
                        return NormalizeVolume(f);

                    if (value is int i)
                        return NormalizeVolume(i / 100.0);

                    if (double.TryParse(value.ToString(), out var parsed))
                        return NormalizeVolume(parsed);
                }
            }
            catch { }

            return 1.0;
        }

        private static void SaveRadioVolume(double volume)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[RadioVolumeSettingKey] = NormalizeVolume(volume);
            }
            catch { }
        }

        private static double NormalizeVolume(double volume)
        {
            return Math.Clamp(volume, 0.0, 1.0);
        }

        // ================= GENERIC now-playing (Music/Video/Radio) =================
        private MediaKind _currentKind = MediaKind.None;
        private string _primaryText = "";
        private string _secondaryText = "";
        private Uri? _genericArtworkUri = null;

        public MediaKind CurrentKind
        {
            get => _currentKind;
            private set { if (_currentKind != value) { _currentKind = value; OnPropertyChanged(); } }
        }

        public string PrimaryText
        {
            get => _primaryText;
            private set { if (!string.Equals(_primaryText, value, StringComparison.Ordinal)) { _primaryText = value; OnPropertyChanged(); } }
        }

        public string SecondaryText
        {
            get => _secondaryText;
            private set { if (!string.Equals(_secondaryText, value, StringComparison.Ordinal)) { _secondaryText = value; OnPropertyChanged(); } }
        }

        public Uri? GenericArtworkUri
        {
            get => _genericArtworkUri;
            private set { if (_genericArtworkUri != value) { _genericArtworkUri = value; OnPropertyChanged(); } }
        }

        // ================= INotifyPropertyChanged model =================
        public string StationTitle
        {
            get => _stationTitle;
            private set { if (!string.Equals(_stationTitle, value, StringComparison.Ordinal)) { _stationTitle = value; OnPropertyChanged(); } }
        }

        public string ArtistName
        {
            get => _artistName;
            private set { if (!string.Equals(_artistName, value, StringComparison.Ordinal)) { _artistName = value; OnPropertyChanged(); } }
        }

        public string SongTitle
        {
            get => _songTitle;
            private set { if (!string.Equals(_songTitle, value, StringComparison.Ordinal)) { _songTitle = value; OnPropertyChanged(); } }
        }

        public TimeSpan Elapsed
        {
            get
            {
                if (!_isPlaying || _startedAt == default)
                    return _elapsedBeforePause;

                var span = _elapsedBeforePause + (DateTimeOffset.Now - _startedAt);
                return span < TimeSpan.Zero ? TimeSpan.Zero : span;
            }
        }

        public TimeSpan? Duration
        {
            get => _duration;
            private set { if (_duration != value) { _duration = value; OnPropertyChanged(); } }
        }

        /// Prefer artist image; fallback to station logo.
        public Uri? ArtworkOrLogo => _artistImageUri ?? _stationLogoUri;

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged();
                    SafeFire(IsPlayingChanged);
                }
            }
        }

        // ================= Back-compat aliases =================
        public string CurrentStationTitle => StationTitle;
        public Uri? CurrentStationLogoUri => _stationLogoUri;
        public string CurrentArtist => ArtistName;
        public string CurrentTitle => SongTitle;
        public Uri? CurrentArtworkUri => _artistImageUri;

        // Events your pages/widgets subscribe to
        public event EventHandler? NowPlayingChanged;
        public event EventHandler? IsPlayingChanged;

        // ================= Public API used by RadioPage / ICY watcher =================
        public void SetStationPlaying(string stationId, string stationTitle, Uri? stationLogoUri, bool isPlaying)
        {
            RaiseOnUI(() =>
            {
                bool stationChanged = !string.Equals(StationTitle, stationTitle ?? "—", StringComparison.Ordinal);
                StationTitle = stationTitle ?? "—";
                _stationLogoUri = stationLogoUri;

                OnPropertyChanged(nameof(CurrentStationLogoUri));
                OnPropertyChanged(nameof(ArtworkOrLogo));

                CurrentKind = MediaKind.Radio;
                PrimaryText = StationTitle;
                SecondaryText = "";
                GenericArtworkUri = ArtworkOrLogo ?? _stationLogoUri;

                if (stationChanged)
                    ResetElapsed(isPlaying);
                else
                    SetPlayingState(isPlaying);

                SafeFire(NowPlayingChanged);
            });
        }

        public void UpdateNowPlaying(string artist, string title, TimeSpan? duration = null, Uri? artistImageUri = null)
        {
            RaiseOnUI(() =>
            {
                ArtistName = artist ?? "";
                SongTitle = title ?? "";
                Duration = duration;
                _artistImageUri = artistImageUri;

                OnPropertyChanged(nameof(ArtworkOrLogo));

                if (CurrentKind == MediaKind.Radio)
                {
                    PrimaryText = StationTitle;
                    SecondaryText = string.IsNullOrWhiteSpace(ArtistName) && string.IsNullOrWhiteSpace(SongTitle)
                        ? ""
                        : (string.IsNullOrWhiteSpace(ArtistName) ? SongTitle : $"{ArtistName} - {SongTitle}");
                    GenericArtworkUri = ArtworkOrLogo ?? _stationLogoUri;
                }

                SafeFire(NowPlayingChanged);
            });
        }

        public void SetIsPlaying(bool isPlaying) => RaiseOnUI(() => SetPlayingState(isPlaying));

        public void SetGenericNowPlaying(MediaKind kind, string primary, string secondary, Uri? artworkUri, bool isPlaying)
        {
            RaiseOnUI(() =>
            {
                CurrentKind = kind;
                PrimaryText = primary ?? "";
                SecondaryText = secondary ?? "";
                GenericArtworkUri = artworkUri;

                _startedAt = DateTimeOffset.Now;
                OnPropertyChanged(nameof(Elapsed));

                IsPlaying = isPlaying;
                SafeFire(NowPlayingChanged);
            });
        }

        public void ClearIfKind(MediaKind kind)
        {
            RaiseOnUI(() =>
            {
                if (CurrentKind != kind) return;

                CurrentKind = MediaKind.None;
                PrimaryText = "";
                SecondaryText = "";
                GenericArtworkUri = null;

                _startedAt = default;
                OnPropertyChanged(nameof(Elapsed));

                IsPlaying = false;
                SafeFire(NowPlayingChanged);
            });
        }

        // ================= Widget → player intents =================
        public event Action? PlayRequested;
        public event Action? PauseRequested;
        public event Action? StopRequested;

        public void RequestPlay() { try { PlayRequested?.Invoke(); } catch { } }
        public void RequestPause() { try { PauseRequested?.Invoke(); } catch { } }
        public void RequestStop()
        {
            try { StopRequested?.Invoke(); } catch { }
            RaiseOnUI(() => ResetElapsed(false));
        }
        public void RequestWidgetStop()
        {
            try { PauseRequested?.Invoke(); } catch { }
            RaiseOnUI(() => ResetElapsed(false));
        }
        public void RequestLive()
        {
            RaiseOnUI(() => ResetElapsed(true));
            try { PlayRequested?.Invoke(); } catch { }
        }

        // ================= INotifyPropertyChanged =================
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ================= Helpers =================
        private void SafeFire(EventHandler? ev)
        {
            try { ev?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void SetPlayingState(bool isPlaying)
        {
            if (isPlaying == IsPlaying)
                return;

            if (isPlaying)
            {
                _startedAt = DateTimeOffset.Now;
            }
            else if (_startedAt != default)
            {
                var played = DateTimeOffset.Now - _startedAt;
                if (played > TimeSpan.Zero)
                    _elapsedBeforePause += played;
                _startedAt = default;
            }

            IsPlaying = isPlaying;
            OnPropertyChanged(nameof(Elapsed));
        }

        private void ResetElapsed(bool isPlaying)
        {
            _elapsedBeforePause = TimeSpan.Zero;
            _startedAt = isPlaying ? DateTimeOffset.Now : default;
            IsPlaying = isPlaying;
            OnPropertyChanged(nameof(Elapsed));
        }

        private void RaiseOnUI(Action action)
        {
            if (_dispatcher == null)
            {
                try { action(); } catch { }
                return;
            }

            if (_dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                _dispatcher.TryEnqueue(() =>
                {
                    try { action(); } catch { }
                });
            }
        }
    }
}

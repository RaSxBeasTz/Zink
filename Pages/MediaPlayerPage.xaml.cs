using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Web;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Zink.Pages;
using Zink.Services;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Zink
{
    public sealed partial class MusicPlayerPage : Page
    {
        private MediaPlayer _mediaPlayer => MediaPlayerSingleton.Instance;
        private DispatcherTimer _timer;
        private ObservableCollection<MusicTrack> _queue = new();
        private ObservableCollection<MusicTrack> _selectedTracks = new();
        private readonly Dictionary<string, BitmapImage> _artworkByPath = new(StringComparer.OrdinalIgnoreCase);
        private int _currentIndex = -1;
        private IReadOnlyList<ZinkLyrics.LyricLine> _timedLyrics = Array.Empty<ZinkLyrics.LyricLine>();
        private int _activeLyricIndex = -1;
        private int _activeWordIndex = -1;
        private string? _loadedLyricsPath;
        private string? _lyricsPlaybackPath;
        private int _lyricsRequestId;
        private System.Threading.CancellationTokenSource? _lyricsCancellation;
        private readonly ZinkLyrics.LyricsReader _lyricsReader = new(
            Path.Combine(ApplicationData.Current.LocalFolder.Path, "LyricsCache"));
        private bool _isShuffle = false;
        private bool _isRepeat = false;
        private Random _random = new();

        private static MusicPlayerState _savedState;
        private static string _currentlyPlayingPath = null;
        private const string BACKGROUND_PLAY_KEY = "MusicPlayer_BackgroundEnabled";

        private double _lastProgressPct = -1;

        private const string MUSIC_POS_PREFIX = "Zink_MusicPos_";
        private double _lastSavedPosSeconds = -1;
        private DateTime _lastPosSaveUtc = DateTime.MinValue;

        private static readonly HttpClient _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });

        private const string DASH_LastKind = "HomeDash_LastKind";
        private const string DASH_LastPath = "HomeDash_LastPath";
        private const string DASH_LastTitle = "HomeDash_LastTitle";
        private const string DASH_LastSubtitle = "HomeDash_LastSubtitle";

        private void SaveDashboardResumeCard_Music(MusicTrack track)
        {
            try
            {
                if (track == null) return;
                if (string.IsNullOrWhiteSpace(track.FilePath)) return;

                ApplicationData.Current.LocalSettings.Values[DASH_LastKind] = "music";
                ApplicationData.Current.LocalSettings.Values[DASH_LastPath] = track.FilePath;
                ApplicationData.Current.LocalSettings.Values[DASH_LastTitle] = track.Title ?? "";
                ApplicationData.Current.LocalSettings.Values[DASH_LastSubtitle] = track.Artist ?? "";
            }
            catch { }
        }

        private void ForceSaveResumePositionNow_Music()
        {
            try
            {
                if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
                var track = _queue[_currentIndex];
                if (track == null || string.IsNullOrWhiteSpace(track.FilePath)) return;

                var session = _mediaPlayer?.PlaybackSession;
                if (session == null) return;

                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                var pos = session.Position.TotalSeconds;
                if (pos < 1) return;

                if ((dur.TotalSeconds - pos) < 2.0)
                    return;

                SavePositionSeconds(track.FilePath, pos);
            }
            catch { }
        }

        public MusicPlayerPage()
        {
            double currentVolume = _mediaPlayer.Volume;
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            Player.SetMediaPlayer(_mediaPlayer);

            _mediaPlayer.MediaFailed += (s, args) =>
                Debug.WriteLine($"MediaFailed: {args.Error} � {args.ErrorMessage}");

            _mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;

            VolumeSlider.Value = currentVolume * 100;
            _mediaPlayer.Volume = currentVolume;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += Timer_Tick;

            QueueList.ItemsSource = _queue;
            SelectedFilesList.ItemsSource = _selectedTracks;

            LibraryData.QueueOverrideRequested += (_, songs) =>
            {
                DispatcherQueue.TryEnqueue(() => OverrideQueueFromLibrary(songs));
            };
            LibraryData.SongRenamed += (previousPath, newPath) =>
            {
                foreach (var track in _queue.Concat(_selectedTracks))
                    if (string.Equals(track.FilePath, previousPath, StringComparison.OrdinalIgnoreCase))
                        track.FilePath = newPath;
                if (_artworkByPath.TryGetValue(previousPath, out var artwork))
                {
                    _artworkByPath.Remove(previousPath);
                    _artworkByPath[newPath] = artwork;
                }
                if (string.Equals(_currentlyPlayingPath, previousPath, StringComparison.OrdinalIgnoreCase))
                    _currentlyPlayingPath = newPath;
            };
            LibraryData.SongUpdated += song =>
            {
                foreach (var track in _queue.Concat(_selectedTracks))
                    if (string.Equals(track.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        track.Title = song.Title;
                        track.Artist = song.Artist;
                    }
                if (song.Artwork != null)
                    _artworkByPath[song.FilePath] = song.Artwork;
                var current = _queue.ElementAtOrDefault(_currentIndex);
                if (current != null && string.Equals(current.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    TrackTitle.Text = current.Title;
                    TrackArtist.Text = current.Artist;
                    if (song.Artwork != null)
                        AlbumArt.Source = song.Artwork;
                    PushNowPlayingToService(current,
                        _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                }
            };

            try
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("Zink/1.0 (+https://zink.app)");
                _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }
            catch { }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            bool backgroundEnabled = ApplicationData.Current.LocalSettings.Values
                .TryGetValue(BACKGROUND_PLAY_KEY, out object val)
                && val is bool enabled && enabled;

            bool wasPlaying = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

            if (_savedState != null && _savedState.Queue?.Any() == true)
            {
                _queue = new ObservableCollection<MusicTrack>(_savedState.Queue);
                QueueList.ItemsSource = _queue;
                VolumeSlider.Value = _savedState.Volume * 100;
                _mediaPlayer.Volume = _savedState.Volume;

                if (_savedState.CurrentIndex >= 0 && _savedState.CurrentIndex < _queue.Count)
                {
                    _currentIndex = _savedState.CurrentIndex;
                    _lyricsPlaybackPath = _queue[_currentIndex].FilePath;
                    TrackTitle.Text = _queue[_currentIndex].Title;
                    TrackArtist.Text = _queue[_currentIndex].Artist;
                    _ = LoadLocalLyricsAsync(_queue[_currentIndex]);
                    PlayPauseButton.Content = new SymbolIcon(Symbol.Pause);

                    if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                        _mediaPlayer.PlaybackSession.Position = _savedState.Position;

                    await ReloadAlbumArtAsync(_queue[_currentIndex]);

                    PushNowPlayingToService(_queue[_currentIndex], isPlaying: wasPlaying);

                    SaveDashboardResumeCard_Music(_queue[_currentIndex]);
                }

                _timer.Start();
                _savedState = null;
                return;
            }

            if (backgroundEnabled && wasPlaying && _queue.Any())
            {
                var track = _queue.ElementAtOrDefault(_currentIndex);
                if (track != null)
                {
                    TrackTitle.Text = track.Title;
                    TrackArtist.Text = track.Artist;
                    _ = LoadLocalLyricsAsync(track);
                    PlayPauseButton.Content = new SymbolIcon(Symbol.Pause);

                    if (AlbumArt.Source is null or BitmapImage { UriSource: null })
                        await ReloadAlbumArtAsync(track);

                    QueueList.ItemsSource = _queue;
                    _timer.Start();

                    PushNowPlayingToService(track, isPlaying: true);

                    SaveDashboardResumeCard_Music(track);
                }
                return;
            }

            if (e.Parameter is string param && param == "autoplay")
            {
                var songs = LibraryData.CurrentQueue;
                if (songs?.Any() == true)
                {
                    var tracks = songs.Select(CreateLibraryTrack).ToList();

                    LoadQueue(new ObservableCollection<MusicTrack>(tracks));
                    return;
                }
            }

            if (e.Parameter is StorageFile storageFile)
            {
                var title = string.IsNullOrWhiteSpace(storageFile.DisplayName)
                    ? System.IO.Path.GetFileNameWithoutExtension(storageFile.Name)
                    : storageFile.DisplayName;

                var one = new ObservableCollection<MusicTrack>
                {
                    new MusicTrack(title, "", storageFile.Path, "ms-appx:///Assets/DefaultAlbumArt.jpg")
                };

                LoadQueue(one);
                return;
            }

            if (e.Parameter is string maybePath &&
                maybePath != "autoplay" &&
                !string.IsNullOrWhiteSpace(maybePath) &&
                System.IO.File.Exists(maybePath))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(maybePath);

                var one = new ObservableCollection<MusicTrack>
                {
                    new MusicTrack(name, "", maybePath, "ms-appx:///Assets/DefaultAlbumArt.jpg")
                };

                LoadQueue(one);
                return;
            }

            if (!_queue.Any())
            {
                await PromptLoadMusicDialogAsync();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            bool playInBackground = BackgroundPlaybackToggle?.IsChecked == true;

            try
            {
                var track = _queue.ElementAtOrDefault(_currentIndex);
                if (track != null)
                    SaveDashboardResumeCard_Music(track);
            }
            catch { }

            ForceSaveResumePositionNow_Music();

            if (playInBackground)
            {
                _savedState = new MusicPlayerState
                {
                    Queue = _queue.ToList(),
                    CurrentIndex = _currentIndex,
                    Position = _mediaPlayer.PlaybackSession.Position,
                    Volume = _mediaPlayer.Volume,
                    PlayInBackground = true
                };
                ApplicationData.Current.LocalSettings.Values[BACKGROUND_PLAY_KEY] = true;
            }
            else
            {
                _timer?.Stop();
                _mediaPlayer.Pause();
                _currentlyPlayingPath = null;
                PlayPauseButton.Content = new SymbolIcon(Symbol.Play);
                _savedState = null;
                ApplicationData.Current.LocalSettings.Values[BACKGROUND_PLAY_KEY] = false;

                try { AppPlaybackService.Instance.ClearIfKind(AppPlaybackService.MediaKind.Music); } catch { }
            }

            base.OnNavigatedFrom(e);
        }

        private void LoadQueue(ObservableCollection<MusicTrack> tracks)
        {
            _queue = tracks;
            QueueList.ItemsSource = _queue;
            if (_queue.Count > 0)
                PlayTrack(0);
        }

        private async void PlayTrack(int index, bool forceReloadArt = false)
        {
            if (index < 0 || index >= _queue.Count)
                return;

            _currentIndex = index;

            try
            {
                var track = _queue[index];
                _ = LoadLocalLyricsAsync(track);
                if (string.IsNullOrWhiteSpace(track.FilePath))
                    return;

                var file = await StorageFile.GetFileFromPathAsync(track.FilePath);
                if (file == null)
                    return;

                var isPlaying = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                if (_currentlyPlayingPath == file.Path && isPlaying && !forceReloadArt)
                {
                    _lyricsPlaybackPath = file.Path;
                    PlayPauseButton.Content = new SymbolIcon(Symbol.Pause);
                    _timer.Start();

                    PushNowPlayingToService(track, isPlaying: true);

                    SaveDashboardResumeCard_Music(track);

                    return;
                }

                _mediaPlayer.Source = MediaSource.CreateFromStorageFile(file);
                _lyricsPlaybackPath = file.Path;
                _mediaPlayer.Play();
                _currentlyPlayingPath = file.Path;

                // Each newly loaded song starts fresh, including after an app restart.
                _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;

                TrackTitle.Text = track.Title;
                TrackArtist.Text = track.Artist;
                PlayPauseButton.Content = new SymbolIcon(Symbol.Pause);

                await ReloadAlbumArtAsync(track);

                _timer.Start();

                ShowSongToast(track);

                try
                {
                    ActivityHub.Record(
                        ActivityHub.ActivityKind.Music,
                        title: track.Title ?? "",
                        subtitle: track.Artist ?? "",
                        payload: track.FilePath ?? "",
                        listenedSeconds: 0
                    );
                }
                catch { }

                PushNowPlayingToService(track, isPlaying: true);

                SaveDashboardResumeCard_Music(track);

                _ = TryFetchAndApplyArtworkAsync(track);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PlayTrack crash: " + ex.Message);
            }
        }

        private async Task LoadLocalLyricsAsync(MusicTrack track)
        {
            _lyricsCancellation?.Cancel();
            _lyricsCancellation?.Dispose();
            _lyricsCancellation = new System.Threading.CancellationTokenSource();
            var cancellationToken = _lyricsCancellation.Token;
            int requestId = ++_lyricsRequestId;
            _loadedLyricsPath = track.FilePath;
            _timedLyrics = Array.Empty<ZinkLyrics.LyricLine>();
            _activeLyricIndex = -1;
            _activeWordIndex = -1;
            LyricsLines.Children.Clear();
            LyricsLines.Visibility = Visibility.Collapsed;
            LyricsText.Visibility = Visibility.Visible;
            LyricsText.Text = "";
            LyricsStatus.Text = "Loading lyrics...";
            LyricsScrollViewer.ChangeView(null, 0, null);
            try
            {
                var result = await _lyricsReader.ReadAsync(track.FilePath, track.Title, track.Artist, cancellationToken);
                // Ignore a read that finishes after the user switches songs.
                if (requestId != _lyricsRequestId) return;
                RenderLyrics(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("Local lyrics: " + ex.Message);
                if (requestId != _lyricsRequestId) return;
                LyricsStatus.Text = "No readable local lyrics found. Add a matching .lrc or .txt file beside this song.";
            }
        }

        private void RenderLyrics(ZinkLyrics.LyricsResult result)
        {
            double duration = string.Equals(_loadedLyricsPath, _lyricsPlaybackPath, StringComparison.OrdinalIgnoreCase)
                ? _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds : 0;
            _timedLyrics = ZinkLyrics.TimedLyrics.CreateTimeline(result, duration);
            _activeLyricIndex = -1;
            _activeWordIndex = -1;
            LyricsLines.Children.Clear();
            LyricsText.Text = result.Text;
            LyricsText.Visibility = Visibility.Visible;
            LyricsLines.Visibility = Visibility.Collapsed;
            LyricsStatus.Text = result.Source;
            if (_timedLyrics.Count > 0)
            {
                LyricsText.Visibility = Visibility.Collapsed;
                LyricsLines.Visibility = Visibility.Visible;
                foreach (var line in _timedLyrics)
                {
                    LyricsLines.Children.Add(new Border
                    {
                        Padding = new Thickness(10, 8, 10, 8),
                        CornerRadius = new CornerRadius(8),
                        Child = new TextBlock
                        {
                            Text = line.Text,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 15,
                            MinHeight = 20,
                            IsTextSelectionEnabled = true,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources["SubtleTextBrush"]
                        }
                    });
                }
                LyricsStatus.Text += _timedLyrics.Any(line => line.Words.Count > 0)
                    ? " - timed word highlighting" : " - timed lines; word timings unavailable";
                UpdateCurrentLyric(forceScroll: true);
            }
            else if (result.HasLyrics)
            {
                LyricsStatus.Text += " - plain lyrics; vocal timings unavailable";
            }
        }

        private void LyricsLines_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateCurrentLyric(forceScroll: true);
        }

        private void UpdateCurrentLyric(bool forceScroll = false)
        {
            if (_timedLyrics.Count == 0 || !string.Equals(_loadedLyricsPath, _lyricsPlaybackPath,
                StringComparison.OrdinalIgnoreCase)) return;
            double position = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
            int current = ZinkLyrics.TimedLyrics.FindCurrentLine(_timedLyrics, position);
            bool lineChanged = current != _activeLyricIndex;
            if (lineChanged || forceScroll)
            {
                if (_activeLyricIndex >= 0 && _activeLyricIndex < LyricsLines.Children.Count)
                    SetLyricHighlight(_activeLyricIndex, false);
                _activeLyricIndex = current;
                _activeWordIndex = -1;
                if (current < 0)
                {
                    if (position < _timedLyrics[0].Seconds) LyricsScrollViewer.ChangeView(null, 0, null);
                    return;
                }
                SetLyricHighlight(current, true);
            }
            if (current < 0) return;
            var row = (Border)LyricsLines.Children[current];
            var text = (TextBlock)row.Child;
            int wordIndex = ZinkLyrics.TimedLyrics.FindCurrentWord(_timedLyrics[current], position,
                current + 1 < _timedLyrics.Count ? _timedLyrics[current + 1].Seconds : null);
            if (wordIndex != _activeWordIndex)
            {
                text.TextHighlighters.Clear();
                _activeWordIndex = wordIndex;
                if (wordIndex >= 0)
                {
                    var word = _timedLyrics[current].Words[wordIndex];
                    var highlight = new Microsoft.UI.Xaml.Documents.TextHighlighter
                    {
                        Background = (Microsoft.UI.Xaml.Media.Brush)Resources["AccentBrush"],
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 4, 12, 18))
                    };
                    highlight.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange
                        { StartIndex = word.StartIndex, Length = word.Length });
                    text.TextHighlighters.Add(highlight);
                }
            }
            if (!lineChanged && !forceScroll) return;
            if (row.ActualHeight <= 0)
            {
                // Retry once layout has measured the new lyric rows.
                _activeLyricIndex = -1;
                return;
            }
            double top = row.TransformToVisual(LyricsLines).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;
            double offset = Math.Max(0, top - (LyricsScrollViewer.ViewportHeight - row.ActualHeight) / 2);
            LyricsScrollViewer.ChangeView(null, Math.Min(offset, LyricsScrollViewer.ScrollableHeight), null);
        }

        private void SetLyricHighlight(int index, bool active)
        {
            var row = (Border)LyricsLines.Children[index];
            var text = (TextBlock)row.Child;
            text.TextHighlighters.Clear();
            bool timedWords = _timedLyrics[index].Words.Count > 0;
            text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources[
                active ? timedWords ? "TextBrush" : "AccentBrush" : "SubtleTextBrush"];
            text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            row.Background = active ? (Microsoft.UI.Xaml.Media.Brush)Resources["ControlSurfaceBrush"] : null;
        }

        private void PushNowPlayingToService(MusicTrack track, bool isPlaying)
        {
            try
            {
                Uri? art = null;
                if (AlbumArt?.Source is BitmapImage bi && bi.UriSource != null)
                    art = bi.UriSource;

                AppPlaybackService.Instance.SetGenericNowPlaying(
                    AppPlaybackService.MediaKind.Music,
                    primary: track?.Title ?? "",
                    secondary: track?.Artist ?? "",
                    artworkUri: art,
                    isPlaying: isPlaying
                );

                if (track != null)
                {
                    DiscordPresenceService.Instance.SetMusicPresence(
                        songTitle: track.Title,
                        artistName: track.Artist,
                        sourceName: "Zink Music",
                        isPlaying: isPlaying);
                }
            }
            catch { }
        }

        private async Task TryFetchAndApplyArtworkAsync(MusicTrack track)
        {
            try
            {
                if (track == null)
                    return;

                var artist = track?.Artist?.Trim() ?? "";
                var title = track?.Title?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                    return;

                var term = HttpUtility.UrlEncode($"{artist} {title}");
                var url = $"https://itunes.apple.com/search?media=music&limit=1&term={term}";

                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                const string key = "\"artworkUrl100\":\"";
                var start = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return;
                start += key.Length;
                var end = json.IndexOf('"', start);
                if (end <= start) return;

                var art100 = json.Substring(start, end - start).Replace("\\/", "/");
                var high = art100.Replace("100x100", "600x600");

                if (!Uri.TryCreate(high, UriKind.Absolute, out var artUri) &&
                    !Uri.TryCreate(art100, UriKind.Absolute, out artUri))
                    return;

                await DispatcherQueue.EnqueueAsync(() =>
                {
                    try
                    {
                        var artwork = new BitmapImage(artUri);
                        _artworkByPath[track.FilePath] = artwork;
                        if (!IsCurrentArtworkTrack(track))
                            return;
                        AlbumArt.Source = artwork;
                        PushNowPlayingToService(track,
                            _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                    }
                    catch { }
                });

            }
            catch { }
        }

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            var track = _queue.ElementAtOrDefault(_currentIndex);
            if (track == null)
                return;

            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await ReloadAlbumArtAsync(track);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("MediaOpened art reload error: " + ex.Message);
                }
            });
        }

        private async Task ReloadAlbumArtAsync(MusicTrack track)
        {
            if (!IsCurrentArtworkTrack(track))
                return;
            if (_artworkByPath.TryGetValue(track.FilePath, out var cachedArtwork))
            {
                AlbumArt.Source = cachedArtwork;
                return;
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(track.FilePath);
                var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView);
                if (thumb != null && thumb.Type == ThumbnailType.Image)
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    // An online lookup may have finished while the thumbnail loaded.
                    if (!_artworkByPath.ContainsKey(track.FilePath))
                        _artworkByPath[track.FilePath] = bmp;
                    if (IsCurrentArtworkTrack(track))
                        AlbumArt.Source = _artworkByPath[track.FilePath];
                }
                else
                {
                    ApplyArtworkFallback(track);
                }
            }
            catch
            {
                ApplyArtworkFallback(track);
            }
        }

        private bool IsCurrentArtworkTrack(MusicTrack track) => track != null &&
            string.Equals(_queue.ElementAtOrDefault(_currentIndex)?.FilePath,
                track.FilePath, StringComparison.OrdinalIgnoreCase);

        private void ApplyArtworkFallback(MusicTrack track)
        {
            if (IsCurrentArtworkTrack(track))
                AlbumArt.Source = _artworkByPath.TryGetValue(track.FilePath, out var artwork)
                    ? artwork : new BitmapImage(new Uri("ms-appx:///Assets/DefaultAlbumArt.jpg"));
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    _mediaPlayer.Pause();
                    PlayPauseButton.Content = new SymbolIcon(Symbol.Play);

                    try
                    {
                        var track = _queue.ElementAtOrDefault(_currentIndex);
                        if (track != null)
                            PushNowPlayingToService(track, isPlaying: false);

                        if (track != null)
                            SaveDashboardResumeCard_Music(track);
                    }
                    catch { }
                }
                else
                {
                    _mediaPlayer.Play();
                    PlayPauseButton.Content = new SymbolIcon(Symbol.Pause);

                    try
                    {
                        var track = _queue.ElementAtOrDefault(_currentIndex);
                        if (track != null)
                            PushNowPlayingToService(track, isPlaying: true);

                        if (track != null)
                            SaveDashboardResumeCard_Music(track);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PlayPause_Click crash: " + ex.Message);
            }
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            if (_queue.Count == 0) return;

            int newIndex = _currentIndex - 1;
            if (newIndex < 0 && _isRepeat)
                newIndex = _queue.Count - 1;

            if (newIndex >= 0)
                PlayTrack(newIndex);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_queue.Count == 0) return;

            int newIndex = _isShuffle
                ? _random.Next(_queue.Count)
                : _currentIndex + 1;

            if (newIndex >= _queue.Count)
                newIndex = _isRepeat ? 0 : -1;

            if (newIndex != -1)
                PlayTrack(newIndex);
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e) =>
            _isShuffle = ShuffleButton.IsChecked == true;

        private void RepeatCurrentButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool repeatCurrent = RepeatCurrentButton.IsChecked == true;
                _mediaPlayer.IsLoopingEnabled = repeatCurrent;
                ToolTipService.SetToolTip(RepeatCurrentButton,
                    repeatCurrent ? "Repeat current song: on" : "Repeat current song: off");

                // Restart a paused or finished song; an already playing song keeps its position.
                if (repeatCurrent &&
                    _mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing &&
                    _currentIndex >= 0 && _currentIndex < _queue.Count)
                {
                    PlayTrack(_currentIndex, forceReloadArt: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("RepeatCurrentButton_Click error: " + ex.Message);
            }
        }

        private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session != null && Math.Abs(session.Position.TotalSeconds - e.NewValue) > 1)
                {
                    session.Position = TimeSpan.FromSeconds(e.NewValue);
                }
            }
            catch { }
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _mediaPlayer.Volume = e.NewValue / 100.0;
            int pct = (int)Math.Round(e.NewValue);
            if (VolumePercent != null)
                VolumePercent.Text = $"{pct}%";
            if (VolumeIcon != null)
                VolumeIcon.Symbol = pct == 0 ? Symbol.Mute : Symbol.Volume;
        }

        private void Timer_Tick(object sender, object e)
        {
            try
            {
                var session = _mediaPlayer.PlaybackSession;
                UpdateCurrentLyric();
                if (session.NaturalDuration.TotalSeconds > 0)
                {
                    SeekSlider.Maximum = session.NaturalDuration.TotalSeconds;
                    SeekSlider.Value = session.Position.TotalSeconds;

                    double pct = session.Position.TotalSeconds / session.NaturalDuration.TotalSeconds * 100;
                    if (Math.Abs(pct - _lastProgressPct) > 0.25)
                    {
                        SongProgressBar.Value = pct;
                        _lastProgressPct = pct;
                    }

                    TimeDisplay.Text = $"{session.Position:mm\\:ss} / {session.NaturalDuration:mm\\:ss}";

                    MaybeSaveResumePosition(session);
                }
            }
            catch { }
        }

        private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (QueueList.SelectedIndex != -1)
                PlayTrack(QueueList.SelectedIndex);
        }

        private async Task<IReadOnlyList<StorageFile>> PickMusicFilesAsync()
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".flac");

            var files = await picker.PickMultipleFilesAsync();
            return files;
        }

        private async void BrowseFiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var files = await PickMusicFilesAsync();
                if (files == null || files.Count == 0) return;

                _selectedTracks.Clear();
                foreach (var file in files)
                {
                    var props = await file.Properties.GetMusicPropertiesAsync();
                    var title = string.IsNullOrWhiteSpace(props.Title) ? file.Name : props.Title;

                    _selectedTracks.Add(new MusicTrack(
                        title,
                        props.Artist,
                        file.Path ?? string.Empty,
                        "ms-appx:///Assets/DefaultAlbumArt.jpg"));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("BrowseFiles_Click crash: " + ex.Message);
            }
        }

        private void SendToPlayer_Click(object sender, RoutedEventArgs e)
        {
            LoadQueue(new ObservableCollection<MusicTrack>(_selectedTracks));
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            Debug.WriteLine($"Playback state changed to {sender.PlaybackState}");
        }

        public void OverrideQueueFromLibrary(IEnumerable<MusicLibraryPage.SongInfo> songs)
        {
            var tracks = songs.Select(CreateLibraryTrack).ToList();

            LoadQueue(new ObservableCollection<MusicTrack>(tracks));
        }

        private MusicTrack CreateLibraryTrack(MusicLibraryPage.SongInfo song)
        {
            if (song.Artwork != null)
                _artworkByPath[song.FilePath] = song.Artwork;
            return new MusicTrack(song.Title, song.Artist, song.FilePath,
                "ms-appx:///Assets/DefaultAlbumArt.jpg");
        }

        private class MusicPlayerState
        {
            public List<MusicTrack> Queue { get; set; }
            public int CurrentIndex { get; set; }
            public TimeSpan Position { get; set; }
            public double Volume { get; set; }
            public bool PlayInBackground { get; set; }
        }

        private void ShowSongToast(MusicTrack track)
        {
            if (track == null) return;

            try
            {
                NotificationService.Instance.Show(track.Title, track.Artist, store: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Toast error: " + ex.Message);
            }
        }

        private async Task PromptLoadMusicDialogAsync()
        {
            var dialog = new ContentDialog
            {
                Title = "Load music",
                Content = "Load a song or songs now from the file picker?",
                PrimaryButtonText = "Browse files",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary
            };

            if (Content is FrameworkElement root && root.XamlRoot != null)
            {
                dialog.XamlRoot = root.XamlRoot;
            }
            else if (App.MainWindow.Content is FrameworkElement winRoot && winRoot.XamlRoot != null)
            {
                dialog.XamlRoot = winRoot.XamlRoot;
            }

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var files = await PickMusicFilesAsync();
            if (files == null || files.Count == 0)
                return;

            var tracks = new ObservableCollection<MusicTrack>();
            foreach (var file in files)
            {
                try
                {
                    var props = await file.Properties.GetMusicPropertiesAsync();
                    var title = string.IsNullOrWhiteSpace(props.Title) ? file.Name : props.Title;

                    tracks.Add(new MusicTrack(
                        title,
                        props.Artist,
                        file.Path ?? string.Empty,
                        "ms-appx:///Assets/DefaultAlbumArt.jpg"));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("PromptLoadMusicDialogAsync file error: " + ex.Message);
                }
            }

            if (tracks.Any())
            {
                LoadQueue(tracks);
            }
        }

        private static string MakeKey(string path)
        {
            try
            {
                using var sha1 = SHA1.Create();
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(path ?? ""));
                return MUSIC_POS_PREFIX + Convert.ToHexString(bytes);
            }
            catch
            {
                return MUSIC_POS_PREFIX + (path ?? "").GetHashCode().ToString();
            }
        }

        private static double GetSavedPositionSeconds(string path)
        {
            try
            {
                var key = MakeKey(path);
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object val))
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
                ApplicationData.Current.LocalSettings.Values[key] = seconds;
            }
            catch { }
        }

        private void MaybeSaveResumePosition(MediaPlaybackSession session)
        {
            try
            {
                if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
                var track = _queue[_currentIndex];
                if (track == null || string.IsNullOrWhiteSpace(track.FilePath)) return;

                if (session.PlaybackState != MediaPlaybackState.Playing &&
                    session.PlaybackState != MediaPlaybackState.Paused)
                    return;

                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                var pos = session.Position.TotalSeconds;
                if (pos < 1) return;

                if (dur.TotalSeconds > 0 && (dur.TotalSeconds - pos) < 2.0)
                    return;

                var now = DateTime.UtcNow;
                if ((now - _lastPosSaveUtc).TotalSeconds < 1.5) return;
                if (_lastSavedPosSeconds >= 0 && Math.Abs(pos - _lastSavedPosSeconds) < 1.0) return;

                _lastPosSaveUtc = now;
                _lastSavedPosSeconds = pos;

                SavePositionSeconds(track.FilePath, pos);
            }
            catch { }
        }
    }
}

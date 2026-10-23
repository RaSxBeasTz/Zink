using Microsoft.Identity.Client;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Zink.Services;

namespace Zink.Pages
{
    public sealed class OneDrivePlayerSurface : Grid
    {
        private readonly Microsoft.UI.Input.InputSystemCursor _visibleCursor =
            Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);

        public void SetCursorHidden(bool hidden) => ProtectedCursor = hidden ? null : _visibleCursor;
    }

    public sealed partial class OneDriveStreamingPage : Page
    {
        private bool _fullscreen;
        private bool _leaving;
        private bool _updatingControls;
        private bool _seeking;
        private bool _fullscreenTransition;
        private bool _wasPlaying;
        private bool _pointerOverControls;
        private bool _controlsHaveFocus;
        private DateTime _controlsVisibleUntil = DateTime.UtcNow.AddSeconds(1);
        private GridLength _sidebarWidthBeforeFullScreen;
        private bool _sidebarWidthSavedForFullScreen;
        private UIElement? _keyboardEventRoot;
        private AppWindow? _window;
        private MediaPlayer? _player;
        private MediaSource? _source;
        private CancellationTokenSource? _operation;
        private CancellationTokenSource? _signIn;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        public OneDriveStreamingPage()
        {
            InitializeComponent();
            _timer.Tick += (_, _) => UpdateControls();
            SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _seeking = true), true);
            SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _seeking = false), true);
            SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => _seeking = false), true);
        }

        internal static bool TryParseLink(string text, out Uri? uri)
        {
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            var host = uri.Host;
            return host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase) || host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("onedrive.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase);
        }

        private async void OpenVideo_Click(object sender, RoutedEventArgs e) => await OpenVideoAsync();
        private async void LinkBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            await OpenVideoAsync();
        }

        private async Task OpenVideoAsync()
        {
            if (!OpenButton.IsEnabled) return;
            if (!TryParseLink(LinkBox.Text, out var uri))
            {
                StatusText.Text = "Enter an HTTPS OneDrive video sharing link.";
                return;
            }
            StopNativePlayback();
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            _operation = cancellation;
            OpenButton.IsEnabled = false;
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingRing.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            PlayerMessage.Text = "Opening your original video…";
            StatusText.Text = "Resolving the original video…";
            VideoTitle.Text = "Zink Player";
            ResolutionText.Text = "Original file";
            var stage = "sign-in";
            try
            {
                StatusText.Text = "Sign in to Microsoft in your browser if prompted…";
                using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                authenticationTimeout.CancelAfter(TimeSpan.FromMinutes(5));
                var accessToken = await OneDriveAuthenticationService.GetAccessTokenAsync(false, authenticationTimeout.Token);
                StatusText.Text = "Resolving the original video through Microsoft Graph…";
                stage = "file-resolution";
                var video = await OneDriveLinkResolver.ResolveAsync(uri!, accessToken, token);
                token.ThrowIfCancellationRequested();
                if (video == null)
                {
                    PlayerMessage.Text = "The link did not resolve to a single original video file. Check the sharing link and try again.";
                    StatusText.Text = "No original-file URL was available. Check that this is a single-video sharing link and that original-file access is allowed.";
                    return;
                }
                if (_leaving) return;
                stage = "media-source";
                // Graph supplies a preauthorized original-file URL. Let Windows handle HTTP,
                // buffering, seeking and content probing instead of adapting a .NET range stream.
                // The Graph bearer token is never attached to the file request.
                _source = MediaSource.CreateFromUri(video.OriginalUri);
                stage = "player-initialization";
                _player = new MediaPlayer { AutoPlay = false, Volume = VolumeSlider.Value };
                if (SpeedBox.SelectedItem is ComboBoxItem selectedSpeed && double.TryParse(selectedSpeed.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                    _player.PlaybackSession.PlaybackRate = rate;
                _player.MediaOpened += Player_MediaOpened;
                _player.MediaFailed += Player_MediaFailed;
                NativePlayer.SetMediaPlayer(_player);
                stage = "source-attachment";
                _player.Source = _source;
                VideoTitle.Text = video.Name;
                PlayerMessage.Text = "Buffering original video…";
                StatusText.Text = "Streaming original file in Zink Player";
                _timer.Start();
                stage = "start-playback";
                _player.Play();
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(_operation, cancellation))
                {
                    PlayerMessage.Text = "Sign-in or playback was canceled. Press Play video to retry.";
                    StatusText.Text = "Canceled";
                }
            }
            catch (MsalException ex)
            {
                if (ReferenceEquals(_operation, cancellation))
                {
                    PlayerMessage.Text = AuthenticationError(ex);
                    StatusText.Text = "Microsoft sign-in did not complete.";
                }
            }
            catch (OneDriveResolutionException ex)
            {
                if (ReferenceEquals(_operation, cancellation))
                {
                    var code = $"{ex.Stage}: HTTP {(int)ex.StatusCode!}, {ex.GraphCode}";
                    PlayerMessage.Text = $"Microsoft could not resolve the original video ({code}).";
                    StatusText.Text = "Original-file lookup failed. Include the Microsoft error code above when reporting this problem.";
                    DiagnosticLogService.WriteLine($"[OneDrivePlayback] stage={ex.Stage}; http={(int)ex.StatusCode!}; graphCode={ex.GraphCode}");
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                if (ReferenceEquals(_operation, cancellation))
                {
                    PlayerMessage.Text = ex.StatusCode == HttpStatusCode.NotFound
                        ? "Microsoft could not find this shared video. Check that the link is still valid."
                        : "Microsoft denied access. Use Sign in to select an account allowed to access the video, and verify Zink has the Files.ReadWrite delegated permission.";
                    StatusText.Text = "Microsoft Graph could not open the original file.";
                }
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_operation, cancellation))
                {
                    StopNativePlayback();
                    var code = $"{stage}, 0x{ex.HResult:X8}";
                    PlayerMessage.Text = $"Unable to start the original video ({code}).";
                    StatusText.Text = "Playback failed. Include the error code above when reporting this problem.";
                    DiagnosticLogService.WriteLine($"[OneDrivePlayback] stage={stage}; type={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
                }
            }
            finally
            {
                if (!_leaving && (_operation == null || ReferenceEquals(_operation, cancellation)))
                {
                    OpenButton.IsEnabled = true;
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static string AuthenticationError(MsalException error) =>
            "Microsoft sign-in failed (" + error.ErrorCode + "). Check that Zink's app registration supports personal Microsoft accounts, has the Mobile and desktop redirect URI http://localhost, and has the delegated Files.ReadWrite permission.";

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            if (_signIn != null || !OpenButton.IsEnabled) return;
            var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            _signIn = cancellation;
            OpenButton.IsEnabled = false;
            StatusText.Text = "Complete Microsoft sign-in in your browser…";
            try
            {
                await OneDriveAuthenticationService.GetAccessTokenAsync(true, cancellation.Token);
                if (!_leaving) StatusText.Text = "Signed in to Microsoft. Paste a sharing link and press Play video.";
            }
            catch (OperationCanceledException) { if (!_leaving) StatusText.Text = "Microsoft sign-in canceled."; }
            catch (MsalException ex) { if (!_leaving) StatusText.Text = AuthenticationError(ex); }
            catch { if (!_leaving) StatusText.Text = "Unable to start Microsoft sign-in. Check your connection and try again."; }
            finally
            {
                if (ReferenceEquals(_signIn, cancellation)) _signIn = null;
                cancellation.Dispose();
                if (!_leaving) OpenButton.IsEnabled = true;
            }
        }

        private void Player_MediaOpened(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_leaving || !ReferenceEquals(sender, _player)) return;
            var session = sender.PlaybackSession;
            ResolutionText.Text = $"Original • {session.NaturalVideoWidth} × {session.NaturalVideoHeight}";
            LoadingPanel.Visibility = Visibility.Collapsed;
            PlayPauseButton.IsEnabled = true;
            UpdateControls();
        });
        private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_leaving || !ReferenceEquals(sender, _player)) return;
            SetFullscreen(false);
            LoadingPanel.Visibility = Visibility.Visible;
            _timer.Stop();
            PlayPauseButton.IsEnabled = false;
            SeekSlider.IsEnabled = false;
            var code = $"{args.Error}, 0x{args.ExtendedErrorCode.HResult:X8}";
            PlayerMessage.Text = $"Windows could not play the original video ({code}). Check codec support or reopen the link.";
            StatusText.Text = "Native video playback failed. Include the error code above when reporting this problem.";
            PauseButton.IsEnabled = false;
            ShowPlayerUi();
            DiagnosticLogService.WriteLine($"[OneDrivePlayback] stage=decoder; error={args.Error}; hresult=0x{args.ExtendedErrorCode.HResult:X8}");
        });
        private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
        private void UpdateControls()
        {
            if (_player == null) return;
            var session = _player.PlaybackSession;
            _updatingControls = true;
            try
            {
                TimeText.Text = FormatTime(session.Position) + " / " + FormatTime(session.NaturalDuration);
                if (session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0)
                {
                    var resolution = $"Original • {session.NaturalVideoWidth} × {session.NaturalVideoHeight}";
                    if (ResolutionText.Text != resolution) ResolutionText.Text = resolution;
                }
                SeekSlider.IsEnabled = session.CanSeek && session.NaturalDuration.TotalSeconds > 0;
                if (SeekSlider.IsEnabled && !_seeking) SeekSlider.Value = session.Position.TotalSeconds / session.NaturalDuration.TotalSeconds * 1000;
                var playing = session.PlaybackState == MediaPlaybackState.Playing;
                PauseButton.IsEnabled = playing;
                if (!playing || !_wasPlaying) ShowPlayerUi();
                _wasPlaying = playing;
                if (playing && !_pointerOverControls && !_controlsHaveFocus && !_seeking &&
                    !_fullscreenTransition && DateTime.UtcNow >= _controlsVisibleUntil)
                {
                    PlayerControls.Visibility = Visibility.Collapsed;
                    PlayerHeader.Visibility = Visibility.Collapsed;
                    PlayerShell.SetCursorHidden(true);
                }
            }
            catch (Exception ex)
            {
                _timer.Stop();
                DiagnosticLogService.WriteLine($"[OneDrivePlayback] stage=control-update; type={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
            }
            finally { _updatingControls = false; }
        }
        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_player == null) return;
            _player.Play();
            ShowPlayerUi();
        }
        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _player?.Pause();
            ShowPlayerUi();
        }
        private void ShowPlayerUi()
        {
            _controlsVisibleUntil = DateTime.UtcNow.AddSeconds(1);
            PlayerShell.SetCursorHidden(false);
            PlayerControls.Visibility = Visibility.Visible;
            PlayerHeader.Visibility = Visibility.Visible;
        }
        private void PlayerShell_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowPlayerUi();
        private void PlayerShell_PointerExited(object sender, PointerRoutedEventArgs e) => PlayerShell.SetCursorHidden(false);
        private void PlayerControls_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _pointerOverControls = true;
            ShowPlayerUi();
        }
        private void PlayerControls_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerOverControls = false;
            ShowPlayerUi();
        }
        private void PlayerControls_GotFocus(object sender, RoutedEventArgs e)
        {
            _controlsHaveFocus = e.OriginalSource is Control control && control.FocusState == FocusState.Keyboard;
            ShowPlayerUi();
        }
        private void PlayerControls_LostFocus(object sender, RoutedEventArgs e)
        {
            _controlsHaveFocus = false;
            ShowPlayerUi();
        }
        private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_updatingControls && _player?.PlaybackSession.CanSeek == true)
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(_player.PlaybackSession.NaturalDuration.TotalSeconds * e.NewValue / 1000);
        }
        private void Skip(double seconds)
        {
            var session = _player?.PlaybackSession;
            if (session?.CanSeek == true) session.Position = TimeSpan.FromSeconds(Math.Clamp(session.Position.TotalSeconds + seconds, 0, session.NaturalDuration.TotalSeconds));
        }
        private void BackTen_Click(object sender, RoutedEventArgs e) => Skip(-10);
        private void ForwardTen_Click(object sender, RoutedEventArgs e) => Skip(10);
        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (_player != null) _player.Volume = e.NewValue; }
        private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_player != null && SpeedBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                _player.PlaybackSession.PlaybackRate = speed;
        }
        private void Fullscreen_Click(object sender, RoutedEventArgs e)
        {
            SetFullscreen(!_fullscreen);
        }
        private void SetFullscreen(bool fullscreen)
        {
            if (_leaving || _fullscreenTransition || _fullscreen == fullscreen) return;
            _fullscreenTransition = true;
            try
            {
                if (App.MainWindow == null) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _window = AppWindow.GetFromWindowId(windowId);
                PlayerControls.Visibility = Visibility.Visible;
                PlayerControls.IsHitTestVisible = true;
                if (fullscreen)
                {
                    // Match the video player's one-time background overlay suppression.
                    _ = Task.Run(VideoPlayerPage.SuppressNvidiaOverlayWindows);
                    HideSidebarColumnForFullScreen();
                    _window.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                else
                {
                    _window.SetPresenter(AppWindowPresenterKind.Overlapped);
                    RestoreSidebarColumnAfterFullScreen();
                }
                UpdateFullscreenUi(fullscreen);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[OneDrivePlayback] stage=fullscreen; type={ex.GetType().Name}; hresult=0x{ex.HResult:X8}");
                UpdateFullscreenUi(_window?.Presenter.Kind == AppWindowPresenterKind.FullScreen);
                if (!_fullscreen) RestoreSidebarColumnAfterFullScreen();
            }
            finally
            {
                _fullscreenTransition = false;
            }
        }

        private void HideSidebarColumnForFullScreen()
        {
            var column = App.MainWindow?.SidebarColumnReference;
            if (column == null || _sidebarWidthSavedForFullScreen) return;
            _sidebarWidthBeforeFullScreen = column.Width;
            _sidebarWidthSavedForFullScreen = true;
            column.Width = new GridLength(0);
        }

        private void RestoreSidebarColumnAfterFullScreen()
        {
            var column = App.MainWindow?.SidebarColumnReference;
            if (column == null || !_sidebarWidthSavedForFullScreen) return;
            column.Width = _sidebarWidthBeforeFullScreen;
            _sidebarWidthSavedForFullScreen = false;
        }

        private void UpdateFullscreenUi(bool fullscreen)
        {
            _fullscreen = fullscreen;
            LinkPanel.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
            PlayerShell.Margin = _fullscreen ? new Thickness(0) : new Thickness(12);
            PlayerShell.CornerRadius = _fullscreen ? new CornerRadius(0) : new CornerRadius(18);
            FullscreenButton.Content = _fullscreen ? "Exit fullscreen (Esc)" : "Fullscreen";
            ShowPlayerUi();
        }

        private void Window_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.KeyStatus.WasKeyDown || e.Key != global::Windows.System.VirtualKey.Escape || !_fullscreen) return;
            e.Handled = true;
            Fullscreen_Click(FullscreenButton, e);
        }
        private void StopNativePlayback()
        {
            _operation?.Cancel(); _operation?.Dispose(); _operation = null;
            _timer.Stop();
            _wasPlaying = false;
            PauseButton.IsEnabled = false;
            ShowPlayerUi();
            SetFullscreen(false);
            if (_player != null)
            {
                _player.MediaOpened -= Player_MediaOpened; _player.MediaFailed -= Player_MediaFailed;
                _player.Pause(); _player.Source = null; NativePlayer.SetMediaPlayer(null); _player.Dispose(); _player = null;
            }
            _source?.Dispose(); _source = null;
            PlayPauseButton.IsEnabled = false;
            SeekSlider.IsEnabled = false;
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _leaving = false;
            if (App.MainWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                _window = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
                _keyboardEventRoot = App.MainWindow.Content as UIElement;
                if (_keyboardEventRoot != null) _keyboardEventRoot.PreviewKeyDown += Window_PreviewKeyDown;
            }
            base.OnNavigatedTo(e);
        }
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Restore synchronously before detaching; a queued transition must not affect the next page.
            if (_window?.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                try
                {
                    _window.SetPresenter(AppWindowPresenterKind.Overlapped);
                }
                catch (Exception ex) { DiagnosticLogService.WriteLine($"[OneDrivePlayback] stage=fullscreen-unload; hresult=0x{ex.HResult:X8}"); }
                RestoreSidebarColumnAfterFullScreen();
            }
            _fullscreen = false;
            if (_keyboardEventRoot != null) _keyboardEventRoot.PreviewKeyDown -= Window_PreviewKeyDown;
            _keyboardEventRoot = null;
            StopNativePlayback(); _leaving = true; _signIn?.Cancel(); base.OnNavigatedFrom(e);
        }
    }
}

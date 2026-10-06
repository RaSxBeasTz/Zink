using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Zink.Services;
using Windows.Storage.Streams; // DataWriter/InMemoryRandomAccessStream
using Microsoft.Web.WebView2.Core;

// Force global Windows namespaces to avoid Zink.Windows collisions
using WinGraphics = global::Windows.Graphics;

namespace Zink
{
    public sealed partial class SpotifyWidgetWindow : Window
    {
        private const int BOX_WIDTH = 1100;
        private const int BOX_HEIGHT = 500;

        private IntPtr _hwnd;
        private AppWindow _appWindow;
        private DispatcherTimer _topmostTimer;
        private DispatcherTimer _stateTick;
        private bool _isPinned = true;

        private bool _userDraggingSlider = false;
        private double _lastDuration = 0;
        private DateTime _trackStartUtc = DateTime.UtcNow;

        // keep last good image / prevent redundant reloads
        private string _lastArtUri = string.Empty;

        public SpotifyWidgetWindow()
        {
            InitializeComponent();
            LocalizationService.RegisterWindow(this);

            // Use the root Grid's Loaded event
            Root.Loaded += OnLoaded;

            ConfigureFramelessAndSize();

            Activated += (_, __) => MakeTopMost();
            MakeTopMost();

            ApplyExtendedStyles();
            StartTopMostKeeper();
            StartStateTick();

            SpotifyControllerService.Instance.TrackChanged += (_, info) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        TrackTitle.Text = string.IsNullOrWhiteSpace(info.Title) ? "Track" : info.Title;
                        TrackArtist.Text = string.IsNullOrWhiteSpace(info.Artist) ? "Artist" : info.Artist;
                        AlbumName.Text = string.IsNullOrWhiteSpace(info.Album) ? "Album" : info.Album;
                        SetAlbumArt(info.ImageUrl);

                        _lastDuration = info.DurationSec > 0 ? info.DurationSec : 0;

                        _trackStartUtc = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, info.PositionSec));

                        if (_lastDuration > 0)
                        {
                            ProgressSlider.Maximum = _lastDuration;
                            if (!_userDraggingSlider)
                                ProgressSlider.Value = Math.Max(0, Math.Min(info.PositionSec, _lastDuration));
                            UpdateTimeTexts(info.PositionSec, _lastDuration);
                        }
                        else
                        {
                            ProgressSlider.Maximum = 100;
                            if (!_userDraggingSlider) ProgressSlider.Value = 0;
                            ElapsedText.Text = "0:00";
                            RemainingText.Text = "-0:00";
                            TimerText.Text = "0:00 / 0:00";
                        }

                        // ? NEW: reflect liked state whenever the service reports it
                        UpdateLikeVisual(info.IsLiked);
                    }
                    catch { }
                });
            };

            SpotifyControllerService.Instance.PlayingChanged += (_, playing) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdatePlayPauseVisual(playing);
                    if (playing) _stateTick?.Start(); else _stateTick?.Stop();
                });
            };
        }

        // ? Only attach the hidden WebView2 if nothing else is already attached
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!SpotifyControllerService.Instance.IsAttached)
                {
                    await SpotifyWebView.EnsureCoreWebView2Async();
                    SpotifyControllerService.Instance.Attach(SpotifyWebView.CoreWebView2);

                    if (SpotifyWebView.Source == null)
                        SpotifyWebView.CoreWebView2.Navigate("https://open.spotify.com/");
                }

                try { await SpotifyControllerService.Instance.RefreshStateAsync(); } catch { }
            }
            catch { }
        }

        public void ShowWindow()
        {
            try { _appWindow?.Show(); Activate(); MakeTopMost(noActivate: true); } catch { }
        }
        public void HideWindow()
        {
            try { _appWindow?.Hide(); } catch { }
        }

        private static string ToMinSec(double sec)
        {
            if (sec < 0) sec = 0;
            int s = (int)Math.Round(sec);
            int m = s / 60; s = s % 60;
            return $"{m}:{s:00}";
        }

        private void UpdateTimeTexts(double pos, double dur)
        {
            ElapsedText.Text = ToMinSec(pos);
            var remain = Math.Max(0, dur - pos);
            RemainingText.Text = "-" + ToMinSec(remain);
            TimerText.Text = $"{ToMinSec(pos)} / {ToMinSec(dur)}";
        }

        // ? NEW: single place to flip the Like button visuals
        private void UpdateLikeVisual(bool liked)
        {
            LikeLabel.Text = liked ? "Liked" : "Like";
            // keep the same heart glyph; just give a subtle visual hint
            LikeButton.Opacity = liked ? 1.0 : 0.9;
        }

        private async void SetAlbumArt(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                if (string.Equals(_lastArtUri, url, StringComparison.OrdinalIgnoreCase)) return;
                if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;

                if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = url.IndexOf(',');
                    if (comma > 0)
                    {
                        string b64 = url.Substring(comma + 1);
                        byte[] bytes = Convert.FromBase64String(b64);

                        using InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();
                        using (var writer = new DataWriter(ms))
                        {
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            await writer.FlushAsync();
                            writer.DetachStream();
                        }

                        ms.Seek(0); // rewind so BitmapImage can load

                        var bmp = new BitmapImage();
                        await bmp.SetSourceAsync(ms);
                        AlbumArtImage.Source = bmp;
                        _lastArtUri = url;
                        return;
                    }
                }

                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var bmp = new BitmapImage
                    {
                        DecodePixelHeight = 120,
                        DecodePixelWidth = 120,
                        UriSource = uri
                    };
                    AlbumArtImage.Source = bmp;
                    _lastArtUri = url;
                }
            }
            catch
            {
                // keep last good image
            }
        }

        private void StartStateTick()
        {
            _stateTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _stateTick.Tick += async (_, __) =>
            {
                if (SpotifyControllerService.Instance.IsAttached && SpotifyControllerService.Instance.IsPlaying)
                {
                    double elapsed = Math.Max(0, (DateTime.UtcNow - _trackStartUtc).TotalSeconds);

                    if (_lastDuration > 0)
                    {
                        var clamped = Math.Min(elapsed, _lastDuration);
                        UpdateTimeTexts(clamped, _lastDuration);

                        if (!_userDraggingSlider)
                            ProgressSlider.Value = clamped;
                    }
                    else
                    {
                        TimerText.Text = $"{ToMinSec(elapsed)} / 0:00";
                    }

                    try { await SpotifyControllerService.Instance.RefreshStateAsync(); } catch { }
                }
            };
        }

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _userDraggingSlider = true;
        }
        private async void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_lastDuration > 0)
                {
                    var target = ProgressSlider.Value;
                    await SpotifyControllerService.Instance.SeekToAsync(target);

                    _trackStartUtc = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, target));

                    UpdateTimeTexts(target, _lastDuration);
                }
            }
            catch { }
            finally
            {
                _userDraggingSlider = false;
            }
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (SpotifyControllerService.Instance.IsAttached)
                await SpotifyControllerService.Instance.PlayPauseAsync();

            UpdatePlayPauseVisual(!SpotifyControllerService.Instance.IsPlaying);
        }
        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (SpotifyControllerService.Instance.IsAttached)
                await SpotifyControllerService.Instance.NextAsync();
        }
        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (SpotifyControllerService.Instance.IsAttached)
                await SpotifyControllerService.Instance.PreviousAsync();
        }
        private void Hide_Click(object sender, RoutedEventArgs e) => HideWindow();

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            SetPinnedState();
        }

        private async void Like_Click(object sender, RoutedEventArgs e)
        {
            if (SpotifyControllerService.Instance.IsAttached)
            {
                // ? Optimistic UI flip so the button updates instantly
                bool newLiked = !SpotifyControllerService.Instance.Current.IsLiked;
                UpdateLikeVisual(newLiked);

                await SpotifyControllerService.Instance.ToggleLikeAsync();
                await SpotifyControllerService.Instance.RefreshStateAsync();
                // TrackChanged will bring the authoritative value shortly
            }
        }

        private void UpdatePlayPauseVisual(bool isPlaying)
        {
            if (isPlaying)
            {
                PlayPauseIcon.Glyph = "\uE769";
                PlayPauseLabel.Text = "Pause";
            }
            else
            {
                PlayPauseIcon.Glyph = "\uE768";
                PlayPauseLabel.Text = "Play";
            }
        }

        private void ConfigureFramelessAndSize()
        {
            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow is not null)
            {
                if (_appWindow.Presenter is OverlappedPresenter p)
                {
                    p.IsResizable = false;
                    p.IsMaximizable = false;
                    p.IsMinimizable = false;
                    p.SetBorderAndTitleBar(false, false);
                }

                _appWindow.Resize(new WinGraphics.SizeInt32(BOX_WIDTH, BOX_HEIGHT));
                DisableNativeWindowBorder();

                var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                var x = area.WorkArea.X + (area.WorkArea.Width - BOX_WIDTH) / 2;
                var y = area.WorkArea.Y + (area.WorkArea.Height - BOX_HEIGHT) / 2;
                _appWindow.Move(new WinGraphics.PointInt32(x, y));

                try
                {
                    ExtendsContentIntoTitleBar = true;
                    SetTitleBar(DragRegion);
                    _appWindow.Changed += (_, __) =>
                    {
                        MakeTopMost(noActivate: true);
                    };
                }
                catch { }
            }
        }

        private void DisableNativeWindowBorder()
        {
            const int DwmwaBorderColor = 34;
            const uint DwmColorNone = 0xFFFFFFFE;
            var borderColor = DwmColorNone;
            DwmSetWindowAttribute(
                _hwnd,
                DwmwaBorderColor,
                ref borderColor,
                Marshal.SizeOf<uint>());
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref uint attributeValue,
            int attributeSize);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private void MakeTopMost() => MakeTopMost(noActivate: false);
        private void MakeTopMost(bool noActivate)
        {
            try
            {
                if (!_isPinned)
                    return;

                if (_hwnd == IntPtr.Zero)
                    _hwnd = WindowNative.GetWindowHandle(this);

                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
                if (noActivate) flags |= SWP_NOACTIVATE;
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
            }
            catch { }
        }

        private void SetPinnedState()
        {
            try
            {
                if (_hwnd == IntPtr.Zero)
                    _hwnd = WindowNative.GetWindowHandle(this);

                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
                SetWindowPos(_hwnd, _isPinned ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, flags);

                PinButton.Opacity = _isPinned ? 1.0 : 0.55;
                ToolTipService.SetToolTip(PinButton, _isPinned ? "Unpin widget" : "Pin above apps and games");
            }
            catch { }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        private void ApplyExtendedStyles()
        {
            try
            {
                if (_hwnd == IntPtr.Zero)
                    _hwnd = WindowNative.GetWindowHandle(this);
                var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                var newEx = new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP);
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, newEx);
                MakeTopMost(noActivate: true);
            }
            catch { }
        }
        private void StartTopMostKeeper()
        {
            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _topmostTimer.Tick += (_, __) => MakeTopMost(noActivate: true);
            _topmostTimer.Start();
        }

    }
}

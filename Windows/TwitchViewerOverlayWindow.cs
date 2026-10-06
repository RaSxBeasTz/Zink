using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using Zink.Services.Streaming;

namespace Zink.Windows
{
    public sealed class TwitchViewerOverlayWindow : Window
    {
        private const int WidgetWidthDips = 190;
        private const int WidgetHeightDips = 58;
        private const int WmNcButtonDown = 0x00A1;
        private const int HtCaption = 2;

        private static TwitchViewerOverlayWindow? _singleton;

        private readonly Border _shell;
        private readonly TextBlock _viewerText;
        private readonly TextBlock _statusText;
        private readonly CancellationTokenSource _cts = new();
        private DispatcherQueueTimer? _refreshTimer;
        private DispatcherQueueTimer? _topmostTimer;
        private IntPtr _hwnd;
        private AppWindow? _appWindow;
        private bool _refreshing;

        public TwitchViewerOverlayWindow()
        {
            Title = "Twitch Viewers";
            (_shell, _viewerText, _statusText) = BuildUi();
            Content = _shell;
            Zink.Services.LocalizationService.RegisterWindow(this);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(_shell);
            Activated += (_, _) => MakeTopMost();
            Closed += (_, _) =>
            {
                StopTimers();
                _cts.Cancel();
                _cts.Dispose();
                _singleton = null;
            };
        }

        public static void ShowSingleton()
        {
            if (_singleton == null)
            {
                _singleton = new TwitchViewerOverlayWindow();
                _singleton.ShowAtTopLeft();
            }
            else
            {
                _singleton.Activate();
                _singleton.MakeTopMost(noActivate: true);
                _ = _singleton.RefreshAsync();
            }
        }

        public static void CloseSingleton()
        {
            try { _singleton?.Close(); } catch { }
            _singleton = null;
        }

        private static (Border Shell, TextBlock ViewerText, TextBlock StatusText) BuildUi()
        {
            var viewerText = new TextBlock
            {
                Text = "Viewers --",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var statusText = new TextBlock
            {
                Text = "Twitch",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(190, 174, 239, 255)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(viewerText);
            stack.Children.Add(statusText);

            var shell = new Border
            {
                Width = WidgetWidthDips,
                Height = WidgetHeightDips,
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(232, 9, 14, 20)),
                Child = stack
            };

            return (shell, viewerText, statusText);
        }

        private void ShowAtTopLeft()
        {
            Activate();

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            ConfigureOverlayWindow();
            _appWindow.Resize(new SizeInt32(DipsToPixels(WidgetWidthDips), DipsToPixels(WidgetHeightDips)));

            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            _appWindow.Move(new PointInt32(area.WorkArea.X + 28, area.WorkArea.Y + 28));

            _shell.PointerPressed += Shell_PointerPressed;
            ApplyExtendedStyles();
            MakeTopMost();
            StartTimers();
            _ = RefreshAsync();
        }

        private void Shell_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_hwnd == IntPtr.Zero)
                _hwnd = WindowNative.GetWindowHandle(this);

            ReleaseCapture();
            SendMessage(_hwnd, WmNcButtonDown, HtCaption, 0);
        }

        private void StartTimers()
        {
            _refreshTimer = DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(15);
            _refreshTimer.Tick += async (_, _) => await RefreshAsync();
            _refreshTimer.Start();

            _topmostTimer = DispatcherQueue.CreateTimer();
            _topmostTimer.Interval = TimeSpan.FromMilliseconds(250);
            _topmostTimer.Tick += (_, _) => MakeTopMost(noActivate: true);
            _topmostTimer.Start();
        }

        private void StopTimers()
        {
            try { _refreshTimer?.Stop(); } catch { }
            try { _topmostTimer?.Stop(); } catch { }
            _refreshTimer = null;
            _topmostTimer = null;
        }

        private async Task RefreshAsync()
        {
            if (_refreshing)
                return;

            _refreshing = true;
            try
            {
                var snapshot = await TwitchViewerCountService.Instance.RefreshAsync(_cts.Token);
                DispatcherQueue.TryEnqueue(() =>
                {
                    _viewerText.Text = snapshot.ViewerCount.HasValue
                        ? $"Viewers {snapshot.ViewerCount.Value:0}"
                        : "Viewers --";
                    _statusText.Text = snapshot.Status;
                });
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void ConfigureOverlayWindow()
        {
            try
            {
                if (_appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    presenter.SetBorderAndTitleBar(false, false);
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsAlwaysOnTop = true;
                }
            }
            catch { }
        }

        private void MakeTopMost() => MakeTopMost(noActivate: false);

        private void MakeTopMost(bool noActivate)
        {
            try
            {
                if (_hwnd == IntPtr.Zero)
                    _hwnd = WindowNative.GetWindowHandle(this);

                uint flags = SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoOwnerZOrder | SwpNoSendChanging;
                if (noActivate)
                    flags |= SwpNoActivate;

                SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, flags);
            }
            catch { }
        }

        private void ApplyExtendedStyles()
        {
            try
            {
                if (_hwnd == IntPtr.Zero)
                    _hwnd = WindowNative.GetWindowHandle(this);

                var ex = GetWindowLongPtr(_hwnd, GwlExStyle);
                var newEx = new IntPtr(ex.ToInt64() | WsExToolWindow | WsExNoRedirectionBitmap);
                SetWindowLongPtr(_hwnd, GwlExStyle, newEx);
                MakeTopMost(noActivate: true);
            }
            catch { }
        }

        private int DipsToPixels(int dips)
        {
            try
            {
                if (_hwnd == IntPtr.Zero)
                    _hwnd = WindowNative.GetWindowHandle(this);

                return Math.Max(1, (int)Math.Round(dips * GetDpiForWindow(_hwnd) / 96.0));
            }
            catch
            {
                return dips;
            }
        }

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoRedirectionBitmap = 0x00200000L;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpNoSendChanging = 0x0400;
    }
}

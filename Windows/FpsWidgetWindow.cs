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
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using Zink.Services.Gaming;

namespace Zink.Windows
{
    public sealed class FpsWidgetWindow : Window
    {
        private const int WidgetWidth = 300;
        private const int WidgetHeight = 74;
        private const int WmNcButtonDown = 0x00A1;
        private const int HtCaption = 2;

        private static FpsWidgetWindow? _singleton;

        private readonly TextBlock _fpsText = new()
        {
            Text = "FPS --",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.NoWrap
        };

        private readonly TextBlock _detailText = new()
        {
            Text = "CPU --%",
            FontSize = 16,
            Opacity = 0.78
        };

        private TextBlock? _titleText;
        private bool _textOnly;
        private readonly Border _shell;
        private IntPtr _hwnd;
        private AppWindow? _appWindow;
        private DispatcherQueueTimer? _topmostTimer;

        public static FpsWidgetWindow? Current => _singleton;

        public FpsWidgetWindow()
        {
            Title = "Zink FPS Widget";
            _shell = BuildUi();
            Content = _shell;
            Zink.Services.LocalizationService.RegisterWindow(this);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(_shell);
            Activated += (_, _) => MakeTopMost();

            FpsMonitorService.Instance.SnapshotChanged += Monitor_SnapshotChanged;
            Closed += (_, _) =>
            {
                StopTopMostKeeper();
                FpsMonitorService.Instance.SnapshotChanged -= Monitor_SnapshotChanged;
                _singleton = null;
            };
        }

        public static void ShowSingleton(int opacity, bool textOnly = false)
        {
            if (_singleton == null)
            {
                _singleton = new FpsWidgetWindow();
                _singleton.SetOpacity(opacity);
                _singleton.SetTextOnly(textOnly);
                _singleton.ShowAtTopRight();
            }
            else
            {
                _singleton.SetOpacity(opacity);
                _singleton.SetTextOnly(textOnly);
                _singleton.Activate();
                _singleton.MakeTopMost(noActivate: true);
            }
        }

        public static void CloseSingleton()
        {
            try { _singleton?.Close(); } catch { }
            _singleton = null;
        }

        public void SetOpacity(int opacity)
        {
            var alpha = (byte)Math.Clamp((int)(255 * Math.Clamp(opacity, 25, 100) / 100.0), 64, 255);
            _shell.Background = new SolidColorBrush(Color.FromArgb(alpha, 9, 14, 20));
        }

        public void SetTextOnly(bool textOnly)
        {
            _textOnly = textOnly;
            if (_titleText != null)
                _titleText.Visibility = Visibility.Visible;

            _detailText.Visibility = Visibility.Visible;
        }

        private Border BuildUi()
        {
            var root = new Border
            {
                Padding = new Thickness(14, 8, 14, 8),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(232, 9, 14, 20))
            };

            var grid = new Grid
            {
                RowSpacing = 0
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "FPS --   CPU --%",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(210, 174, 239, 255)),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.Clip,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _titleText = title;

            var fitText = new Viewbox
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 30,
                Child = title
            };

            Grid.SetRow(fitText, 0);
            grid.Children.Add(fitText);
            root.Child = grid;

            root.PointerPressed += Root_PointerPressed;
            return root;
        }

        private void Monitor_SnapshotChanged(object? sender, FpsMonitorSnapshot e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var fpsText = e.CurrentFps > 0 ? $"FPS {e.CurrentFps:0}" : "FPS --";
                var cpuText = $"CPU {e.CpuUsagePercent:0}%";
                if (_titleText != null)
                    _titleText.Text = $"{fpsText}   {cpuText}";

                _fpsText.Text = fpsText;
                _detailText.Text = $"CPU {e.CpuUsagePercent:0}%";
            });
        }

        private void ShowAtTopRight()
        {
            Activate();

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            ConfigureOverlayWindow();
            _appWindow.Resize(new SizeInt32(WidgetWidth, WidgetHeight));

            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            var x = area.WorkArea.X + area.WorkArea.Width - WidgetWidth - 28;
            var y = area.WorkArea.Y + 28;
            _appWindow.Move(new PointInt32(x, y));

            ApplyExtendedStyles();
            MakeTopMost();
            StartTopMostKeeper();
        }

        private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_hwnd == IntPtr.Zero)
                _hwnd = WindowNative.GetWindowHandle(this);

            ReleaseCapture();
            SendMessage(_hwnd, WmNcButtonDown, HtCaption, 0);
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

        private void StartTopMostKeeper()
        {
            StopTopMostKeeper();
            _topmostTimer = DispatcherQueue.CreateTimer();
            _topmostTimer.Interval = TimeSpan.FromMilliseconds(250);
            _topmostTimer.Tick += (_, _) => MakeTopMost(noActivate: true);
            _topmostTimer.Start();
        }

        private void StopTopMostKeeper()
        {
            try { _topmostTimer?.Stop(); } catch { }
            _topmostTimer = null;
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpNoSendChanging = 0x0400;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoRedirectionBitmap = 0x00200000;
    }
}

using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;
using Zink.Services;
using Zink.ViewModels;

namespace Zink
{
    public sealed class MiniRadioWidgetWindow : Window
    {
        private const int WidgetWidth = 480;
        private const int WidgetHeight = 190;
        private const int WmNcButtonDown = 0x00A1;
        private const int HtCaption = 2;

        private static MiniRadioWidgetWindow? _singleton;

        private readonly TextBlock _txtStation = new()
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };

        private readonly TextBlock _txtArtist = new()
        {
            FontSize = 12,
            Opacity = 0.95,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };

        private readonly TextBlock _txtTitle = new()
        {
            FontSize = 13,
            Opacity = 0.9,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };

        private readonly TextBlock _txtElapsed = new() { FontSize = 12 };
        private readonly TextBlock _txtDuration = new() { FontSize = 12, Opacity = 0.9 };
        private readonly Button _liveButton = new();
        private readonly Image _img = new() { Stretch = Stretch.Uniform };
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

        private IntPtr _hwnd;
        private Grid? _dragRegion;

        public RadioWidgetViewModel ViewModel { get; } = new();
        public static MiniRadioWidgetWindow? Current => _singleton;

        public MiniRadioWidgetWindow()
        {
            Title = "Radio Widget";
            Content = BuildUI();
            LocalizationService.RegisterWindow(this);
            TryUseCustomChrome();

            RefreshAll();
            ViewModel.PropertyChanged += (_, e) => RefreshChanged(e?.PropertyName);
            _tick.Tick += (_, __) => _txtElapsed.Text = FormatClock(ViewModel.Elapsed);
            _tick.Start();
        }

        public static void ShowSingleton()
        {
            if (_singleton == null)
            {
                _singleton = new MiniRadioWidgetWindow();
                _singleton.Closed += (_, __) => _singleton = null;
                _singleton.CenterAndShow();
            }
            else
            {
                _singleton.Activate();
            }
        }

        private UIElement BuildUI()
        {
            var root = new Grid
            {
                Width = WidgetWidth,
                Height = WidgetHeight
            };

            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 9, 15, 22), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 19, 42, 55), Offset = 0.48 },
                    new GradientStop { Color = Color.FromArgb(255, 5, 8, 13), Offset = 1 }
                }
            };

            _dragRegion = CreateHeader();
            root.Children.Add(_dragRegion);

            var shell = new Border
            {
                Margin = new Thickness(8, 0, 8, 8),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(18),
                Background = GlassBrush(0xC8, 0x12, 0x1B, 0x24),
                BorderBrush = GlassBrush(0x4D, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1.25)
            };
            Grid.SetRow(shell, 1);
            root.Children.Add(shell);

            var layout = new Grid
            {
                ColumnSpacing = 12
            };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shell.Child = layout;

            _txtStation.Foreground = GlassBrush(0xFF, 0xFF, 0xFF, 0xFF);
            _txtArtist.Foreground = GlassBrush(0xDE, 0xC9, 0xD4, 0xDE);
            _txtTitle.Foreground = GlassBrush(0xF5, 0xF3, 0xFA, 0xFF);
            _txtElapsed.Foreground = GlassBrush(0xFF, 0xFF, 0xFF, 0xFF);
            _txtDuration.Foreground = GlassBrush(0xD6, 0xC9, 0xD4, 0xDE);

            var imgBorder = new Border
            {
                CornerRadius = new CornerRadius(16),
                Height = 120,
                Width = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = GlassBrush(0xFF, 0x25, 0x38, 0x46),
                BorderBrush = GlassBrush(0x34, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1),
                Child = _img
            };
            layout.Children.Add(imgBorder);

            var stack = new StackPanel
            {
                Spacing = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(stack, 1);
            layout.Children.Add(stack);

            stack.Children.Add(_txtStation);
            stack.Children.Add(_txtArtist);
            stack.Children.Add(_txtTitle);
            stack.Children.Add(CreateTimeRow());
            stack.Children.Add(CreatePlaybackControls());

            return root;
        }

        private Grid CreateHeader()
        {
            var header = new Grid
            {
                Height = 26,
                Background = GlassBrush(0x01, 0, 0, 0),
                Margin = new Thickness(8, 3, 6, 3),
                ColumnSpacing = 6
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.PointerPressed += BeginDragMove;

            var badge = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(8),
                Background = GlassBrush(0xD0, 0x16, 0x2D, 0x35),
                BorderBrush = GlassBrush(0x50, 0xB8, 0xFB, 0xFF),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "z",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = GlassBrush(0xFF, 0xDC, 0xFF, 0xF7),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            header.Children.Add(badge);

            var title = new TextBlock
            {
                Text = "Radio Widget",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = GlassBrush(0xE8, 0xF2, 0xF8, 0xFF),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            var closeButton = CreateWindowButton("x", Close);
            Grid.SetColumn(closeButton, 2);
            header.Children.Add(closeButton);

            return header;
        }

        private UIElement CreateTimeRow()
        {
            var timeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 2, 0, 0)
            };

            timeRow.Children.Add(CreatePill(_txtElapsed, 0x3A, 0x2E, 0x8D, 0xFF, 0x70, 0x7C, 0xC7, 0xFF));
            _liveButton.MinWidth = 0;
            _liveButton.MinHeight = 0;
            _liveButton.Padding = new Thickness(8, 2, 8, 3);
            _liveButton.CornerRadius = new CornerRadius(999);
            _liveButton.BorderThickness = new Thickness(1);
            _liveButton.Foreground = GlassBrush(0xFF, 0xFF, 0xFF, 0xFF);
            _liveButton.Content = _txtDuration;
            _liveButton.Command = new RelayAction(() => AppPlaybackService.Instance.RequestLive());
            ToolTipService.SetToolTip(_liveButton, "Back to live");
            timeRow.Children.Add(_liveButton);
            RefreshLiveButton();
            return timeRow;
        }

        private UIElement CreatePlaybackControls()
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0)
            };

            buttons.Children.Add(CreateGlassButton(Symbol.Play, () => AppPlaybackService.Instance.RequestPlay(), "Play", primary: true, size: 27));
            buttons.Children.Add(CreateGlassButton(Symbol.Pause, () => AppPlaybackService.Instance.RequestPause(), "Pause", size: 25));
            buttons.Children.Add(CreateGlassButton(Symbol.Stop, () => AppPlaybackService.Instance.RequestWidgetStop(), "Stop", size: 25));
            return buttons;
        }

        private static Border CreatePill(
            UIElement child,
            byte backgroundAlpha,
            byte backgroundRed,
            byte backgroundGreen,
            byte backgroundBlue,
            byte borderAlpha,
            byte borderRed,
            byte borderGreen,
            byte borderBlue)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 2, 8, 3),
                Background = GlassBrush(backgroundAlpha, backgroundRed, backgroundGreen, backgroundBlue),
                BorderBrush = GlassBrush(borderAlpha, borderRed, borderGreen, borderBlue),
                BorderThickness = new Thickness(1),
                Child = child
            };
        }

        private static Button CreateGlassButton(Symbol symbol, Action action, string tooltip, bool primary = false, double size = 58)
        {
            var button = new Button
            {
                Width = size,
                Height = size,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(size / 2),
                BorderThickness = new Thickness(1),
                BorderBrush = primary ? GlassBrush(0x8A, 0xD8, 0xF0, 0xFF) : GlassBrush(0x34, 0xFF, 0xFF, 0xFF),
                Background = primary ? GlassBrush(0xDC, 0x2E, 0x8D, 0xFF) : GlassBrush(0x34, 0xFF, 0xFF, 0xFF),
                Foreground = GlassBrush(0xFF, 0xFF, 0xFF, 0xFF),
                Content = new FontIcon
                {
                    Glyph = symbol switch
                    {
                        Symbol.Play => "\uE768",
                        Symbol.Pause => "\uE769",
                        Symbol.Stop => "\uE71A",
                        _ => "\uE10C"
                    },
                    FontSize = 13
                },
                Command = new RelayAction(action)
            };
            ToolTipService.SetToolTip(button, tooltip);
            return button;
        }

        private static Button CreateWindowButton(string text, Action action)
        {
            var button = new Button
            {
                Width = 26,
                Height = 24,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = GlassBrush(0x24, 0xFF, 0xFF, 0xFF),
                BorderBrush = GlassBrush(0x28, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1),
                Foreground = GlassBrush(0xE8, 0xF2, 0xF8, 0xFF),
                Content = new TextBlock
                {
                    Text = "\u00D7",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Command = new RelayAction(action)
            };
            ToolTipService.SetToolTip(button, "Close");
            return button;
        }

        private void TryUseCustomChrome()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;
                if (_dragRegion != null)
                    SetTitleBar(_dragRegion);

                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
                var appWindow = AppWindow.GetFromWindowId(id);
                if (appWindow == null)
                    return;

                appWindow.Title = "Radio Widget";
                appWindow.Resize(new global::Windows.Graphics.SizeInt32(
                    DipsToPixels(WidgetWidth),
                    DipsToPixels(WidgetHeight)));

                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = true;
                    presenter.SetBorderAndTitleBar(false, false);
                }
            }
            catch
            {
            }
        }

        private void CenterAndShow()
        {
            Activate();

            var hwnd = _hwnd == IntPtr.Zero
                ? WinRT.Interop.WindowNative.GetWindowHandle(this)
                : _hwnd;
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);

            try
            {
                appWindow?.SetPresenter(AppWindowPresenterKind.Overlapped);
                int widgetWidthPixels = DipsToPixels(WidgetWidth);
                int widgetHeightPixels = DipsToPixels(WidgetHeight);
                appWindow?.Resize(new global::Windows.Graphics.SizeInt32(widgetWidthPixels, widgetHeightPixels));

                if (appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(false, false);
                }

                var da = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
                var wa = da.WorkArea;
                int x = wa.X + (wa.Width - widgetWidthPixels) / 2;
                int y = wa.Y + (wa.Height - widgetHeightPixels) / 2;
                appWindow?.Move(new global::Windows.Graphics.PointInt32(x, y));
            }
            catch
            {
            }

        }

        private void BeginDragMove(object sender, PointerRoutedEventArgs e)
        {
            if (_hwnd == IntPtr.Zero)
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            if (_hwnd == IntPtr.Zero)
                return;

            try
            {
                ReleaseCapture();
                SendMessage(_hwnd, WmNcButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
            }
            catch
            {
            }
        }

        private void RefreshAll()
        {
            _txtStation.Text = string.IsNullOrWhiteSpace(ViewModel.StationTitle) ? "Radio" : ViewModel.StationTitle;
            _txtArtist.Text = string.IsNullOrWhiteSpace(ViewModel.ArtistName) ? "-" : ViewModel.ArtistName;
            _txtTitle.Text = string.IsNullOrWhiteSpace(ViewModel.SongTitle) ? "-" : ViewModel.SongTitle;
            _txtElapsed.Text = FormatClock(ViewModel.Elapsed);
            _txtDuration.Text = ViewModel.Duration is TimeSpan d ? FormatClock(d) : "Live";
            RefreshLiveButton();
            _img.Source = ViewModel.ArtworkOrLogo != null ? new BitmapImage(ViewModel.ArtworkOrLogo) : null;
        }

        private void RefreshChanged(string? name)
        {
            switch (name)
            {
                case nameof(AppPlaybackService.StationTitle):
                    _txtStation.Text = string.IsNullOrWhiteSpace(ViewModel.StationTitle) ? "Radio" : ViewModel.StationTitle;
                    break;
                case nameof(AppPlaybackService.ArtistName):
                    _txtArtist.Text = string.IsNullOrWhiteSpace(ViewModel.ArtistName) ? "-" : ViewModel.ArtistName;
                    break;
                case nameof(AppPlaybackService.SongTitle):
                    _txtTitle.Text = string.IsNullOrWhiteSpace(ViewModel.SongTitle) ? "-" : ViewModel.SongTitle;
                    break;
                case nameof(AppPlaybackService.Duration):
                    _txtDuration.Text = ViewModel.Duration is TimeSpan d ? FormatClock(d) : "Live";
                    break;
                case nameof(AppPlaybackService.IsPlaying):
                    RefreshLiveButton();
                    break;
                case nameof(AppPlaybackService.ArtworkOrLogo):
                    _img.Source = ViewModel.ArtworkOrLogo != null ? new BitmapImage(ViewModel.ArtworkOrLogo) : null;
                    break;
            }
        }

        private void RefreshLiveButton()
        {
            bool isLive = ViewModel.IsPlaying;
            _liveButton.Background = isLive
                ? GlassBrush(0xE8, 0xD9, 0x1E, 0x2B)
                : GlassBrush(0x28, 0xFF, 0xFF, 0xFF);
            _liveButton.BorderBrush = isLive
                ? GlassBrush(0xFF, 0xFF, 0x68, 0x72)
                : GlassBrush(0x26, 0xFF, 0xFF, 0xFF);
        }

        private static SolidColorBrush GlassBrush(byte alpha, byte red, byte green, byte blue)
        {
            return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        }

        private int DipsToPixels(int dips)
        {
            try
            {
                if (Content?.XamlRoot is { RasterizationScale: > 0 } xamlRoot)
                    return Math.Max(1, (int)Math.Round(dips * xamlRoot.RasterizationScale));

                if (_hwnd == IntPtr.Zero)
                    _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

                return Math.Max(1, (int)Math.Round(dips * GetDpiForWindow(_hwnd) / 96.0));
            }
            catch
            {
                return dips;
            }
        }

        private static string FormatClock(TimeSpan ts) => $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private sealed class RelayAction : System.Windows.Input.ICommand
        {
            private readonly Action _action;

            public RelayAction(Action action) => _action = action;

            public bool CanExecute(object? parameter) => true;
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public void Execute(object? parameter) => _action();
        }
    }
}

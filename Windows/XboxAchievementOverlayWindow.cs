using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using Zink.Services;

namespace Zink
{
    /// <summary>A focus-safe, always-on-top achievement toast for desktop games.</summary>
    public sealed class XboxAchievementOverlayWindow : Window
    {
        private const int OverlayWidth = 720;
        private const int OverlayHeight = 190;
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;

        private static XboxAchievementOverlayWindow? _current;
        private readonly Grid _root;
        private readonly CompositeTransform _transform;

        private XboxAchievementOverlayWindow(string title, string score, string description)
        {
            Title = "ZINK achievement";
            ExtendsContentIntoTitleBar = true;
            _transform = new CompositeTransform
            {
                TranslateY = 42,
                ScaleX = 0.94,
                ScaleY = 0.94,
                CenterX = OverlayWidth / 2.0,
                CenterY = OverlayHeight / 2.0
            };
            _root = BuildContent(title, score, description);
            Content = _root;
            ConfigureWindow();
        }

        public static void Show(string title, string score, string description)
        {
            AchievementUnlockSoundService.Play();
            _current?.Close();
            _current = new XboxAchievementOverlayWindow(title, score, description);
            _current.Closed += (_, _) => _current = null;
            _current.ShowWithoutActivation();
            _current.BeginAnimation();
        }

        private Grid BuildContent(string title, string score, string description)
        {
            var root = new Grid
            {
                Width = OverlayWidth,
                Height = OverlayHeight,
                Opacity = 0,
                RenderTransform = _transform
            };

            var shell = new Border
            {
                CornerRadius = new CornerRadius(30),
                BorderThickness = new Thickness(2),
                BorderBrush = Brush(0xDD, 0x11, 0xD8, 0xE8),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(0xFA, 0x07, 0x18, 0x22), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0xFA, 0x0A, 0x24, 0x30), Offset = 0.55 },
                        new GradientStop { Color = Color.FromArgb(0xFA, 0x04, 0x10, 0x1A), Offset = 1 }
                    }
                }
            };
            root.Children.Add(shell);

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shell.Child = layout;

            var ring = new Border
            {
                Width = 138,
                Height = 138,
                CornerRadius = new CornerRadius(69),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = Brush(0xFF, 0x38, 0xFF, 0x66),
                BorderThickness = new Thickness(4),
                Background = Brush(0x54, 0x11, 0xD8, 0xE8)
            };
            layout.Children.Add(ring);

            ring.Child = new FontIcon
            {
                Glyph = "\uE7E7",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 48,
                Foreground = Brush(0xFF, 0xFF, 0xFF, 0xFF),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var text = new StackPanel
            {
                Margin = new Thickness(14, 25, 30, 22),
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 7
            };
            Grid.SetColumn(text, 1);
            layout.Children.Add(text);

            text.Children.Add(new TextBlock
            {
                Text = "ACHIEVEMENT UNLOCKED",
                Foreground = Brush(0xFF, 0x82, 0xFF, 0x9D),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 180
            });
            text.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush(0xFF, 0xFF, 0xFF, 0xFF),
                FontSize = 32,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });

            var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            details.Children.Add(new Border
            {
                Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = new CornerRadius(12),
                Background = Brush(0x30, 0x38, 0xFF, 0x66),
                BorderBrush = Brush(0xFF, 0x38, 0xFF, 0x66),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = score,
                    Foreground = Brush(0xFF, 0xFF, 0xFF, 0xFF),
                    FontSize = 17,
                    FontWeight = FontWeights.Bold
                }
            });
            details.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Brush(0xFF, 0xD6, 0xE8, 0xEA),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                MaxWidth = 385
            });
            text.Children.Add(details);
            return root;
        }

        private void ConfigureWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(id);
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            DisplayArea area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            int x = work.X + Math.Max(0, (work.Width - OverlayWidth) / 2);
            int y = work.Y + Math.Max(0, work.Height - OverlayHeight - 54);
            appWindow.MoveAndResize(new RectInt32(x, y, OverlayWidth, OverlayHeight));

            long styles = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(styles | WsExToolWindow | WsExNoActivate));
        }

        private void ShowWithoutActivation()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
        }

        private void BeginAnimation()
        {
            var storyboard = new Storyboard();
            var easingOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var opacity = new DoubleAnimationUsingKeyFrames();
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(520), Value = 1, EasingFunction = easingOut });
            opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(4300), Value = 1 });
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(4750), Value = 0, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            Storyboard.SetTarget(opacity, _root);
            Storyboard.SetTargetProperty(opacity, "Opacity");

            var slide = new DoubleAnimation
            {
                From = 42,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(520),
                EasingFunction = easingOut
            };
            Storyboard.SetTarget(slide, _transform);
            Storyboard.SetTargetProperty(slide, "TranslateY");
            storyboard.Children.Add(opacity);
            storyboard.Children.Add(slide);
            storyboard.Completed += (_, _) => Close();
            storyboard.Begin();
        }

        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
            new(Color.FromArgb(a, r, g, b));

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern IntPtr GetWindowLong32(IntPtr hwnd, int index);

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, IntPtr value);

        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value);
    }
}

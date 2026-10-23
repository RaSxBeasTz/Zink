using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Zink.Pages;
using Zink.Services;

namespace Zink.Controls
{
    /// <summary>Shared glass banner above the app's current page.</summary>
    internal sealed partial class ZinkNotificationHost : UserControl, IDisposable
    {
        private readonly MainWindow _window;
        private readonly Microsoft.UI.Windowing.AppWindow _appWindow;
        private readonly DispatcherQueueTimer _dismissTimer;
        private readonly DispatcherQueueTimer _quietTimer;
        private readonly TextBlock _title;
        private readonly TextBlock _message;
        private readonly TextBlock _category;
        private readonly FontIcon _icon;
        private NotificationPriority _priority;
        private long _requestVersion;
        private bool _disposed;

        public ZinkNotificationHost(MainWindow window)
        {
            _window = window;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(20);
            Width = 400;
            var card = new Border
            {
                CornerRadius = new CornerRadius(24),
                Padding = new Thickness(20),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(110, 17, 216, 232))
            };
            card.Background = new AcrylicBrush
            {
                TintColor = global::Windows.UI.Color.FromArgb(255, 6, 28, 36),
                TintOpacity = 0.78,
                TintLuminosityOpacity = 0.55,
                FallbackColor = global::Windows.UI.Color.FromArgb(250, 6, 28, 36)
            };
            RequestedTheme = ElementTheme.Dark;
            Shadow = new ThemeShadow();
            Translation = new System.Numerics.Vector3(0, 0, 24);
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

            var content = new Grid { ColumnSpacing = 14 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _icon = new FontIcon { Glyph = "\uEA8F", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 20,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 17, 216, 232)) };
            var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(35, 17, 216, 232)),
                VerticalAlignment = VerticalAlignment.Top, Child = _icon };
            content.Children.Add(badge);
            var text = new StackPanel { Spacing = 6 };
            _category = new TextBlock { Text = "ZINK", FontSize = 11, CharacterSpacing = 150,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 56, 255, 102)) };
            _title = new TextBlock { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            _message = new TextBlock { FontSize = 13, Opacity = 0.82, TextWrapping = TextWrapping.Wrap,
                MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Children.Add(_category);
            text.Children.Add(_title);
            text.Children.Add(_message);
            var view = new Button { Content = "View notifications", Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left };
            view.Click += (_, _) => { Dismiss(); _window.ContentFrame.Navigate(typeof(NotificationsPage)); };
            text.Children.Add(view);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            var close = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
                Padding = new Thickness(7), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(close, "Dismiss notification");
            close.Click += (_, _) => Dismiss();
            Grid.SetColumn(close, 2);
            content.Children.Add(close);
            card.Child = content;
            Content = card;

            _dismissTimer = DispatcherQueue.CreateTimer();
            _dismissTimer.IsRepeating = false;
            _dismissTimer.Tick += (_, _) => Dismiss();
            PointerEntered += (_, _) => _dismissTimer.Stop();
            PointerExited += (_, _) => { if (Visibility == Visibility.Visible) _dismissTimer.Start(); };
            GettingFocus += (_, _) => _dismissTimer.Stop();
            LostFocus += (_, _) => { if (Visibility == Visibility.Visible) _dismissTimer.Start(); };
            _quietTimer = DispatcherQueue.CreateTimer();
            _quietTimer.Interval = TimeSpan.FromMilliseconds(250);
            _quietTimer.Tick += (_, _) => CheckQuietMode();
            NotificationVideoGuard.Changed += CheckQuietMode;
            _appWindow.Changed += WindowChanged;
            AppPlaybackService.Instance.PropertyChanged += PlaybackStateChanged;
            NotificationService.Instance.BannerRequested += OnBannerRequested;
        }

        private void PlaybackStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(AppPlaybackService.IsPlaying) || args.PropertyName == nameof(AppPlaybackService.CurrentKind))
                CheckQuietMode();
        }

        private void WindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
        {
            CheckQuietMode();
        }

        private async void OnBannerRequested(Notification notification)
        {
            try { await PresentAsync(notification); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Zink notification banner: " + ex.Message); }
        }

        private async System.Threading.Tasks.Task PresentAsync(Notification notification)
        {
            if (_disposed || !_window.IsNotificationWindowActive) return;
            // Routine activity must not replace a call or critical alert.
            if (Visibility == Visibility.Visible && (int)notification.Priority < (int)_priority) return;
            var version = ++_requestVersion;
            if (notification.Priority == NotificationPriority.Normal)
            {
                await NotificationVideoGuard.RefreshAsync(_window.ContentFrame.Content as DependencyObject);
                if (_disposed || version != _requestVersion || _window.AreRoutineNotificationsSuppressed) return;
            }
            if (!_window.IsNotificationWindowActive) return;
            _dismissTimer.Stop();
            _priority = notification.Priority;
            _category.Text = notification.Priority switch
            {
                NotificationPriority.Call => "ZINK · CALL",
                NotificationPriority.Critical => "ZINK · IMPORTANT",
                _ => "ZINK"
            };
            _icon.Glyph = notification.Priority switch
            {
                NotificationPriority.Call => "\uE717",
                NotificationPriority.Critical => "\uE7BA",
                _ => "\uEA8F"
            };
            _title.Text = notification.Title;
            _message.Text = notification.Message;
            Width = Math.Max(0, Math.Min(400, ((_window.Content as FrameworkElement)?.ActualWidth ?? 440) - 40));
            Visibility = Visibility.Visible;
            AutomationProperties.SetName(this, $"{notification.Title}. {notification.Message}");
            FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            var animation = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
            Storyboard.SetTarget(animation, this);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
            _dismissTimer.Interval = TimeSpan.FromSeconds(notification.Priority == NotificationPriority.Normal ? 5 : 12);
            _dismissTimer.Start();
            _quietTimer.Start();
        }

        private void CheckQuietMode()
        {
            if (!_window.IsNotificationWindowActive ||
                (_priority == NotificationPriority.Normal && _window.AreRoutineNotificationsSuppressed))
                Dismiss();
        }

        private void Dismiss()
        {
            ++_requestVersion;
            _dismissTimer.Stop();
            _quietTimer.Stop();
            Visibility = Visibility.Collapsed;
        }

        public void Dispose()
        {
            _disposed = true;
            Dismiss();
            NotificationService.Instance.BannerRequested -= OnBannerRequested;
            NotificationVideoGuard.Changed -= CheckQuietMode;
            _appWindow.Changed -= WindowChanged;
            AppPlaybackService.Instance.PropertyChanged -= PlaybackStateChanged;
        }
    }
}

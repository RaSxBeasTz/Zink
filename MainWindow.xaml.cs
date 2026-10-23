using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using WinRT;
using WinRT.Interop;
using Zink.Pages;
using Zink.Pages.Social;
using Zink.Services;
using Zink.Services.Calling;
using Zink.Services.NativeCalling;
using Zink.Services.Recording;
using Zink.Services.Social;
using Zink.Services.Streaming;

namespace Zink
{
    public sealed partial class MainWindow : Window
    {
        private const double SidebarOpenWidth = 320;
        private const double SidebarCompactWidth = 104;
        private const double SidebarOpenPaneLength = 292;
        private const double SidebarCompactPaneLength = 76;

        private bool _isSidebarCompact;

        private bool _savedSidebarStateExists = false;
        private bool _savedSidebarVisible;
        private bool _savedPaneOpen;
        private NavigationViewPaneDisplayMode _savedPaneDisplayMode;
        private GridLength _savedSidebarWidth;

        private bool _weAreInFullscreenMode = false;

        private Timer? _fullscreenMonitorTimer;
        private readonly object _fullscreenMonitorLock = new();
        private IntPtr _fullscreenMouseHook;
        private LowLevelMouseProc? _fullscreenMouseProc;

        private DesktopAcrylicController? _acrylicController;
        private SystemBackdropConfiguration? _backdropConfig;
        private static readonly global::Windows.UI.Color DefaultGlassTint =
            global::Windows.UI.Color.FromArgb(255, 6, 64, 74);
        private static readonly global::Windows.UI.Color ZinkNeonAccent =
            global::Windows.UI.Color.FromArgb(255, 56, 255, 102);
        private static readonly global::Windows.UI.Color ZinkCyanAccent =
            global::Windows.UI.Color.FromArgb(255, 17, 216, 232);
        private static readonly global::Windows.UI.Color LegacyDefaultGlassTint =
            global::Windows.UI.Color.FromArgb(255, 59, 117, 130);
        private static readonly global::Windows.UI.Color PreviousEmeraldDefaultGlassTint =
            global::Windows.UI.Color.FromArgb(255, 14, 143, 61);
        private static readonly global::Windows.UI.Color PreviousNeonDefaultGlassTint =
            global::Windows.UI.Color.FromArgb(255, 56, 255, 102);
        private global::Windows.UI.Color _currentGlassTint = DefaultGlassTint;
        private ElementTheme _currentAppTheme = ElementTheme.Dark;

        private bool _windowIsActivated = false;
        private Zink.Controls.ZinkNotificationHost? _notificationHost;
        internal bool IsNotificationWindowActive => _windowIsActivated;
        internal bool AreRoutineNotificationsSuppressed
        {
            get
            {
                if (_weAreInFullscreenMode) return true;
                var hwnd = WindowNative.GetWindowHandle(this);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                if (AppWindow.GetFromWindowId(id)?.Presenter.Kind == AppWindowPresenterKind.FullScreen) return true;
                if (ContentFrame.Content is VideoPlayerPage video && video.IsVideoPlayingForNotifications) return true;
                var playback = AppPlaybackService.Instance;
                return (playback.CurrentKind == AppPlaybackService.MediaKind.Video && playback.IsPlaying) ||
                    NotificationVideoGuard.IsPlaying(ContentFrame.Content as DependencyObject);
            }
        }

        private bool _incomingCallDialogShowing = false;
        private bool _realtimeConnectAttempted = false;
        private bool _startupOverlayHidden = false;
        private bool _initialNavigationStarted = false;
        private HotkeyService? _hotkeyService;
        private readonly HashSet<WebView2> _discordPresenceWebViews = new();
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _localizationPopupTimer;
        private XboxAchievementNotificationService? _xboxAchievementNotificationService;

        public MainWindow()
        {
            this.InitializeComponent();

            ApplySavedThemeOnStartup();
            ApplySavedGlassTintOnStartup();

            this.Activated += MainWindow_Activated;
            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated_EnsureRealtime;

            TrySetDesktopAcrylicBackdrop();

            MaximizeWindow();
            SetWindowIcon();
            InitializeHotkeys();

            SidebarNav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(true);

            InitializeNavigationTabs();

            ContentFrame.Navigating += ContentFrame_Navigating;
            ContentFrame.Navigated += ContentFrame_Navigated;
            _notificationHost = new Zink.Controls.ZinkNotificationHost(this);
            Grid.SetColumnSpan(_notificationHost, 2);
            Canvas.SetZIndex(_notificationHost, 10001);
            RootGrid.Children.Add(_notificationHost);
            AppActivityNotificationBridge.Attach(RootGrid);
            RootGrid.Loaded += RootGrid_Loaded_StartInitialNavigation;
            RootGrid.Loaded += (_, _) => LocalizationService.Apply(RootGrid);
            RootGrid.Loaded += RootGrid_Loaded_StartXboxAchievementListener;
            LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
            _localizationPopupTimer = DispatcherQueue.CreateTimer();
            _localizationPopupTimer.Interval = TimeSpan.FromMilliseconds(350);
            _localizationPopupTimer.Tick += (_, _) =>
            {
                LocalizationService.ApplyOpenPopups(RootGrid.XamlRoot);
            };
            _localizationPopupTimer.Start();

            SocialManager.Instance.Realtime.IncomingCall -= Realtime_IncomingCall_Global;
            SocialManager.Instance.Realtime.IncomingCall += Realtime_IncomingCall_Global;

            VideoLibraryService.Current.BusyChanged -= VideoLibraryService_BusyChanged;
            VideoLibraryService.Current.BusyChanged += VideoLibraryService_BusyChanged;
            UpdateVideoLibrarySidebarBusyIndicator(VideoLibraryService.Current.IsBusy);

            _ = EnsureRealtimeConnectedIfLoggedInAsync();
        }

        private async void RootGrid_Loaded_StartXboxAchievementListener(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= RootGrid_Loaded_StartXboxAchievementListener;
            _xboxAchievementNotificationService = new XboxAchievementNotificationService(DispatcherQueue);
            await _xboxAchievementNotificationService.StartAsync();
        }

        private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => LocalizationService.Apply(RootGrid));
        }

        private void VideoLibraryService_BusyChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => UpdateVideoLibrarySidebarBusyIndicator(VideoLibraryService.Current.IsBusy));
        }

        private void UpdateVideoLibrarySidebarBusyIndicator(bool isBusy)
        {
            try
            {
                if (VideoLibrarySidebarBusyRing == null)
                    return;

                VideoLibrarySidebarBusyRing.IsActive = isBusy;
                VideoLibrarySidebarBusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private async void RootGrid_Loaded_StartInitialNavigation(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= RootGrid_Loaded_StartInitialNavigation;

            if (_initialNavigationStarted)
                return;

            _initialNavigationStarted = true;

            await Task.Delay(100);

            try
            {
                if (SuppressInitialNavigation || ContentFrame.Content != null || ContentFrame.CurrentSourcePageType != null)
                {
                    DispatcherQueue.TryEnqueue(ApplyGlassTintToCurrentPage);
                    return;
                }

                ContentFrame.Navigate(typeof(HomeDashboardPage));
                TrySelectHomeItem();
                DispatcherQueue.TryEnqueue(ApplyGlassTintToCurrentPage);
            }
            catch
            {
                HideStartupOverlay();
            }
        }

        private async void MainWindow_Activated_EnsureRealtime(object sender, WindowActivatedEventArgs e)
        {
            await EnsureRealtimeConnectedIfLoggedInAsync();
        }

        private async Task EnsureRealtimeConnectedIfLoggedInAsync()
        {
            try
            {
                var token = await TokenStore.Instance.GetTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                    return;

                if (_realtimeConnectAttempted && SocialManager.Instance.Realtime.IsConnected)
                    return;

                _realtimeConnectAttempted = true;

                if (!SocialManager.Instance.Realtime.IsConnected)
                {
                    await SocialManager.Instance.Realtime.ConnectAsync();
                }
            }
            catch
            {
            }
        }

        private void Realtime_IncomingCall_Global(object? sender, IncomingCallEventArgs e)
        {
            IncomingCallRingtoneService.TryStart();
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowIncomingCallDialogAsync(e);
            });
        }

        private async Task ShowIncomingCallDialogAsync(IncomingCallEventArgs e)
        {
            if (_incomingCallDialogShowing)
                return;

            if (RootGrid == null)
                return;

            _incomingCallDialogShowing = true;
            IncomingCallRingtoneService.TryStart();

            try
            {
                var callerName = !string.IsNullOrWhiteSpace(e.FromDisplayName)
                    ? e.FromDisplayName
                    : (!string.IsNullOrWhiteSpace(e.FromUsername) ? e.FromUsername : $"User {e.FromUserId}");

                var completion = new TaskCompletionSource<bool>();
                var body = CreateIncomingCallGlassContent(
                    callerName,
                    () =>
                    {
                        completion.TrySetResult(true);
                    },
                    () =>
                    {
                        completion.TrySetResult(false);
                    });

                var overlay = CreateIncomingCallOverlay(body);
                RootGrid.Children.Add(overlay);

                bool acceptedCall;
                try
                {
                    acceptedCall = await completion.Task;
                }
                finally
                {
                    RootGrid.Children.Remove(overlay);
                }

                IncomingCallRingtoneService.TryStop();

                if (acceptedCall)
                {
                    try
                    {
                        await NativeCallCoordinator.Instance.AcceptIncomingAsync(e.FromUserId, e.CallId);

                        ContentFrame.Navigate(typeof(CallPage), new CallPageArgs
                        {
                            TargetUserId = e.FromUserId,
                            IsScreenShare = false
                        });
                    }
                    catch
                    {
                    }
                }
                else
                {
                    await SocialManager.Instance.Realtime.RejectCallAsync(e.FromUserId, e.CallId);
                }
            }
            catch
            {
            }
            finally
            {
                IncomingCallRingtoneService.TryStop();
                _incomingCallDialogShowing = false;
            }
        }

        private static Grid CreateIncomingCallOverlay(FrameworkElement body)
        {
            var overlay = new Grid
            {
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(92, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(24)
            };

            Grid.SetColumnSpan(overlay, 2);
            Canvas.SetZIndex(overlay, 10000);

            body.HorizontalAlignment = HorizontalAlignment.Center;
            body.VerticalAlignment = VerticalAlignment.Center;
            overlay.Children.Add(body);

            return overlay;
        }

        private static FrameworkElement CreateIncomingCallGlassContent(
            string callerName,
            Action accept,
            Action decline)
        {
            var callerInitial = string.IsNullOrWhiteSpace(callerName)
                ? "Z"
                : callerName.Trim()[0].ToString().ToUpperInvariant();

            var acrylicBrush = new AcrylicBrush
            {
                TintColor = global::Windows.UI.Color.FromArgb(255, 15, 23, 31),
                TintOpacity = 0.78,
                TintLuminosityOpacity = 0.58,
                FallbackColor = global::Windows.UI.Color.FromArgb(245, 15, 23, 31)
            };

            var card = new Border
            {
                Width = 420,
                MaxWidth = 460,
                Padding = new Thickness(24),
                CornerRadius = new CornerRadius(28),
                Background = acrylicBrush,
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(24, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };

            var root = new Grid
            {
                RowSpacing = 16
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid
            {
                ColumnSpacing = 16
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = new Grid
            {
                Width = 58,
                Height = 58
            };

            avatar.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = global::Windows.UI.Color.FromArgb(255, 34, 211, 238), Offset = 0 },
                        new GradientStop { Color = global::Windows.UI.Color.FromArgb(255, 45, 212, 191), Offset = 0.52 },
                        new GradientStop { Color = global::Windows.UI.Color.FromArgb(255, 99, 102, 241), Offset = 1 }
                    }
                },
                Stroke = new SolidColorBrush(global::Windows.UI.Color.FromArgb(90, 255, 255, 255)),
                StrokeThickness = 1
            });

            avatar.Children.Add(new TextBlock
            {
                Text = callerInitial,
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            var titleStack = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            titleStack.Children.Add(new TextBlock
            {
                Text = "Incoming call",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(210, 188, 245, 255))
            });

            titleStack.Children.Add(new TextBlock
            {
                Text = callerName,
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255))
            });

            Grid.SetColumn(titleStack, 1);
            header.Children.Add(avatar);
            header.Children.Add(titleStack);

            var statusPill = new Border
            {
                Padding = new Thickness(14, 9, 14, 9),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };

            var statusRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };

            statusRow.Children.Add(new FontIcon
            {
                Glyph = "\uE717",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Width = 22,
                Height = 22,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 125, 249, 196))
            });

            statusRow.Children.Add(new TextBlock
            {
                Text = "Ringing now on Zink",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(235, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });

            statusPill.Child = statusRow;

            var actionGrid = new Grid
            {
                ColumnSpacing = 12
            };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var answerButton = CreateIncomingCallActionButton(
                "\uE717",
                "Answer",
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 21, 132, 151)),
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            answerButton.Click += (_, _) => accept();

            var declineButton = CreateIncomingCallActionButton(
                "\uE711",
                "Decline",
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, 255, 255, 255)),
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(238, 255, 255, 255)));
            declineButton.Click += (_, _) => decline();

            Grid.SetColumn(declineButton, 1);
            actionGrid.Children.Add(answerButton);
            actionGrid.Children.Add(declineButton);

            Grid.SetRow(header, 0);
            Grid.SetRow(statusPill, 1);
            Grid.SetRow(actionGrid, 2);

            root.Children.Add(header);
            root.Children.Add(statusPill);
            root.Children.Add(actionGrid);
            card.Child = root;

            return card;
        }

        private static Button CreateIncomingCallActionButton(
            string glyph,
            string text,
            Brush background,
            Brush foreground)
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            content.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 17,
                Foreground = foreground
            });

            content.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = foreground
            });

            return new Button
            {
                Height = 48,
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(16),
                Background = background,
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = content
            };
        }

        public void ApplyAppTheme(ElementTheme theme)
        {
            _currentAppTheme = theme;

            try
            {
                ApplicationData.Current.LocalSettings.Values["Zink.Theme"] = theme switch
                {
                    ElementTheme.Light => "Light",
                    ElementTheme.Dark => "Dark",
                    _ => "Default"
                };
            }
            catch { }

            try
            {
                if (RootGrid != null)
                {
                    RootGrid.RequestedTheme = theme;
                }

                if (SidebarNav != null)
                {
                    SidebarNav.RequestedTheme = theme;
                }

                if (ContentFrame != null)
                {
                    ContentFrame.RequestedTheme = theme;
                }

                if (ContentFrame?.Content is FrameworkElement content)
                {
                    content.RequestedTheme = theme;
                }

                if (RootGrid != null)
                {
                    SetBackdropThemeFromRoot(RootGrid);
                }

                ApplyGlassTintToResources(_currentGlassTint, true);
            }
            catch { }
        }

        private ElementTheme GetEffectiveAppTheme()
        {
            try
            {
                if (_currentAppTheme != ElementTheme.Default)
                    return _currentAppTheme;

                if (RootGrid?.ActualTheme == ElementTheme.Light || RootGrid?.ActualTheme == ElementTheme.Dark)
                    return RootGrid.ActualTheme;

                if (Content is FrameworkElement root &&
                    (root.ActualTheme == ElementTheme.Light || root.ActualTheme == ElementTheme.Dark))
                {
                    return root.ActualTheme;
                }
            }
            catch { }

            return ElementTheme.Default;
        }

        public IntPtr GetWindowHandle()
        {
            return WindowNative.GetWindowHandle(this);
        }

        private void InitializeHotkeys()
        {
            try
            {
                _hotkeyService = new HotkeyService(GetWindowHandle());
                _hotkeyService.Initialize();
            }
            catch
            {
                _hotkeyService = null;
            }
        }

        public void HideToTray()
        {
            try
            {
                var hwnd = GetWindowHandle();
                ShowWindow(hwnd, SW_HIDE);
            }
            catch
            {
            }
        }

        public void ShowFromTray()
        {
            try
            {
                var hwnd = GetWindowHandle();
                ShowWindow(hwnd, SW_RESTORE);
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
                this.Activate();
            }
            catch
            {
            }
        }

        private void ApplySavedThemeOnStartup()
        {
            try
            {
                var value = ApplicationData.Current.LocalSettings.Values["Zink.Theme"] as string ?? "Dark";

                var theme = value switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };

                _currentAppTheme = theme;
                ApplyAppTheme(theme);
            }
            catch
            {
            }
        }

        private void TrySelectHomeItem()
        {
            try
            {
                var homeItem = FindNavItemByTag(SidebarNav.MenuItems, "Home");
                if (homeItem != null)
                    SidebarNav.SelectedItem = homeItem;
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Win32Point point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int SW_MAXIMIZE = 3;
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private void MaximizeWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_MAXIMIZE);
        }

        private void SetWindowIcon()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Zink.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
            appWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        }

        public NavigationView SidebarNavReference => SidebarNav;
        public ColumnDefinition SidebarColumnReference => SidebarColumn;
        public Frame MainFrame => ContentFrame;
        public bool SuppressInitialNavigation { get; set; }

        public void NavigateFromFileActivation(Type pageType, object parameter)
        {
            try
            {
                SuppressInitialNavigation = true;
                MaximizeWindow();
                DiagnosticLogService.WriteLine("File activation navigation started for " + pageType.Name + ".");
                if (InitialContentFrame.Content == null && InitialContentFrame.CurrentSourcePageType == null)
                {
                    InitialContentFrame.Navigate(typeof(HomeDashboardPage));
                }
                OpenSidebarTab(pageType, parameter);
                ClearStartupOverlayImmediately();

                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        ApplyGlassTintToCurrentPage();
                        ClearStartupOverlayImmediately();
                    }
                    catch { }
                });
            }
            catch
            {
                ClearStartupOverlayImmediately();
            }
        }

        public void ClearStartupOverlayImmediately()
        {
            try
            {
                _startupOverlayHidden = true;

                if (StartupOverlay != null)
                {
                    StartupOverlay.Visibility = Visibility.Collapsed;
                    StartupOverlay.IsHitTestVisible = false;
                }
            }
            catch { }
        }

        public void SaveAndHideSidebar()
        {
            if (_savedSidebarStateExists) return;

            _savedSidebarVisible = SidebarNav.IsPaneVisible;
            _savedPaneOpen = SidebarNav.IsPaneOpen;
            _savedPaneDisplayMode = SidebarNav.PaneDisplayMode;
            _savedSidebarWidth = SidebarColumn.Width;

            _savedSidebarStateExists = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    TabStripGlass.Visibility = Visibility.Collapsed;
                    SidebarNav.IsPaneVisible = false;
                    SidebarNav.Visibility = Visibility.Collapsed;
                    SidebarColumn.Width = new GridLength(0);
                }
                catch { }
            });
        }

        public void RestoreSavedSidebar()
        {
            if (!_savedSidebarStateExists) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    TabStripGlass.Visibility = Visibility.Visible;
                    SidebarNav.IsPaneVisible = _savedSidebarVisible;
                    SidebarNav.IsPaneOpen = _savedPaneOpen;
                    SidebarNav.PaneDisplayMode = _savedPaneDisplayMode;
                    SidebarColumn.Width = _savedSidebarVisible
                        ? (_savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(_savedPaneOpen ? SidebarOpenWidth : SidebarCompactWidth))
                        : new GridLength(0);
                    SidebarNav.Visibility = _savedSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
                }
                catch { }
                finally
                {
                    _savedSidebarStateExists = false;
                }
            });
        }

        // Twitch HTML5 fullscreen needs the TabView's row removed, not merely
        // its visibility changed. Restrict this to Twitch so other fullscreen
        // pages keep their original shell layout and behaviour.
        private GridLength? _savedTwitchTabStripHeight;
        public void SetTwitchPlayerFullscreenLayout(bool enabled)
        {
            try
            {
                var rows = TabStripGlass.Parent as Grid;
                if (rows == null || rows.RowDefinitions.Count == 0) return;
                var row = rows.RowDefinitions[0];
                if (enabled)
                {
                    if (!_savedTwitchTabStripHeight.HasValue)
                        _savedTwitchTabStripHeight = row.Height;
                    row.Height = new GridLength(0);
                }
                else if (_savedTwitchTabStripHeight.HasValue)
                {
                    row.Height = _savedTwitchTabStripHeight.Value;
                    _savedTwitchTabStripHeight = null;
                }
            }
            catch (Exception error)
            {
                DiagnosticLogService.WriteLine("Twitch player fullscreen shell layout: " + error.Message);
            }
        }

        public void EnterFullscreenMode(bool enableMouseExitHook = true)
        {
            if (_weAreInFullscreenMode) return;

            SaveAndHideSidebar();

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    _weAreInFullscreenMode = true;

                    if (enableMouseExitHook) StartFullscreenMouseExitHook();
                    StartFullscreenMonitor(appWindow);
                }
                catch { }
            });
        }

        public void ExitFullscreenMode()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    _weAreInFullscreenMode = false;
                    StopFullscreenMouseExitHook();
                    StopFullscreenMonitor();
                }
                catch { }
                finally
                {
                    RestoreSavedSidebar();
                }
            });
        }

        public void RestoreSidebar()
        {
            TabStripGlass.Visibility = Visibility.Visible;
            SidebarNav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            SidebarNav.IsPaneOpen = true;
            SidebarNav.IsPaneVisible = true;
            SidebarNav.Visibility = Visibility.Visible;
            _isSidebarCompact = false;
            SetSidebarColumnForPaneState(true);
            _savedSidebarStateExists = false;
            _weAreInFullscreenMode = false;
            StopFullscreenMouseExitHook();
            StopFullscreenMonitor();
        }

        public void SetSidebarVisibility(bool visible)
        {
            SidebarNav.IsPaneVisible = visible;
            SidebarNav.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SidebarColumn.Width = visible
                ? new GridLength(SidebarNav.IsPaneOpen ? SidebarOpenWidth : SidebarCompactWidth)
                : new GridLength(0);
        }

        private void SidebarNav_PaneOpening(NavigationView sender, object args)
        {
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarNav_PaneOpened(NavigationView sender, object args)
        {
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarNav_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
        {
            args.Cancel = true;
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarNav_PaneClosed(NavigationView sender, object args)
        {
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarCompact = !_isSidebarCompact;
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        public void CollapseSidebar()
        {
            _isSidebarCompact = true;
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(false);
        }

        private void SetSidebarColumnForPaneState(bool isOpen)
        {
            try
            {
                if (SidebarNav.Visibility != Visibility.Visible || !SidebarNav.IsPaneVisible)
                {
                    SidebarColumn.Width = new GridLength(0);
                    return;
                }

                SidebarNav.IsPaneOpen = true;
                SidebarNav.OpenPaneLength = isOpen ? SidebarOpenPaneLength : SidebarCompactPaneLength;
                SidebarColumn.Width = new GridLength(isOpen ? SidebarOpenWidth : SidebarCompactWidth);
            }
            catch { }
        }

        private void StartFullscreenMonitor(AppWindow appWindow)
        {
            lock (_fullscreenMonitorLock)
            {
                StopFullscreenMonitor();

                _fullscreenMonitorTimer = new Timer(_ =>
                {
                    try
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                                var currentAppWindow = AppWindow.GetFromWindowId(windowId);
                                if (currentAppWindow is null) return;

                                var presenterKind = currentAppWindow.Presenter?.Kind;
                                if (_weAreInFullscreenMode && presenterKind != AppWindowPresenterKind.FullScreen)
                                {
                                    _weAreInFullscreenMode = false;
                                    StopFullscreenMonitor();
                                    RestoreSavedSidebar();
                                }
                            }
                            catch { }
                        });
                    }
                    catch { }
                }, null, 250, 250);
            }
        }

        private void StopFullscreenMonitor()
        {
            lock (_fullscreenMonitorLock)
            {
                try
                {
                    _fullscreenMonitorTimer?.Dispose();
                }
                catch { }
                finally
                {
                    _fullscreenMonitorTimer = null;
                }
            }
        }

        private void StartFullscreenMouseExitHook()
        {
            if (_fullscreenMouseHook != IntPtr.Zero)
                return;

            _fullscreenMouseProc = FullscreenMouseHookProc;
            _fullscreenMouseHook = SetWindowsHookEx(WH_MOUSE_LL, _fullscreenMouseProc, IntPtr.Zero, 0);
        }

        private void StopFullscreenMouseExitHook()
        {
            if (_fullscreenMouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_fullscreenMouseHook);
                _fullscreenMouseHook = IntPtr.Zero;
            }

            _fullscreenMouseProc = null;
        }

        private IntPtr FullscreenMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 &&
                _weAreInFullscreenMode &&
                wParam == (IntPtr)WM_LBUTTONDOWN &&
                IsCursorInFullscreenExitZone())
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ContentFrame.Content is CallPage callPage)
                        callPage.ExitScreenShareFullscreenFromWindowHook();

                    ExitFullscreenMode();
                });
                return (IntPtr)1;
            }

            return CallNextHookEx(_fullscreenMouseHook, nCode, wParam, lParam);
        }

        private bool IsCursorInFullscreenExitZone()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (!GetWindowRect(hwnd, out var rect) || !GetCursorPos(out var point))
                    return false;

                const int exitZoneSize = 180;
                return point.X >= rect.Right - exitZoneSize &&
                    point.X <= rect.Right &&
                    point.Y >= rect.Bottom - exitZoneSize &&
                    point.Y <= rect.Bottom;
            }
            catch
            {
                return false;
            }
        }

        // Sidebar selection is synchronized by navigation; invocation opens a separate tab.
        private void SidebarNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
        }

        private void SidebarNav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (!_syncingTabSelection && args.InvokedItemContainer is NavigationViewItem item &&
                GetSidebarPageType(item.Tag) is Type pageType)
                OpenSidebarTab(pageType);
        }

        private static Type? GetSidebarPageType(object tag) => tag switch
            {
                "Search" => typeof(SearchResultsPage),
                "Home" => typeof(HomeDashboardPage),
                "ZinkConnect" => typeof(ZinkConnectPage),
                "ZinkVpn" => typeof(ZinkVpnPage),
                "Tools" => typeof(ToolsPage),
                "MusicPlayer" => typeof(MusicPlayerPage),
                "MusicLibrary" => typeof(MusicLibraryPage),
                "YouTubeMusic" => typeof(YouTubeMusicPage),
                "AmazonMusic" => typeof(AmazonMusicPage),
                "Equalizer" => typeof(EqualizerPage),
                "Visualizer" => typeof(VisualizerPage),
                "VideoPlayer" => typeof(VideoPlayerPage),
                "VideoLibrary" => typeof(VideoLibraryPage),
                "OneDriveStreaming" => typeof(OneDriveStreamingPage),
                "DuplicateVideoSearcher" => typeof(DuplicateVideoSearcherPage),
                "ScreenRecorder" => typeof(RecorderPage),
                "RecordingsLibrary" => typeof(RecordingsLibraryPage),
                "Streaming" => typeof(StreamingPage),
                "YouTubeStreaming" => typeof(YouTubeStreamingPage),
                "KickStreaming" => typeof(KickStreamingPage),
                "InstagramStreaming" => typeof(InstagramStreamingPage),
                "TikTokStreaming" => typeof(TikTokStreamingPage),
                "FacebookStreaming" => typeof(FacebookStreamingPage),
                "XStreaming" => typeof(XStreamingPage),
                "Netflix" => typeof(NetflixPage),
                "PrimeVideo" => typeof(PrimeVideoPage),
                "DisneyPlus" => typeof(DisneyPlusPage),
                "ParamountPlus" => typeof(ParamountPlusPage),
                "NowTV" => typeof(NowTVPage),
                "BBCiPlayer" => typeof(BBCiPlayerPage),
                "My5" => typeof(My5Page),
                "Radio" => typeof(RadioPage),
                "RadioDiscovery" => typeof(RadioDiscoveryPage),
                "Spotify" => typeof(SpotifyLoginPage),
                "SpotifyWidget" => typeof(SpotifyWidgetPage),
                "LikedRadioSongs" => typeof(LikedRadioSongsPage),
                "RadioWidget" => typeof(RadioWidgetPage),
                "Twitch" => typeof(TwitchPage),
                "Discord" => typeof(DiscordPage),
                "YouTube" => typeof(YouTubePage),
                "TikTok" => typeof(TikTokPage),
                "Instagram" => typeof(InstagramPage),
                "X" => typeof(XPage),
                "Facebook" => typeof(FacebookPage),
                "Telegram" => typeof(TelegramPage),
                "WhatsApp" => typeof(WhatsAppPage),
                "Messenger" => typeof(MessengerPage),
                "LinkedIn" => typeof(LinkedInPage),
                "Threads" => typeof(ThreadsPage),
                "Bluesky" => typeof(BlueskyPage),
                "Mastodon" => typeof(MastodonPage),
                "Pinterest" => typeof(PinterestPage),
                "Tumblr" => typeof(TumblrPage),
                "Reddit" => typeof(RedditPage),
                "ZinkSocial" => typeof(SocialLockedPage),
                "SocialLogin" => typeof(LoginPage),
                "SocialRegister" => typeof(RegisterPage),
                "SocialDeveloperSettings" => typeof(DeveloperSettingsPage),
                "SocialFriends" => typeof(FriendsPage),
                "SocialMessages" => typeof(MessagesPage),
                "SocialFriendRequests" => typeof(FriendRequestsPage),
                "SocialProfile" => typeof(ProfilePage),
                "SocialCall" => typeof(CallPage),
                "Xbox" => typeof(XboxPage),
                "FpsRecorder" => typeof(FpsRecorderPage),
                "GeForceNow" => typeof(GeForceNowPage),
                "AmazonLuna" => typeof(AmazonLunaPage),
                "Boosteroid" => typeof(BoosteroidPage),
                "ShadowPC" => typeof(ShadowPCPage),
                "FeatureTour" => typeof(FeatureTourPage),
                "WidgetHub" => typeof(WidgetHubPage),
                "Notifications" => typeof(NotificationsPage),
                "PrivacyPolicy" => typeof(PrivacyPolicyPage),
                "LeaveReview" => typeof(ReviewPage),
                "AppCustomization" => typeof(AppCustomizationPage),
                "Settings" => typeof(SettingsPage),
                "About" => typeof(AboutPage),
                _ => null
        };

        private bool TrySetDesktopAcrylicBackdrop()
        {
            if (!DesktopAcrylicController.IsSupported())
                return false;

            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true
            };

            if (Content is FrameworkElement rootElement)
            {
                rootElement.ActualThemeChanged += RootElement_ActualThemeChanged;
                SetBackdropThemeFromRoot(rootElement);
            }

            _acrylicController = new DesktopAcrylicController();
            _acrylicController.AddSystemBackdropTarget(
                this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);

            return true;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            _windowIsActivated = args.WindowActivationState != WindowActivationState.Deactivated;

            if (_backdropConfig == null) return;

            _backdropConfig.IsInputActive =
                args.WindowActivationState != WindowActivationState.Deactivated;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _notificationHost?.Dispose();
            _notificationHost = null;
            SocialManager.Instance.Realtime.IncomingCall -= Realtime_IncomingCall_Global;
            VideoLibraryService.Current.BusyChanged -= VideoLibraryService_BusyChanged;

            _hotkeyService?.Dispose();
            _hotkeyService = null;

            _xboxAchievementNotificationService?.Dispose();
            _xboxAchievementNotificationService = null;

            if (_acrylicController != null)
            {
                _acrylicController.Dispose();
                _acrylicController = null;
            }

            _backdropConfig = null;
        }

        private void RootElement_ActualThemeChanged(FrameworkElement sender, object args)
        {
            SetBackdropThemeFromRoot(sender);

            if (_currentAppTheme == ElementTheme.Default)
            {
                ApplyGlassTintToResources(_currentGlassTint, true);
            }
        }

        private void SetBackdropThemeFromRoot(FrameworkElement root)
        {
            if (_backdropConfig == null) return;

            _backdropConfig.Theme = root.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default
            };
        }

        public void ApplyGlassTint(global::Windows.UI.Color tint)
        {
            ApplyGlassTint(tint, true);
        }

        public void ApplyGlassTint(global::Windows.UI.Color tint, bool tintCurrentPage)
        {
            _currentGlassTint = tint;

            try
            {
                ApplicationData.Current.LocalSettings.Values["Zink.GlassTint"] = ColorToHex(tint);
            }
            catch { }

            ApplyGlassTintToResources(tint, tintCurrentPage);
        }

        private void ApplySavedGlassTintOnStartup()
        {
            try
            {
                var saved = ApplicationData.Current.LocalSettings.Values["Zink.GlassTint"] as string;
                var tint = TryParseHexColor(saved, out var savedTint) && !IsLegacyDefaultGlassTint(savedTint)
                    ? savedTint
                    : DefaultGlassTint;

                if (!string.Equals(saved, ColorToHex(tint), StringComparison.OrdinalIgnoreCase))
                {
                    ApplicationData.Current.LocalSettings.Values["Zink.GlassTint"] = ColorToHex(tint);
                }

                _currentGlassTint = tint;
                ApplyGlassTintToResources(tint, true);
            }
            catch { }
        }

        private void ApplyGlassTintToResources(global::Windows.UI.Color tint, bool tintCurrentPage)
        {
            var useLightTheme = GetEffectiveAppTheme() == ElementTheme.Light;
            var panel = useLightTheme ? WithAlpha(MixWithWhite(tint, 210), 172) : WithAlpha(Darken(tint, 34), 100);
            var card = useLightTheme ? WithAlpha(MixWithWhite(tint, 230), 142) : WithAlpha(Darken(tint, 16), 70);
            var hover = useLightTheme ? WithAlpha(tint, 24) : WithAlpha(ZinkCyanAccent, 48);
            var selected = useLightTheme ? WithAlpha(tint, 38) : WithAlpha(ZinkNeonAccent, 64);
            var pressed = useLightTheme ? WithAlpha(Darken(tint, 20), 34) : WithAlpha(ZinkNeonAccent, 56);
            var primaryText = GetTintedTextColor(tint, TextBrushRole.Primary, useLightTheme);
            var mutedText = GetTintedTextColor(tint, TextBrushRole.Muted, useLightTheme);
            var sidebarPrimaryText = useLightTheme
                ? global::Windows.UI.Color.FromArgb(255, 0, 0, 0)
                : primaryText;
            var sidebarMutedText = useLightTheme
                ? global::Windows.UI.Color.FromArgb(255, 0, 0, 0)
                : mutedText;

            SetBrushColor("ZinkGlassPanelBrush", panel);
            SetBrushColor("ZinkGlassCardBrush", card);
            SetBrushColor("ZinkGlassBorderBrush", useLightTheme ? WithAlpha(Darken(tint, 24), 70) : WithAlpha(ZinkCyanAccent, 122));
            SetBrushColor("ZinkGlassHoverBrush", hover);
            SetBrushColor("ZinkGlassSelectedBrush", selected);
            SetBrushColor("NavigationViewItemBackgroundPointerOver", hover);
            SetBrushColor("NavigationViewItemBackgroundSelected", selected);
            SetBrushColor("NavigationViewItemBackgroundPressed", pressed);
            // Match the reference panel while retaining the user's light theme and tint settings.
            SetBrushColor("NavigationViewItemBackgroundPointerOver", useLightTheme ? WithAlpha(tint, 18) : WithAlpha(ZinkCyanAccent, 18));
            SetBrushColor("NavigationViewItemBackgroundPressed", useLightTheme ? WithAlpha(tint, 30) : WithAlpha(ZinkCyanAccent, 28));
            SetBrushColor("NavigationViewItemForegroundSelectedPointerOver", sidebarPrimaryText);
            SetBrushColor("NavigationViewItemForegroundSelectedPressed", sidebarPrimaryText);
            SidebarPanelTop.Color = useLightTheme ? WithAlpha(MixWithWhite(tint, 235), 180) : WithAlpha(Darken(tint, 70), 156);
            SidebarPanelMiddle.Color = useLightTheme ? WithAlpha(MixWithWhite(tint, 225), 160) : WithAlpha(Darken(tint, 42), 120);
            SidebarPanelBottom.Color = useLightTheme ? WithAlpha(MixWithWhite(tint, 240), 140) : WithAlpha(Darken(tint, 80), 88);

            SetBrushColor("NavigationViewItemForeground", sidebarMutedText);
            SetBrushColor("NavigationViewItemForegroundPointerOver", sidebarPrimaryText);
            SetBrushColor("NavigationViewItemForegroundSelected", sidebarPrimaryText);
            SetBrushColor("NavigationViewItemIconForeground", sidebarMutedText);
            SetBrushColor("NavigationViewItemIconForegroundPointerOver", sidebarPrimaryText);
            SetBrushColor("NavigationViewItemIconForegroundSelected", useLightTheme ? Darken(ZinkNeonAccent, 140) : ZinkNeonAccent);
            SetBrushColor("NavigationViewItemHeaderForeground", sidebarMutedText);
            SetBrushColor("NavigationViewItemSeparatorForeground", sidebarMutedText);
            ApplyTitleBarGlass(tint, useLightTheme);
            if (RootGrid.Resources["NavigationTabGlassBrush"] is AcrylicBrush tabGlass)
            {
                tabGlass.TintColor = useLightTheme ? MixWithWhite(tint, 220) : tint;
                tabGlass.TintLuminosityOpacity = useLightTheme ? 0.75 : 0.35;
                tabGlass.FallbackColor = useLightTheme ? WithAlpha(MixWithWhite(tint, 220), 180) : WithAlpha(tint, 176);
            }

            SidebarNav.Foreground = new SolidColorBrush(sidebarPrimaryText);
            ApplySidebarItemTextColor(sidebarMutedText);

            if (useLightTheme)
            {
                ShellGradientStart.Color = MixWithWhite(tint, 238);
                ShellGradientMiddle.Color = MixWithWhite(tint, 220);
                ShellGradientEnd.Color = MixWithWhite(tint, 246);
            }
            else
            {
                ShellGradientStart.Color = global::Windows.UI.Color.FromArgb(222, 0, 8, 20);
                ShellGradientMiddle.Color = WithAlpha(Darken(tint, 26), 255);
                ShellGradientEnd.Color = WithAlpha(tint, 255);
            }

            if (tintCurrentPage)
            {
                ApplyGlassTintToCurrentPage();
            }
        }

        private void ApplyTitleBarGlass(global::Windows.UI.Color tint, bool useLightTheme)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var titleBar = AppWindow.GetFromWindowId(windowId).TitleBar;

                var titleBarColor = useLightTheme
                    ? MixWithWhite(tint, 236)
                    : Darken(tint, 82);
                var inactiveTitleBarColor = useLightTheme
                    ? MixWithWhite(tint, 244)
                    : Darken(tint, 104);
                var hoverColor = useLightTheme
                    ? MixWithWhite(tint, 218)
                    : Darken(tint, 54);
                var pressedColor = useLightTheme
                    ? MixWithWhite(tint, 202)
                    : Darken(tint, 36);
                var foregroundColor = useLightTheme
                    ? global::Windows.UI.Color.FromArgb(255, 20, 28, 31)
                    : global::Windows.UI.Color.FromArgb(255, 238, 250, 255);
                var inactiveForegroundColor = useLightTheme
                    ? global::Windows.UI.Color.FromArgb(255, 92, 104, 108)
                    : global::Windows.UI.Color.FromArgb(255, 157, 185, 191);

                titleBar.BackgroundColor = titleBarColor;
                titleBar.ForegroundColor = foregroundColor;
                titleBar.InactiveBackgroundColor = inactiveTitleBarColor;
                titleBar.InactiveForegroundColor = inactiveForegroundColor;
                titleBar.ButtonBackgroundColor = titleBarColor;
                titleBar.ButtonForegroundColor = foregroundColor;
                titleBar.ButtonHoverBackgroundColor = hoverColor;
                titleBar.ButtonHoverForegroundColor = foregroundColor;
                titleBar.ButtonPressedBackgroundColor = pressedColor;
                titleBar.ButtonPressedForegroundColor = foregroundColor;
                titleBar.ButtonInactiveBackgroundColor = inactiveTitleBarColor;
                titleBar.ButtonInactiveForegroundColor = inactiveForegroundColor;
            }
            catch { }
        }

        private void ApplyGlassTintToCurrentPage()
        {
            try
            {
                if (ContentFrame?.Content is FrameworkElement content)
                {
                    content.RequestedTheme = _currentAppTheme;
                    content.Loaded -= CurrentPage_Loaded;
                    content.Loaded += CurrentPage_Loaded;

                    ApplyGlassTintToElementTree(content, _currentGlassTint);

                    if (content is not SettingsPage)
                    {
                        QueueGlassTintPass(content, 3);
                    }
                }
            }
            catch { }
        }

        private void CurrentPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement content)
                {
                    content.RequestedTheme = _currentAppTheme;
                    ApplyGlassTintToElementTree(content, _currentGlassTint);
                    LocalizationService.Apply(content);

                    if (content is not SettingsPage)
                    {
                        QueueGlassTintPass(content, 3);
                    }
                }

                HideStartupOverlay();
            }
            catch { }
        }

        private async void HideStartupOverlay()
        {
            if (_startupOverlayHidden)
                return;

            _startupOverlayHidden = true;

            await Task.Delay(TimeSpan.FromSeconds(3));

            try
            {
                if (StartupOverlay != null)
                {
                    StartupOverlay.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void QueueGlassTintPass(FrameworkElement content, int remainingPasses)
        {
            if (remainingPasses <= 0)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ApplyGlassTintToElementTree(content, _currentGlassTint);
                    QueueGlassTintPass(content, remainingPasses - 1);
                }
                catch { }
            });
        }

        private void ApplyGlassTintToElementTree(DependencyObject element, global::Windows.UI.Color tint)
        {
            if (IsWebViewElement(element))
                return;

            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.RequestedTheme = _currentAppTheme;
                ApplyGlassTintToResourceDictionary(frameworkElement.Resources, tint);
                TintElementBrushes(frameworkElement, tint);
            }

            var childCount = VisualTreeHelper.GetChildrenCount(element);
            for (var i = 0; i < childCount; i++)
            {
                ApplyGlassTintToElementTree(VisualTreeHelper.GetChild(element, i), tint);
            }
        }

        private void ApplyGlassTintToResourceDictionary(ResourceDictionary resources, global::Windows.UI.Color tint)
        {
            var useLightTheme = GetEffectiveAppTheme() == ElementTheme.Light;
            var panel = useLightTheme ? WithAlpha(MixWithWhite(tint, 210), 218) : WithAlpha(Darken(tint, 34), 124);
            var card = useLightTheme ? WithAlpha(MixWithWhite(tint, 230), 190) : WithAlpha(Darken(tint, 16), 88);
            var border = useLightTheme ? WithAlpha(Darken(tint, 24), 60) : WithAlpha(ZinkCyanAccent, 88);
            var strong = useLightTheme ? WithAlpha(MixWithWhite(tint, 200), 235) : WithAlpha(global::Windows.UI.Color.FromArgb(255, 0, 8, 20), 188);

            SetResourceBrush(resources, "GlassPanelBrush", panel, tint);
            SetResourceBrush(resources, "GlassPanelStrongBrush", strong, useLightTheme ? MixWithWhite(tint, 200) : Darken(tint, 12));
            SetResourceBrush(resources, "GlassPanelBorderBrush", border, useLightTheme ? Darken(tint, 24) : Lighten(tint, 98));
            SetResourceBrush(resources, "GlassStrongBrush", strong, useLightTheme ? MixWithWhite(tint, 200) : Darken(tint, 28));
            SetResourceBrush(resources, "GlassCardBrush", card, tint);
            SetResourceBrush(resources, "GlassTileBrush", useLightTheme ? WithAlpha(MixWithWhite(tint, 228), 170) : WithAlpha(Darken(tint, 18), 82), tint);
            SetResourceBrush(resources, "GlassTileHoverBrush", useLightTheme ? WithAlpha(tint, 34) : WithAlpha(ZinkCyanAccent, 72), useLightTheme ? tint : ZinkCyanAccent);
            SetResourceBrush(resources, "GlassBorderBrush", border, useLightTheme ? Darken(tint, 24) : Lighten(tint, 96));
            SetResourceBrush(resources, "GlassBorderStrongBrush", useLightTheme ? WithAlpha(Darken(tint, 38), 96) : WithAlpha(Lighten(tint, 112), 108), useLightTheme ? Darken(tint, 38) : Lighten(tint, 112));
            SetResourceBrush(resources, "ThemePanelBrush", panel, tint);
            SetResourceBrush(resources, "ThemeCardBrush", card, tint);
            SetResourceTextBrush(resources, "MutedTextBrush", GetTintedTextColor(tint, TextBrushRole.Muted, useLightTheme));
            SetResourceTextBrush(resources, "DimTextBrush", GetTintedTextColor(tint, TextBrushRole.Dim, useLightTheme));
            SetResourceTextBrush(resources, "SubtleTextBrush", GetTintedTextColor(tint, TextBrushRole.Dim, useLightTheme));
            SetResourceTextBrush(resources, "PrimaryTextBrush", GetTintedTextColor(tint, TextBrushRole.Primary, useLightTheme));
        }

        private void TintElementBrushes(FrameworkElement element, global::Windows.UI.Color tint)
        {
            try
            {
                var useLightTheme = GetEffectiveAppTheme() == ElementTheme.Light;

                switch (element)
                {
                    case TextBlock textBlock:
                        textBlock.Foreground = TintTextBrush(textBlock.Foreground, tint, TextBrushRole.Primary, useLightTheme);
                        break;
                    case RichTextBlock richTextBlock:
                        richTextBlock.Foreground = TintTextBrush(richTextBlock.Foreground, tint, TextBrushRole.Primary, useLightTheme);
                        break;
                    case FontIcon fontIcon:
                        fontIcon.Foreground = TintTextBrush(fontIcon.Foreground, tint, TextBrushRole.Primary, useLightTheme);
                        break;
                    case Grid grid:
                        grid.Background = TintBrush(grid.Background, tint, GlassBrushRole.Surface, useLightTheme);
                        break;
                    case StackPanel stackPanel:
                        stackPanel.Background = TintBrush(stackPanel.Background, tint, GlassBrushRole.Surface, useLightTheme);
                        break;
                    case Panel panel:
                        panel.Background = TintBrush(panel.Background, tint, GlassBrushRole.Surface, useLightTheme);
                        break;
                    case Border border:
                        border.Background = TintBrush(border.Background, tint, GlassBrushRole.Surface, useLightTheme);
                        border.BorderBrush = TintBrush(border.BorderBrush, tint, GlassBrushRole.Border, useLightTheme);
                        break;
                    case Button:
                        break;
                    case Control control:
                        control.Background = TintBrush(control.Background, tint, GlassBrushRole.Control, useLightTheme);
                        control.BorderBrush = TintBrush(control.BorderBrush, tint, GlassBrushRole.Border, useLightTheme);
                        control.Foreground = TintTextBrush(control.Foreground, tint, TextBrushRole.Primary, useLightTheme);
                        break;
                    case Microsoft.UI.Xaml.Shapes.Shape shape:
                        shape.Fill = TintBrush(shape.Fill, tint, GlassBrushRole.Surface, useLightTheme);
                        shape.Stroke = TintBrush(shape.Stroke, tint, GlassBrushRole.Border, useLightTheme);
                        break;
                }
            }
            catch { }
        }

        private enum GlassBrushRole
        {
            Surface,
            Control,
            Border
        }

        private enum TextBrushRole
        {
            Primary,
            Muted,
            Dim
        }

        private static Brush TintBrush(Brush brush, global::Windows.UI.Color tint, GlassBrushRole role, bool useLightTheme)
        {
            try
            {
                switch (brush)
                {
                    case null:
                        return brush;
                    case SolidColorBrush solidBrush when solidBrush.Color.A == 0:
                        return brush;
                    case SolidColorBrush solidBrush when IsGlassLikeColor(solidBrush.Color):
                        solidBrush.Color = role switch
                        {
                            GlassBrushRole.Border => useLightTheme
                                ? WithAlpha(Darken(tint, 24), Math.Max((byte)48, solidBrush.Color.A))
                                : WithAlpha(ZinkCyanAccent, 82),
                            GlassBrushRole.Control => useLightTheme
                                ? WithAlpha(MixWithWhite(tint, 224), Math.Max((byte)45, solidBrush.Color.A))
                                : WithAlpha(Darken(tint, 8), 92),
                            _ => useLightTheme
                                ? WithAlpha(MixWithWhite(tint, 230), Math.Max((byte)70, solidBrush.Color.A))
                                : WithAlpha(Darken(tint, 36), 108)
                        };
                        return brush;
                    case AcrylicBrush acrylicBrush:
                        acrylicBrush.TintColor = useLightTheme ? MixWithWhite(tint, 220) : tint;
                        acrylicBrush.FallbackColor = WithAlpha(
                            useLightTheme ? MixWithWhite(tint, 230) : tint,
                            Math.Max((byte)160, acrylicBrush.FallbackColor.A));
                        return brush;
                    case LinearGradientBrush gradientBrush:
                        TintGradientStops(gradientBrush, tint, useLightTheme);
                        return brush;
                }
            }
            catch { }

            return brush;
        }

        private static Brush TintTextBrush(Brush brush, global::Windows.UI.Color tint, TextBrushRole fallbackRole, bool useLightTheme)
        {
            try
            {
                if (brush is not SolidColorBrush solidBrush)
                    return new SolidColorBrush(GetTintedTextColor(tint, fallbackRole, useLightTheme));

                if (solidBrush.Color.A == 0)
                    return brush;

                var role = IsTextLikeColor(solidBrush.Color)
                    ? GetTextRole(solidBrush.Color)
                    : fallbackRole;

                return new SolidColorBrush(GetTintedTextColor(tint, role, useLightTheme));
            }
            catch { }

            return brush;
        }

        private static void TintGradientStops(LinearGradientBrush gradientBrush, global::Windows.UI.Color tint, bool useLightTheme)
        {
            try
            {
                for (var i = 0; i < gradientBrush.GradientStops.Count; i++)
                {
                    var stop = gradientBrush.GradientStops[i];
                    if (!IsGlassLikeColor(stop.Color))
                        continue;

                    stop.Color = i switch
                    {
                        0 => WithAlpha(useLightTheme ? MixWithWhite(tint, 238) : Darken(tint, 112), stop.Color.A),
                        1 => WithAlpha(useLightTheme ? MixWithWhite(tint, 220) : Darken(tint, 52), stop.Color.A),
                        _ => WithAlpha(useLightTheme ? MixWithWhite(tint, 246) : Darken(tint, 126), stop.Color.A)
                    };
                }
            }
            catch { }
        }

        private static bool IsGlassLikeColor(global::Windows.UI.Color color)
        {
            if (color.A == 0)
                return false;

            if (color.A < 255)
                return true;

            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            var min = Math.Min(color.R, Math.Min(color.G, color.B));

            return max <= 64 || (max - min <= 24 && max >= 210);
        }

        private static bool IsTextLikeColor(global::Windows.UI.Color color)
        {
            if (color.A == 0)
                return false;

            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            var min = Math.Min(color.R, Math.Min(color.G, color.B));

            return max >= 120 && max - min <= 70;
        }

        private static TextBrushRole GetTextRole(global::Windows.UI.Color color)
        {
            if (color.A < 170)
                return TextBrushRole.Dim;

            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            if (max < 215 || color.A < 225)
                return TextBrushRole.Muted;

            return TextBrushRole.Primary;
        }

        private static global::Windows.UI.Color GetTintedTextColor(global::Windows.UI.Color tint, TextBrushRole role, bool useLightTheme)
        {
            if (useLightTheme)
            {
                return role switch
                {
                    TextBrushRole.Dim => WithAlpha(MixWithBlack(tint, 120), 175),
                    TextBrushRole.Muted => WithAlpha(MixWithBlack(tint, 150), 220),
                    _ => WithAlpha(MixWithBlack(tint, 190), 255)
                };
            }

            return role switch
            {
                TextBrushRole.Dim => WithAlpha(MixWithWhite(tint, 112), 165),
                TextBrushRole.Muted => WithAlpha(MixWithWhite(tint, 136), 220),
                _ => WithAlpha(MixWithWhite(tint, 168), 255)
            };
        }

        private static global::Windows.UI.Color MixWithWhite(global::Windows.UI.Color color, byte whiteAmount)
        {
            var keep = 255 - whiteAmount;
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Min(255, ((color.R * keep) + (255 * whiteAmount)) / 255),
                (byte)Math.Min(255, ((color.G * keep) + (255 * whiteAmount)) / 255),
                (byte)Math.Min(255, ((color.B * keep) + (255 * whiteAmount)) / 255));
        }

        private static global::Windows.UI.Color MixWithBlack(global::Windows.UI.Color color, byte blackAmount)
        {
            var keep = 255 - blackAmount;
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Max(0, (color.R * keep) / 255),
                (byte)Math.Max(0, (color.G * keep) / 255),
                (byte)Math.Max(0, (color.B * keep) / 255));
        }

        private static void SetResourceBrush(
            ResourceDictionary resources,
            string key,
            global::Windows.UI.Color color,
            global::Windows.UI.Color tint)
        {
            try
            {
                if (!resources.TryGetValue(key, out var value))
                    return;

                if (value is SolidColorBrush solidBrush)
                {
                    solidBrush.Color = color;
                    return;
                }

                if (value is AcrylicBrush acrylicBrush)
                {
                    acrylicBrush.TintColor = tint;
                    acrylicBrush.FallbackColor = color;
                }
            }
            catch { }
        }

        private static void SetResourceTextBrush(
            ResourceDictionary resources,
            string key,
            global::Windows.UI.Color color)
        {
            try
            {
                if (resources.TryGetValue(key, out var value) &&
                    value is SolidColorBrush solidBrush)
                {
                    solidBrush.Color = color;
                }
            }
            catch { }
        }

        private void ApplySidebarItemTextColor(global::Windows.UI.Color color)
        {
            try
            {
                var brush = new SolidColorBrush(color);
                ApplySidebarItemTextColor(SidebarNav.MenuItems, brush);
                ApplySidebarItemTextColor(SidebarNav.FooterMenuItems, brush);
            }
            catch { }
        }

        private void ApplySidebarItemTextColor(System.Collections.Generic.IList<object> items, Brush brush)
        {
            try
            {
                foreach (var item in items)
                {
                    switch (item)
                    {
                        case NavigationViewItem navItem:
                            navItem.Foreground = brush;

                            if (navItem.Icon is FontIcon fontIcon)
                            {
                                fontIcon.ClearValue(FontIcon.ForegroundProperty);
                            }

                            if (navItem.MenuItems != null && navItem.MenuItems.Count > 0)
                            {
                                ApplySidebarItemTextColor(navItem.MenuItems, brush);
                            }
                            break;

                        case NavigationViewItemHeader header:
                            header.Foreground = brush;
                            break;
                    }
                }
            }
            catch { }
        }

        private static bool IsWebViewElement(DependencyObject root)
        {
            try
            {
                var typeName = root.GetType().FullName ?? root.GetType().Name;
                if (typeName.Contains("WebView", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            return false;
        }

        private void SetBrushColor(string resourceKey, global::Windows.UI.Color color)
        {
            try
            {
                if (Application.Current.Resources.TryGetValue(resourceKey, out var appValue) &&
                    appValue is SolidColorBrush appBrush)
                {
                    appBrush.Color = color;
                }

                if (RootGrid.Resources.TryGetValue(resourceKey, out var shellValue) &&
                    shellValue is SolidColorBrush shellBrush)
                {
                    shellBrush.Color = color;
                }
            }
            catch { }
        }

        private static global::Windows.UI.Color WithAlpha(global::Windows.UI.Color color, byte alpha)
        {
            return global::Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static global::Windows.UI.Color Lighten(global::Windows.UI.Color color, byte amount)
        {
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Min(255, color.R + amount),
                (byte)Math.Min(255, color.G + amount),
                (byte)Math.Min(255, color.B + amount));
        }

        private static global::Windows.UI.Color Darken(global::Windows.UI.Color color, byte amount)
        {
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Max(0, color.R - amount),
                (byte)Math.Max(0, color.G - amount),
                (byte)Math.Max(0, color.B - amount));
        }

        private static string ColorToHex(global::Windows.UI.Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static bool TryParseHexColor(string? hex, out global::Windows.UI.Color color)
        {
            color = DefaultGlassTint;

            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var value = hex.Trim().TrimStart('#');
            if (value.Length == 8)
                value = value.Substring(2);

            if (value.Length != 6)
                return false;

            if (!byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return false;
            }

            color = global::Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }

        private static bool IsLegacyDefaultGlassTint(global::Windows.UI.Color color)
        {
            return color.R == LegacyDefaultGlassTint.R &&
                color.G == LegacyDefaultGlassTint.G &&
                color.B == LegacyDefaultGlassTint.B ||
                color.R == PreviousEmeraldDefaultGlassTint.R &&
                color.G == PreviousEmeraldDefaultGlassTint.G &&
                color.B == PreviousEmeraldDefaultGlassTint.B ||
                color.R == PreviousNeonDefaultGlassTint.R &&
                color.G == PreviousNeonDefaultGlassTint.G &&
                color.B == PreviousNeonDefaultGlassTint.B;
        }

        private void ContentFrame_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            var navigatingFrame = (Frame)sender;
            if (e.SourcePageType != typeof(SocialLockedPage) &&
                e.SourcePageType?.Namespace == typeof(SocialLockedPage).Namespace)
            {
                e.Cancel = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (navigatingFrame.CurrentSourcePageType != typeof(SocialLockedPage))
                        navigatingFrame.Navigate(typeof(SocialLockedPage));
                });
                return;
            }

            if (navigatingFrame.Content is DependencyObject outgoingPage)
            {
                // Keep Spotify's authenticated WebView alive while using its widget.
                // Closing it here stops playback and destroys the controller target.
                if (outgoingPage is SpotifyLoginPage)
                    return;

                CloseWebViews(outgoingPage);
            }
        }

        private void CloseWebViews(DependencyObject root)
        {
            if (root is YouTubePage youtubePage)
            {
                youtubePage.StopWebView();
                return;
            }

            if (root is WebView2 webView)
            {
                _discordPresenceWebViews.Remove(webView);

                try { webView.CoreWebView2?.Stop(); } catch { }
                try { webView.CoreWebView2?.Navigate("about:blank"); } catch { }
                try { webView.Close(); } catch { }

                return;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                CloseWebViews(VisualTreeHelper.GetChild(root, i));
            }
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (!ReferenceEquals(sender, ContentFrame)) return;
            try
            {
                UpdateSelectedTabTitle();
                var t = e?.SourcePageType;
                if (t == null) return;

                if (ContentFrame.Content is FrameworkElement content)
                {
                    content.RequestedTheme = _currentAppTheme;
                    NotificationVideoGuard.TrackTree(content);
                }

                ApplyGlassTintToCurrentPage();

                try
                {
                    var tag = GetTagForPageType(t);
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        var match = FindNavItemByTag(SidebarNav.MenuItems, tag)
                                 ?? FindNavItemByTag(SidebarNav.FooterMenuItems, tag);

                        if (match != null)
                            SidebarNav.SelectedItem = match;
                    }
                }
                catch { }

                var fullName = t.FullName ?? t.Name;
                var shortName = t.Name ?? fullName;

                var title = shortName.EndsWith("Page", StringComparison.OrdinalIgnoreCase)
                    ? shortName.Substring(0, shortName.Length - 4)
                    : shortName;

                if (!string.IsNullOrWhiteSpace(fullName) &&
                    (fullName.StartsWith("Zink.Pages.", StringComparison.Ordinal) || fullName.StartsWith("Zink.", StringComparison.Ordinal)))
                {
                    ActivityHub.Record(
                        ActivityHub.ActivityKind.PageVisit,
                        title: $"Visited {title}",
                        subtitle: fullName,
                        payload: fullName,
                        watchedSeconds: 0,
                        listenedSeconds: 0,
                        imageUri: ""
                    );
                }

                UpdateDiscordPresenceForPage(t, title);
                AttachDiscordPresenceToCurrentWebViews(t, title);
            }
            catch { }
        }

        private void UpdateDiscordPresenceForPage(Type pageType, string fallbackTitle)
        {
            try
            {
                if (IsActiveCallState(NativeCallCoordinator.Instance.CurrentSession.State))
                    return;

                var tag = GetTagForPageType(pageType);
                var displayName = GetDiscordPresenceDisplayName(tag, fallbackTitle);

                switch (tag)
                {
                    case "Home":
                        DiscordPresenceService.Instance.SetAppPresence("Using the home dashboard");
                        break;

                    case "ZinkConnect":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Connect", "Using the Zink Connect browser");
                        break;
                    case "ZinkVpn":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "VPN", "Using Zink VPN");
                        break;

                    case "MusicPlayer":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Music", "Playing music in Zink");
                        break;
                    case "MusicLibrary":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Music", "Using the music library");
                        break;
                    case "YouTubeMusic":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Music", "Playing YouTube Music");
                        break;
                    case "AmazonMusic":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Music", "Playing Amazon Music");
                        break;
                    case "Spotify":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Music", "Playing Spotify");
                        break;
                    case "SpotifyWidget":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Music", "Using the Spotify widget");
                        break;
                    case "Radio":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Radio", "Playing Zink Radio");
                        break;
                    case "RadioDiscovery":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Radio", "Discovering radio stations");
                        break;
                    case "LikedRadioSongs":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Radio", "Viewing liked radio songs");
                        break;
                    case "RadioWidget":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Radio", "Using the radio widget");
                        break;
                    case "Equalizer":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Audio", "Tuning the equalizer");
                        break;
                    case "Visualizer":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Music", "Watching the music visualizer");
                        break;

                    case "VideoPlayer":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Video", "Watching a video");
                        break;
                    case "VideoLibrary":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Video", "Using the video library");
                        break;
                    case "DuplicateVideoSearcher":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Video", "Finding duplicate videos");
                        break;
                    case "Netflix":
                    case "PrimeVideo":
                    case "DisneyPlus":
                    case "ParamountPlus":
                    case "NowTV":
                    case "BBCiPlayer":
                    case "My5":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Streaming", $"Watching {displayName}");
                        break;
                    case "YouTube":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Video", "Watching YouTube");
                        break;
                    case "Twitch":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Streaming", "Watching Twitch");
                        break;

                    case "Streaming":
                        DiscordPresenceService.Instance.SetStreamingPresence("Twitch", NativeTwitchStreamingService.Instance.IsStreaming);
                        break;
                    case "YouTubeStreaming":
                        DiscordPresenceService.Instance.SetStreamingPresence("YouTube", NativeTwitchStreamingService.YouTubeInstance.IsStreaming);
                        break;
                    case "KickStreaming":
                        DiscordPresenceService.Instance.SetStreamingPresence("Kick", NativeTwitchStreamingService.KickInstance.IsStreaming);
                        break;
                    case "InstagramStreaming":
                        DiscordPresenceService.Instance.SetStreamingPresence("Instagram", NativeTwitchStreamingService.InstagramInstance.IsStreaming);
                        break;
                    case "TikTokStreaming":
                        DiscordPresenceService.Instance.SetStreamingPresence("TikTok", NativeTwitchStreamingService.TikTokInstance.IsStreaming);
                        break;
                    case "FacebookStreaming":
                        DiscordPresenceService.Instance.SetStreamingPresence("Facebook Live", NativeTwitchStreamingService.FacebookInstance.IsStreaming);
                        break;
                    case "XStreaming":
                        DiscordPresenceService.Instance.SetStreamingPresence("X Live", NativeTwitchStreamingService.XInstance.IsStreaming);
                        break;
                    case "ScreenRecorder":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Recording", "Setting up a screen recording");
                        break;
                    case "RecordingsLibrary":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Recording", "Viewing saved recordings");
                        break;
                    case "FpsRecorder":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Gaming", "Recording FPS and game clips");
                        break;

                    case "Discord":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Discord");
                        break;
                    case "TikTok":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Watching TikTok");
                        break;
                    case "Instagram":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Instagram");
                        break;
                    case "X":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using X");
                        break;
                    case "Facebook":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Facebook");
                        break;
                    case "Telegram":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Messaging on Telegram");
                        break;
                    case "WhatsApp":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Messaging on WhatsApp");
                        break;
                    case "Messenger":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Messaging on Messenger");
                        break;
                    case "LinkedIn":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using LinkedIn");
                        break;
                    case "Threads":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Threads");
                        break;
                    case "Bluesky":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Bluesky");
                        break;
                    case "Mastodon":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Mastodon");
                        break;
                    case "Pinterest":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Pinterest");
                        break;
                    case "Tumblr":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Tumblr");
                        break;
                    case "Reddit":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", "Using Reddit");
                        break;
                    case "SocialLogin":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink Social", "Signing in to Zink Social");
                        break;
                    case "SocialRegister":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink Social", "Creating a Zink Social account");
                        break;
                    case "SocialDeveloperSettings":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink Social", "Editing social developer settings");
                        break;
                    case "SocialFriends":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink Social", "Checking friends");
                        break;
                    case "SocialMessages":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink Social", "Reading messages");
                        break;
                    case "SocialFriendRequests":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink Social", "Checking friend requests");
                        break;
                    case "SocialProfile":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink Social", "Viewing a profile");
                        break;
                    case "SocialCall":
                        DiscordPresenceService.Instance.SetPagePresence("Zink Call", "Calls", "Starting a Zink call");
                        break;

                    case "Xbox":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Gaming", "Playing on Xbox");
                        break;
                    case "GeForceNow":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Cloud gaming", "Playing on GeForce NOW");
                        break;
                    case "AmazonLuna":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Cloud gaming", "Playing on Amazon Luna");
                        break;
                    case "Boosteroid":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Cloud gaming", "Playing on Boosteroid");
                        break;
                    case "ShadowPC":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Cloud gaming", "Using Shadow PC");
                        break;

                    case "FeatureTour":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink", "Exploring Zink features");
                        break;
                    case "WidgetHub":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Widgets", "Managing Zink widgets");
                        break;
                    case "Notifications":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "App", "Checking notifications");
                        break;
                    case "PrivacyPolicy":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "App", "Reading the privacy policy");
                        break;
                    case "LeaveReview":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "App", "Leaving a review for Zink");
                        break;
                    case "AppCustomization":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "App", "Customizing Zink");
                        break;
                    case "Settings":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "App", "Changing Zink settings");
                        break;
                    case "About":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "App", "Reading about Zink");
                        break;
                    case "Search":
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Search", "Searching in Zink");
                        break;

                    default:
                        DiscordPresenceService.Instance.SetPagePresence(displayName, "Zink", $"Using {displayName}");
                        break;
                }
            }
            catch { }
        }

        private void AttachDiscordPresenceToCurrentWebViews(Type pageType, string fallbackTitle)
        {
            try
            {
                if (ContentFrame.Content is not DependencyObject root)
                    return;

                var tag = GetTagForPageType(pageType);
                var displayName = GetDiscordPresenceDisplayName(tag, fallbackTitle);

                AttachDiscordPresenceToWebViews(root, tag, displayName);

                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await Task.Delay(650);
                        if (ContentFrame.Content is DependencyObject delayedRoot)
                            AttachDiscordPresenceToWebViews(delayedRoot, tag, displayName);
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void AttachDiscordPresenceToWebViews(DependencyObject root, string tag, string displayName)
        {
            try
            {
                if (root is WebView2 webView)
                {
                    AttachDiscordPresenceToWebView(webView, tag, displayName);
                }

                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++)
                {
                    AttachDiscordPresenceToWebViews(VisualTreeHelper.GetChild(root, i), tag, displayName);
                }
            }
            catch { }
        }

        private void AttachDiscordPresenceToWebView(WebView2 webView, string tag, string displayName)
        {
            try
            {
                if (!_discordPresenceWebViews.Add(webView))
                    return;

                webView.CoreWebView2Initialized += (_, __) => AttachDiscordPresenceToCoreWebView(webView, tag, displayName);
                AttachDiscordPresenceToCoreWebView(webView, tag, displayName);
            }
            catch { }
        }

        private void AttachDiscordPresenceToCoreWebView(WebView2 webView, string tag, string displayName)
        {
            try
            {
                var core = webView.CoreWebView2;
                if (core == null)
                    return;

                core.NavigationCompleted += (_, __) => UpdateDiscordPresenceFromWebView(webView, tag, displayName);
                core.DocumentTitleChanged += (_, __) => UpdateDiscordPresenceFromWebView(webView, tag, displayName);
                UpdateDiscordPresenceFromWebView(webView, tag, displayName);
            }
            catch { }
        }

        private void UpdateDiscordPresenceFromWebView(WebView2 webView, string tag, string displayName)
        {
            try
            {
                if (IsActiveCallState(NativeCallCoordinator.Instance.CurrentSession.State))
                    return;

                var core = webView.CoreWebView2;
                if (core == null)
                    return;

                var title = CleanDiscordWebTitle(core.DocumentTitle, displayName);
                if (string.IsNullOrWhiteSpace(title) || string.Equals(title, displayName, StringComparison.OrdinalIgnoreCase))
                    return;

                switch (tag)
                {
                    case "YouTube":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Video", $"Watching {title}");
                        break;
                    case "Twitch":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Streaming", $"Watching {title}");
                        break;
                    case "YouTubeMusic":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Music", $"Playing {title}");
                        break;
                    case "AmazonMusic":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Music", $"Playing {title}");
                        break;
                    case "Spotify":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Music", $"Playing {title}");
                        break;
                    case "Netflix":
                    case "PrimeVideo":
                    case "DisneyPlus":
                    case "ParamountPlus":
                    case "NowTV":
                    case "BBCiPlayer":
                    case "My5":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Streaming", $"Watching {title}");
                        break;
                    case "Xbox":
                    case "GeForceNow":
                    case "AmazonLuna":
                    case "Boosteroid":
                    case "ShadowPC":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Gaming", $"Playing {title}");
                        break;
                    case "Discord":
                    case "Telegram":
                    case "WhatsApp":
                    case "Messenger":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", $"Messaging on {title}");
                        break;
                    case "TikTok":
                    case "Instagram":
                    case "X":
                    case "Facebook":
                    case "LinkedIn":
                    case "Threads":
                    case "Bluesky":
                    case "Mastodon":
                    case "Pinterest":
                    case "Tumblr":
                    case "Reddit":
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", $"Viewing {title}");
                        break;
                    default:
                        DiscordPresenceService.Instance.SetWebPresence(displayName, "Web", $"Viewing {title}");
                        break;
                }
            }
            catch { }
        }

        private static string CleanDiscordWebTitle(string? title, string fallback)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "";

            var clean = title.Trim();
            var suffixes = new[]
            {
                " - YouTube",
                " - YouTube Music",
                " | Netflix",
                " - Netflix",
                " | Prime Video",
                " - Prime Video",
                " | Disney+",
                " - Disney+",
                " | Twitch",
                " - Twitch",
                " / X",
                " | X",
                " | Facebook",
                " - Facebook",
                " | Instagram",
                " - Instagram",
                " | TikTok",
                " - TikTok"
            };

            foreach (var suffix in suffixes)
            {
                if (clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean.Substring(0, clean.Length - suffix.Length).Trim();
                    break;
                }
            }

            if (string.Equals(clean, fallback, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clean, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return clean.Length <= 110 ? clean : clean.Substring(0, 110);
        }

        private static bool IsActiveCallState(NativeCallState state)
        {
            return state == NativeCallState.Calling ||
                   state == NativeCallState.Incoming ||
                   state == NativeCallState.Accepted ||
                   state == NativeCallState.Negotiating ||
                   state == NativeCallState.Connected;
        }

        private static bool IsStreamingPresenceTag(string tag)
        {
            return tag is "Netflix" or "PrimeVideo" or "DisneyPlus" or "ParamountPlus" or "NowTV" or "BBCiPlayer" or "My5" or "YouTube" or "Twitch";
        }

        private static bool IsMusicPresenceTag(string tag)
        {
            return tag is "MusicPlayer" or "MusicLibrary" or "YouTubeMusic" or "AmazonMusic" or "Radio" or "Spotify" or "SpotifyWidget" or "LikedRadioSongs" or "RadioWidget";
        }

        private static bool IsSocialPresenceTag(string tag)
        {
            return tag is "Discord" or "TikTok" or "Instagram" or "X" or "Facebook" or "Telegram" or "WhatsApp" or "Messenger" or "LinkedIn" or "Threads" or "Bluesky" or "Mastodon" or "Pinterest" or "Tumblr" or "Reddit" or "SocialLogin" or "SocialRegister" or "SocialDeveloperSettings" or "SocialFriends" or "SocialMessages" or "SocialFriendRequests" or "SocialProfile";
        }

        private static bool IsGamingPresenceTag(string tag)
        {
            return tag is "Xbox" or "FpsRecorder" or "GeForceNow" or "AmazonLuna" or "Boosteroid" or "ShadowPC";
        }

        private static string GetDiscordPresenceDisplayName(string tag, string fallbackTitle)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return MakeFriendlyTitle(fallbackTitle);

            return tag switch
            {
                "Home" => "Home dashboard",
                "ZinkConnect" => "Zink Connect",
                "ZinkVpn" => "Zink VPN",
                "MusicPlayer" => "Music player",
                "MusicLibrary" => "Music library",
                "YouTubeMusic" => "YouTube Music",
                "AmazonMusic" => "Amazon Music",
                "RadioDiscovery" => "Radio Discovery",
                "Equalizer" => "Equalizer",
                "Visualizer" => "Visualizer",
                "VideoPlayer" => "Video player",
                "VideoLibrary" => "Video library",
                "DuplicateVideoSearcher" => "Duplicate video searcher",
                "ScreenRecorder" => "Screen recorder",
                "Streaming" => "Streaming",
                "YouTubeStreaming" => "YouTube streaming",
                "KickStreaming" => "Kick streaming",
                "InstagramStreaming" => "Instagram streaming",
                "TikTokStreaming" => "TikTok streaming",
                "FacebookStreaming" => "Facebook Live streaming",
                "XStreaming" => "X Live streaming",
                "FpsRecorder" => "FPS recorder",
                "PrimeVideo" => "Prime Video",
                "DisneyPlus" => "Disney+",
                "ParamountPlus" => "Paramount+",
                "NowTV" => "NOW",
                "BBCiPlayer" => "BBC iPlayer",
                "My5" => "My5",
                "SpotifyWidget" => "Spotify widget",
                "LikedRadioSongs" => "Liked radio songs",
                "RadioWidget" => "Radio widget",
                "SocialLogin" => "Zink Social login",
                "SocialRegister" => "Zink Social signup",
                "SocialDeveloperSettings" => "Developer settings",
                "SocialFriends" => "Friends",
                "SocialMessages" => "Messages",
                "SocialFriendRequests" => "Friend requests",
                "SocialProfile" => "Profile",
                "SocialCall" => "Zink Call",
                "GeForceNow" => "GeForce NOW",
                "AmazonLuna" => "Amazon Luna",
                "Boosteroid" => "Boosteroid",
                "ShadowPC" => "Shadow PC",
                "FeatureTour" => "Try Zink",
                "WidgetHub" => "Widget Hub",
                "Notifications" => "Notifications",
                "PrivacyPolicy" => "Privacy policy",
                "LeaveReview" => "Leave a review",
                "AppCustomization" => "App customization",
                "Settings" => "Settings",
                "About" => "About",
                _ => MakeFriendlyTitle(tag)
            };
        }

        private static string MakeFriendlyTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Zink";

            var clean = value.EndsWith("Page", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - 4)
                : value;

            var builder = new System.Text.StringBuilder(clean.Length + 8);
            for (var i = 0; i < clean.Length; i++)
            {
                var current = clean[i];
                if (i > 0 &&
                    char.IsUpper(current) &&
                    !char.IsWhiteSpace(clean[i - 1]) &&
                    !char.IsUpper(clean[i - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        private string GetTagForPageType(Type t)
        {
            try
            {
                if (t == typeof(SearchResultsPage)) return "Search";
                if (t == typeof(HomeDashboardPage)) return "Home";
                if (t == typeof(ZinkConnectPage)) return "ZinkConnect";
                if (t == typeof(ZinkVpnPage)) return "ZinkVpn";
                if (t == typeof(ToolsPage)) return "Tools";
                if (t == typeof(MusicPlayerPage)) return "MusicPlayer";
                if (t == typeof(MusicLibraryPage)) return "MusicLibrary";
                if (t == typeof(YouTubeMusicPage)) return "YouTubeMusic";
                if (t == typeof(AmazonMusicPage)) return "AmazonMusic";
                if (t == typeof(EqualizerPage)) return "Equalizer";
                if (t == typeof(VisualizerPage)) return "Visualizer";
                if (t == typeof(VideoPlayerPage)) return "VideoPlayer";
                if (t == typeof(VideoLibraryPage)) return "VideoLibrary";
                if (t == typeof(OneDriveStreamingPage)) return "OneDriveStreaming";
                if (t == typeof(DuplicateVideoSearcherPage)) return "DuplicateVideoSearcher";
                if (t == typeof(RecorderPage)) return "ScreenRecorder";
                if (t == typeof(RecordingsLibraryPage)) return "RecordingsLibrary";
                if (t == typeof(StreamingPage)) return "Streaming";
                if (t == typeof(YouTubeStreamingPage)) return "YouTubeStreaming";
                if (t == typeof(KickStreamingPage)) return "KickStreaming";
                if (t == typeof(InstagramStreamingPage)) return "InstagramStreaming";
                if (t == typeof(TikTokStreamingPage)) return "TikTokStreaming";
                if (t == typeof(FacebookStreamingPage)) return "FacebookStreaming";
                if (t == typeof(XStreamingPage)) return "XStreaming";
                if (t == typeof(NetflixPage)) return "Netflix";
                if (t == typeof(PrimeVideoPage)) return "PrimeVideo";
                if (t == typeof(DisneyPlusPage)) return "DisneyPlus";
                if (t == typeof(ParamountPlusPage)) return "ParamountPlus";
                if (t == typeof(NowTVPage)) return "NowTV";
                if (t == typeof(BBCiPlayerPage)) return "BBCiPlayer";
                if (t == typeof(My5Page)) return "My5";
                if (t == typeof(RadioPage)) return "Radio";
                if (t == typeof(RadioDiscoveryPage)) return "RadioDiscovery";
                if (t == typeof(SpotifyLoginPage)) return "Spotify";
                if (t == typeof(SpotifyWidgetPage)) return "SpotifyWidget";
                if (t == typeof(LikedRadioSongsPage)) return "LikedRadioSongs";
                if (t == typeof(RadioWidgetPage)) return "RadioWidget";
                if (t == typeof(TwitchPage)) return "Twitch";
                if (t == typeof(DiscordPage)) return "Discord";
                if (t == typeof(YouTubePage)) return "YouTube";
                if (t == typeof(TikTokPage)) return "TikTok";
                if (t == typeof(InstagramPage)) return "Instagram";
                if (t == typeof(XPage)) return "X";
                if (t == typeof(FacebookPage)) return "Facebook";
                if (t == typeof(TelegramPage)) return "Telegram";
                if (t == typeof(WhatsAppPage)) return "WhatsApp";
                if (t == typeof(MessengerPage)) return "Messenger";
                if (t == typeof(LinkedInPage)) return "LinkedIn";
                if (t == typeof(ThreadsPage)) return "Threads";
                if (t == typeof(BlueskyPage)) return "Bluesky";
                if (t == typeof(MastodonPage)) return "Mastodon";
                if (t == typeof(PinterestPage)) return "Pinterest";
                if (t == typeof(TumblrPage)) return "Tumblr";
                if (t == typeof(RedditPage)) return "Reddit";
                if (t == typeof(LoginPage)) return "SocialLogin";
                if (t == typeof(RegisterPage)) return "SocialRegister";
                if (t == typeof(DeveloperSettingsPage)) return "SocialDeveloperSettings";
                if (t == typeof(FriendsPage)) return "SocialFriends";
                if (t == typeof(MessagesPage)) return "SocialMessages";
                if (t == typeof(FriendRequestsPage)) return "SocialFriendRequests";
                if (t == typeof(ProfilePage)) return "SocialProfile";
                if (t == typeof(CallPage)) return "SocialCall";
                if (t == typeof(XboxPage)) return "Xbox";
                if (t == typeof(FpsRecorderPage)) return "FpsRecorder";
                if (t == typeof(GeForceNowPage)) return "GeForceNow";
                if (t == typeof(AmazonLunaPage)) return "AmazonLuna";
                if (t == typeof(BoosteroidPage)) return "Boosteroid";
                if (t == typeof(ShadowPCPage)) return "ShadowPC";
                if (t == typeof(FeatureTourPage)) return "FeatureTour";
                if (t == typeof(WidgetHubPage)) return "WidgetHub";
                if (t == typeof(NotificationsPage)) return "Notifications";
                if (t == typeof(PrivacyPolicyPage)) return "PrivacyPolicy";
                if (t == typeof(ReviewPage)) return "LeaveReview";
                if (t == typeof(AppCustomizationPage)) return "AppCustomization";
                if (t == typeof(SettingsPage)) return "Settings";
                if (t == typeof(AboutPage)) return "About";
                return "";
            }
            catch { return ""; }
        }

        private NavigationViewItem? FindNavItemByTag(System.Collections.Generic.IList<object> items, string tag)
        {
            try
            {
                foreach (var obj in items)
                {
                    if (obj is NavigationViewItem nvi)
                    {
                        if (string.Equals(nvi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                            return nvi;

                        if (nvi.MenuItems != null && nvi.MenuItems.Count > 0)
                        {
                            var nested = FindNavItemByTag(nvi.MenuItems, tag);
                            if (nested != null) return nested;
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}

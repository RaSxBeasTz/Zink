using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Zink.Services;
using Microsoft.UI.Dispatching;
using System.Threading;
using Windows.Foundation;

namespace Zink
{
    public sealed partial class MainWindow
    {
        private Frame? _activeTabFrame;
        private bool _syncingTabSelection;
        private readonly Dictionary<Frame, Image> _tabPreviewImages = new();
        private readonly Dictionary<Frame, Task> _tabPreviewCaptures = new();
        private readonly Popup _tabPreviewPopup = new() { IsHitTestVisible = false };
        private CancellationTokenSource? _tabHoverCancellation;
        private DispatcherQueueTimer? _tabPreviewTimer;
        public Frame ContentFrame => _activeTabFrame ?? InitialContentFrame;

        private void InitializeNavigationTabs()
        {
            _activeTabFrame = InitialContentFrame;
            NavigationTabs.TabItems.Add(CreateNavigationTab(InitialContentFrame, "Home"));
            // Warm the active page's thumbnail after layout, rather than racing its collapse on exit.
            _tabPreviewTimer = DispatcherQueue.CreateTimer();
            _tabPreviewTimer.Interval = TimeSpan.FromSeconds(2);
            _tabPreviewTimer.Tick += async (_, _) =>
            {
                if (_windowIsActivated && !_weAreInFullscreenMode)
                    await CaptureTabPreviewAsync(ContentFrame);
            };
            RootGrid.Loaded += (_, _) => _tabPreviewTimer.Start();
            Closed += (_, _) =>
            {
                _tabPreviewTimer.Stop();
                HideTabPreview();
                _tabPreviewImages.Clear();
            };
            Activated += (_, args) =>
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated) HideTabPreview();
            };
            RootGrid.SizeChanged += (_, _) => HideTabPreview();
            NavigationTabs.SelectedIndex = 0;
            AddSidebarTabMenus(SidebarNav.MenuItems);
            AddSidebarTabMenus(SidebarNav.FooterMenuItems);
        }

        private void AddSidebarTabMenus(IList<object> items)
        {
            foreach (var entry in items)
            {
                if (entry is not NavigationViewItem item) continue;
                AddSidebarTabMenus(item.MenuItems);
                if (GetSidebarPageType(item.Tag) is not Type pageType) continue;
                var menu = item.ContextFlyout as MenuFlyout ?? new MenuFlyout();
                var open = new MenuFlyoutItem { Text = "Open in new tab" };
                open.Click += (_, _) => OpenSidebarTab(pageType);
                menu.Items.Add(open);
                item.ContextFlyout = menu;
            }
        }

        public void OpenTwitchTab(Uri address)
        {
            if (Pages.TwitchPage.TryGetTwitchPageUri(address.AbsoluteUri, out var target))
                OpenSidebarTab(typeof(Pages.TwitchPage), target);
        }

        internal void RefreshTwitchTab(Pages.TwitchPage page)
        {
            foreach (TabViewItem tab in NavigationTabs.TabItems)
            {
                if (tab.Tag is not Frame frame || !ReferenceEquals(frame.Content, page)) continue;
                tab.Header = page.TabTitle;
                if (tab.ContextFlyout != null) return;
                var duplicate = new MenuFlyoutItem { Text = "Open Twitch page in new tab" };
                duplicate.Click += (_, _) => OpenTwitchTab(page.CurrentPageUri);
                var menu = new MenuFlyout();
                menu.Items.Add(duplicate);
                tab.ContextFlyout = menu;
                return;
            }
        }

        private async void OpenSidebarTab(Type pageType, object? parameter = null)
        {
            HideTabPreview();
            if (ContentFrame.Content is Pages.TwitchPage outgoingTwitch)
                await outgoingTwitch.SetPlaybackTabActiveAsync(false);
            await CaptureTabPreviewAsync(ContentFrame);
            // Resolve the existing Social lock before navigation; a cancelled navigation must not close its tab.
            if (pageType != typeof(Pages.Social.SocialLockedPage) &&
                pageType.Namespace == typeof(Pages.Social.SocialLockedPage).Namespace)
                pageType = typeof(Pages.Social.SocialLockedPage);
            var frame = new Frame { RequestedTheme = _currentAppTheme };
            frame.Navigating += ContentFrame_Navigating;
            frame.Navigated += ContentFrame_Navigated;
            var tag = GetTagForPageType(pageType);
            var item = FindNavItemByTag(SidebarNav.MenuItems, tag) ?? FindNavItemByTag(SidebarNav.FooterMenuItems, tag);
            var tab = CreateNavigationTab(frame, item?.Content as string ?? pageType.Name.Replace("Page", ""));
            TabFrames.Children.Add(frame);
            NavigationTabs.TabItems.Add(tab);
            NavigationTabs.SelectedItem = tab;
            // Establish the target synchronously before page constructors and navigation callbacks run.
            ActivateNavigationTab(tab);
            try
            {
                if (!frame.Navigate(pageType, parameter)) CloseNavigationTab(tab);
            }
            catch
            {
                CloseNavigationTab(tab);
                throw;
            }
            UpdateTabCloseButtons();
        }

        private async void NavigationTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavigationTabs.SelectedItem is not TabViewItem tab || tab.Tag is not Frame frame)
                return;
            HideTabPreview();
            if (!ReferenceEquals(ContentFrame, frame))
            {
                if (ContentFrame.Content is Pages.TwitchPage outgoingTwitch)
                    await outgoingTwitch.SetPlaybackTabActiveAsync(false);
                await CaptureTabPreviewAsync(ContentFrame);
            }
            if (ReferenceEquals(NavigationTabs.SelectedItem, tab)) ActivateNavigationTab(tab);
        }

        private void ActivateNavigationTab(TabViewItem tab)
        {
            if (tab.Tag is not Frame frame) return;
            _activeTabFrame = frame;
            if (frame.Content is Pages.TwitchPage activeTwitch)
                _ = activeTwitch.SetPlaybackTabActiveAsync(true);
            foreach (var child in TabFrames.Children)
                child.Visibility = ReferenceEquals(child, frame) ? Visibility.Visible : Visibility.Collapsed;
            _syncingTabSelection = true;
            try
            {
                var tag = frame.CurrentSourcePageType is Type type ? GetTagForPageType(type) : null;
                SidebarNav.SelectedItem = string.IsNullOrEmpty(tag) ? null :
                    FindNavItemByTag(SidebarNav.MenuItems, tag) ?? FindNavItemByTag(SidebarNav.FooterMenuItems, tag);
            }
            finally { _syncingTabSelection = false; }
            frame.RequestedTheme = _currentAppTheme;
            if (frame.Content is FrameworkElement content) content.RequestedTheme = _currentAppTheme;
            ApplyGlassTintToCurrentPage();
        }

        private void NavigationTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
            => CloseNavigationTab(args.Tab);

        private void CloseNavigationTab(TabViewItem tab)
        {
            if (NavigationTabs.TabItems.Count <= 1 || tab.Tag is not Frame frame) return;
            HideTabPreview();
            if (ReferenceEquals(NavigationTabs.SelectedItem, tab))
            {
                var index = NavigationTabs.TabItems.IndexOf(tab);
                var next = (TabViewItem)NavigationTabs.TabItems[index == 0 ? 1 : index - 1];
                NavigationTabs.SelectedItem = next;
                ActivateNavigationTab(next);
            }
            if (frame.Content is DependencyObject page) CloseWebViews(page);
            frame.Navigating -= ContentFrame_Navigating;
            frame.Navigated -= ContentFrame_Navigated;
            _tabPreviewImages.Remove(frame);
            ToolTipService.SetToolTip(tab, null);
            TabFrames.Children.Remove(frame);
            NavigationTabs.TabItems.Remove(tab);
            UpdateTabCloseButtons();
        }

        private TabViewItem CreateNavigationTab(Frame frame, string title)
        {
            var tab = new TabViewItem { Header = title, Tag = frame, IsClosable = false,
                Style = (Style)RootGrid.Resources["GlassNavigationTabStyle"] };
            var image = new Image { Width = 280, Height = 158, Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed };
            var heading = new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
            var details = new TextBlock { Text = "Page preview appears after viewing this tab.",
                Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(heading);
            panel.Children.Add(image);
            panel.Children.Add(details);
            var card = new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1), BorderBrush = (Brush)RootGrid.Resources["SidebarGlassEdgeBrush"],
                Background = new AcrylicBrush {
                    TintColor = global::Windows.UI.Color.FromArgb(255, 18, 56, 61), TintOpacity = 0.15,
                    TintLuminosityOpacity = 0.65,
                    FallbackColor = global::Windows.UI.Color.FromArgb(255, 18, 48, 56) }, Child = panel };
            // TabView owns its built-in header tooltip, so use a separate popup for thumbnails.
            ToolTipService.SetToolTip(tab, new ToolTip { IsEnabled = false });
            tab.PointerEntered += async (_, _) =>
            {
                HideTabPreview();
                if (ToolTipService.GetToolTip(tab) is ToolTip nativeTooltip)
                {
                    nativeTooltip.IsOpen = false;
                    nativeTooltip.IsEnabled = false;
                }
                var cancellation = _tabHoverCancellation = new CancellationTokenSource();
                try
                {
                    await Task.Delay(350, cancellation.Token);
                    if (cancellation.IsCancellationRequested || !tab.IsLoaded ||
                        !NavigationTabs.TabItems.Contains(tab) || _weAreInFullscreenMode) return;
                    heading.Text = tab.Header?.ToString() ?? title;
                    details.Text = image.Source == null ? "Capturing preview..." : "Last captured view";
                    _tabPreviewPopup.XamlRoot = RootGrid.XamlRoot;
                    _tabPreviewPopup.Child = card;
                    card.RequestedTheme = RootGrid.ActualTheme;
                    var position = tab.TransformToVisual(RootGrid).TransformPoint(new Point(0, tab.ActualHeight));
                    _tabPreviewPopup.HorizontalOffset = Math.Clamp(position.X, 8, Math.Max(8, RootGrid.ActualWidth - 316));
                    _tabPreviewPopup.VerticalOffset = position.Y + 10;
                    _tabPreviewPopup.IsOpen = true;
                    if (ReferenceEquals(frame, ContentFrame)) await CaptureTabPreviewAsync(frame);
                    if (!cancellation.IsCancellationRequested)
                        details.Text = image.Source == null ? "This content cannot be captured." : "Last captured view";
                }
                catch (OperationCanceledException) { }
            };
            tab.PointerExited += (_, _) => HideTabPreview();
            tab.PointerPressed += (_, _) => HideTabPreview();
            tab.Unloaded += (_, _) => HideTabPreview();
            _tabPreviewImages[frame] = image;
            return tab;
        }

        private void HideTabPreview()
        {
            _tabHoverCancellation?.Cancel();
            _tabHoverCancellation?.Dispose();
            _tabHoverCancellation = null;
            _tabPreviewPopup.IsOpen = false;
            _tabPreviewPopup.Child = null;
        }

        private async Task CaptureTabPreviewAsync(Frame frame)
        {
            if (_tabPreviewCaptures.TryGetValue(frame, out var pending))
            {
                await pending;
                return;
            }
            if (!_tabPreviewImages.TryGetValue(frame, out var image) ||
                frame.Content == null || frame.Visibility != Visibility.Visible || frame.ActualWidth < 1 ||
                frame.ActualHeight < 1 || !frame.IsLoaded) return;
            var capture = CaptureTabPreviewCoreAsync(frame, image);
            _tabPreviewCaptures[frame] = capture;
            try { await capture; }
            finally { _tabPreviewCaptures.Remove(frame); }
        }

        private async Task CaptureTabPreviewCoreAsync(Frame frame, Image image)
        {
            var capturedPage = frame.Content;
            try
            {
                // Render only the visible page; never activate a hidden tab to generate a preview.
                ImageSource snapshot;
                var webView = FindPreviewWebView(frame);
                if (webView?.CoreWebView2 != null)
                {
                    // WebView2 is a separate surface and is excluded from XAML bitmap rendering.
                    using var stream = new InMemoryRandomAccessStream();
                    await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                    stream.Seek(0);
                    var bitmap = new BitmapImage { DecodePixelWidth = 560 };
                    await bitmap.SetSourceAsync(stream);
                    snapshot = bitmap;
                }
                else
                {
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(frame, 560, 316);
                    if (bitmap.PixelWidth == 0) return;
                    snapshot = bitmap;
                }
                if (_tabPreviewImages.ContainsKey(frame) && ReferenceEquals(capturedPage, frame.Content))
                {
                    image.Source = snapshot;
                    image.Visibility = Visibility.Visible;
                }
            }
            catch (Exception error)
            {
                DiagnosticLogService.WriteLine("Tab preview capture failed: " + error.Message);
            }
        }

        private static WebView2? FindPreviewWebView(DependencyObject root)
        {
            if (root is WebView2 webView && webView.Visibility == Visibility.Visible) return webView;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is UIElement element && element.Visibility != Visibility.Visible) continue;
                var match = FindPreviewWebView(child);
                if (match != null) return match;
            }
            return null;
        }

        private void UpdateTabCloseButtons()
        {
            foreach (TabViewItem tab in NavigationTabs.TabItems)
                tab.IsClosable = NavigationTabs.TabItems.Count > 1;
        }

        private void UpdateSelectedTabTitle()
        {
            if (NavigationTabs.SelectedItem is not TabViewItem tab || ContentFrame.CurrentSourcePageType is not Type type)
                return;
            if (_tabPreviewImages.TryGetValue(ContentFrame, out var preview))
            {
                preview.Source = null;
                preview.Visibility = Visibility.Collapsed;
            }
            var tag = GetTagForPageType(type);
            var item = FindNavItemByTag(SidebarNav.MenuItems, tag) ?? FindNavItemByTag(SidebarNav.FooterMenuItems, tag);
            tab.Header = item?.Content as string ?? (tag == "VideoLibrary" ? "Video Library" : type.Name.Replace("Page", ""));
            if (ContentFrame.Content is Pages.TwitchPage twitch) RefreshTwitchTab(twitch);
        }
    }
}

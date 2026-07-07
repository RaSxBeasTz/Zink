using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Graphics;
using Zink.Services;

namespace Zink
{
    public sealed partial class ZinkConnectBrowserWindow : Window
    {
        private const string HomeUrl = "https://www.bing.com/";
        public const string LaunchBrowserOnlySettingKey = "ZinkConnect.LaunchBrowserOnly";
        private const string BrowsingHistoryEnabledSettingKey = "ZinkConnect.BrowsingHistoryEnabled";
        private const string DeleteBrowsingDataOnCloseSettingKey = "ZinkConnect.DeleteBrowsingDataOnClose";
        private const int MaxHistoryEntries = 500;
        private static ZinkConnectBrowserWindow? _current;

        private readonly Dictionary<TabViewItem, WebView2> _tabBrowsers = new();
        private readonly Dictionary<WebView2, ZinkConnectAdBlockEngine> _adBlockEngines = new();
        private readonly ObservableCollection<BrowserHistoryEntry> _historyEntries = new();
        private CoreWebView2Environment? _environment;
        private AppWindow? _appWindow;
        private bool _loaded;
        private bool _browserFullscreen;
        private bool _ublockLoadAttempted;
        private bool _browsingHistoryEnabled = true;
        private bool _deleteBrowsingDataOnClose;
        private bool _clearingBrowsingDataOnClose;

        private ZinkConnectBrowserWindow()
        {
            InitializeComponent();
            Title = "Zink Connect";
            Root.Loaded += ZinkConnectBrowserWindow_Loaded;
            Closed += ZinkConnectBrowserWindow_Closed;
            ConfigureWindow();
            HistoryList.ItemsSource = _historyEntries;
            LoadBrowsingHistory();
            LoadSettings();
            UpdateHistoryView();
        }

        public static void ShowOrActivate()
        {
            if (_current == null)
            {
                _current = new ZinkConnectBrowserWindow();
                _current.Activate();
                _current.MaximizeBrowserWindow();
                return;
            }

            try { _current._appWindow?.Show(); } catch { }
            _current.Activate();
            _current.MaximizeBrowserWindow();
        }

        public static bool LaunchBrowserOnlyEnabled
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[LaunchBrowserOnlySettingKey] is bool enabled && enabled;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool BrowsingHistoryEnabled
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[BrowsingHistoryEnabledSettingKey] is not bool enabled || enabled;
                }
                catch
                {
                    return true;
                }
            }
        }

        private static bool DeleteBrowsingDataOnCloseEnabled
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[DeleteBrowsingDataOnCloseSettingKey] is bool enabled && enabled;
                }
                catch
                {
                    return false;
                }
            }
        }

        private async void ZinkConnectBrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
                return;

            _loaded = true;
            await CreateBrowserTabAsync(HomeUrl, true);
        }

        private void ConfigureWindow()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _appWindow = AppWindow.GetFromWindowId(winId);
                _appWindow.Title = "Zink Connect";

                var display = DisplayArea.GetFromWindowId(winId, DisplayAreaFallback.Primary);
                var work = display.WorkArea;
                _appWindow.MoveAndResize(new RectInt32
                {
                    X = work.X,
                    Y = work.Y,
                    Width = work.Width,
                    Height = work.Height
                });
            }
            catch { }
        }

        private void MaximizeBrowserWindow()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SW_MAXIMIZE);
            }
            catch { }
        }

        private async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_environment != null)
                return _environment;

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZinkConnectWebViewData");

            Directory.CreateDirectory(userDataFolder);
            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = true
            };

            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
            return _environment;
        }

        private async Task<WebView2?> CreateBrowserTabAsync(string url, bool select, bool navigate = true)
        {
            var browser = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var tab = new TabViewItem
            {
                Header = "New tab",
                IconSource = new SymbolIconSource { Symbol = Symbol.Globe },
                MaxWidth = 220
            };

            _tabBrowsers[tab] = browser;
            BrowserTabs.TabItems.Add(tab);

            if (select)
                BrowserTabs.SelectedItem = tab;

            ShowBrowserForTab(tab);
            StatusText.Text = "Starting browser...";

            try
            {
                var env = await GetEnvironmentAsync();
                await browser.EnsureCoreWebView2Async(env);
                await TryLoadUBlockOriginAsync(browser.CoreWebView2);
                ConfigureBrowser(tab, browser);
                if (navigate)
                    Navigate(browser, url);
                UpdateControls();
                return browser;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Browser failed to start: {ex.Message}";
                return null;
            }
        }

        private void ConfigureBrowser(TabViewItem tab, WebView2 browser)
        {
            var core = browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            var adBlockEngine = new ZinkConnectAdBlockEngine();
            adBlockEngine.Attach(core);
            _adBlockEngines[browser] = adBlockEngine;
            core.ContainsFullScreenElementChanged += (_, __) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    SetBrowserFullscreen(core.ContainsFullScreenElement);
                });
            };
            core.WebMessageReceived += (_, args) =>
            {
                try
                {
                    string json = args.WebMessageAsJson ?? "";
                    if (json.Contains("zink-youtube-fullscreen", StringComparison.OrdinalIgnoreCase) &&
                        json.Contains("enter", StringComparison.OrdinalIgnoreCase))
                    {
                        SetBrowserFullscreen(true);
                    }
                    else if (json.Contains("zink-youtube-fullscreen", StringComparison.OrdinalIgnoreCase) &&
                             json.Contains("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        SetBrowserFullscreen(false);
                    }
                }
                catch { }
            };
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeFullscreenScript);
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeSafeAdShieldScript);
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeHighestQualityScript);

            core.NavigationStarting += (_, args) =>
            {
                if (TryGetYouTubeWatchUrlFromEmbed(args.Uri, out var watchUrl))
                {
                    args.Cancel = true;
                    core.Navigate(watchUrl);
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(BrowserTabs.SelectedItem, tab))
                    {
                        AddressBox.Text = args.Uri ?? string.Empty;
                        StatusText.Text = "Loading...";
                    }
                    UpdateControls();
                });
            };

            core.NavigationCompleted += (_, args) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(BrowserTabs.SelectedItem, tab))
                    {
                        AddressBox.Text = core.Source ?? string.Empty;
                        StatusText.Text = args.IsSuccess
                            ? BuildReadyStatus(browser)
                            : $"Navigation failed: {args.WebErrorStatus}";
                    }
                    if (args.IsSuccess)
                        AddBrowsingHistoryEntry(core.Source, core.DocumentTitle);
                    UpdateControls();
                });

                try
                {
                    if (_adBlockEngines.TryGetValue(browser, out var engine))
                    {
                        var ignored = engine.InjectCosmeticRulesAsync(core, core.Source);
                    }
                }
                catch { }
            };

            core.DocumentTitleChanged += (_, __) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var title = core.DocumentTitle;
                    tab.Header = TrimTabTitle(string.IsNullOrWhiteSpace(title) ? "Zink Connect" : title);
                    UpdateBrowsingHistoryTitle(core.Source, title);
                });
            };

            core.SourceChanged += (_, __) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(BrowserTabs.SelectedItem, tab))
                        AddressBox.Text = core.Source ?? string.Empty;
                    UpdateControls();
                });
            };

            core.NewWindowRequested += async (_, args) =>
            {
                args.Handled = true;
                string target = NormalizeNewWindowTarget(args.Uri);
                if (target.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    target = HomeUrl;

                try
                {
                    await DispatcherQueue.EnqueueAsync(async () =>
                    {
                        await CreateBrowserTabAsync(target, true);
                    });
                }
                catch
                {
                    args.Handled = true;
                }
            };

            core.DownloadStarting += (_, args) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    StatusText.Text = $"Downloading {Path.GetFileName(args.ResultFilePath)}";
                });
            };
        }

        private async Task TryLoadUBlockOriginAsync(CoreWebView2 core)
        {
            if (_ublockLoadAttempted)
                return;

            _ublockLoadAttempted = true;

            try
            {
                string extensionPath = GetUBlockOriginExtensionPath();
                if (!Directory.Exists(extensionPath) ||
                    !File.Exists(Path.Combine(extensionPath, "manifest.json")))
                {
                    StatusText.Text = "Ready - uBlock extension not installed";
                    return;
                }

                await core.Profile.AddBrowserExtensionAsync(extensionPath);
                StatusText.Text = "Ready - uBlock Origin loaded";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ready - uBlock extension unavailable: {ex.Message}";
            }
        }

        private static string GetUBlockOriginExtensionPath()
        {
            string outputPath = Path.Combine(AppContext.BaseDirectory, "Extensions", "uBlockOrigin", "uBlock0.chromium");
            if (Directory.Exists(outputPath))
                return outputPath;

            return Path.Combine(AppContext.BaseDirectory, "Extensions", "uBlockOrigin");
        }

        private void SetBrowserFullscreen(bool fullscreen)
        {
            if (_browserFullscreen == fullscreen)
                return;

            _browserFullscreen = fullscreen;

            try
            {
                TabsRow.Height = fullscreen ? new GridLength(0) : new GridLength(40);
                ToolbarRow.Height = fullscreen ? new GridLength(0) : new GridLength(54);
                FavoritesRow.Height = fullscreen ? new GridLength(0) : new GridLength(42);
                StatusRow.Height = fullscreen ? new GridLength(0) : new GridLength(32);

                if (fullscreen)
                {
                    _appWindow?.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                else
                {
                    _appWindow?.SetPresenter(AppWindowPresenterKind.Overlapped);
                    MaximizeBrowserWindow();
                }
            }
            catch { }
        }

        private const string YouTubeFullscreenScript = @"
(() => {
    if (window.__zinkConnectYouTubeFullscreenInstalled) return;
    window.__zinkConnectYouTubeFullscreenInstalled = true;

    const post = (state) => {
        try { chrome.webview.postMessage({ type: 'zink-youtube-fullscreen', state }); } catch {}
    };

    function forcePlayerFullscreen(on) {
        try {
            document.documentElement.classList.toggle('zink-youtube-fullscreen', !!on);
            document.body.classList.toggle('zink-youtube-fullscreen', !!on);
        } catch {}
    }

    function installFullscreenBridge() {
        const style = document.createElement('style');
        style.textContent = `
            html.zink-youtube-fullscreen,
            body.zink-youtube-fullscreen {
                overflow: hidden !important;
                background: #000 !important;
            }
            html.zink-youtube-fullscreen ytd-app,
            html.zink-youtube-fullscreen #page-manager,
            html.zink-youtube-fullscreen ytd-watch-flexy,
            html.zink-youtube-fullscreen #player,
            html.zink-youtube-fullscreen #movie_player,
            html.zink-youtube-fullscreen .html5-video-container,
            html.zink-youtube-fullscreen video {
                position: fixed !important;
                inset: 0 !important;
                width: 100vw !important;
                height: 100vh !important;
                max-width: none !important;
                max-height: none !important;
                z-index: 2147483647 !important;
                background: #000 !important;
            }
            html.zink-youtube-fullscreen video {
                object-fit: contain !important;
            }
        `;
        document.documentElement.appendChild(style);

        document.addEventListener('fullscreenchange', () => {
            const on = !!document.fullscreenElement;
            forcePlayerFullscreen(on);
            post(on ? 'enter' : 'exit');
        }, true);

        document.addEventListener('click', (event) => {
            const button = event.target && event.target.closest
                ? event.target.closest('.ytp-fullscreen-button, button[title*=""Full screen""], button[aria-label*=""Full screen""]')
                : null;
            if (!button) return;

            setTimeout(() => {
                const player = document.querySelector('.html5-video-player');
                const on = !!document.fullscreenElement || !!(player && player.classList.contains('ytp-fullscreen'));
                forcePlayerFullscreen(on);
                post(on ? 'enter' : 'exit');
            }, 120);
        }, true);
    }

    installFullscreenBridge();
})();
";

        private const string YouTubeSafeAdShieldScript = @"
(() => {
    if (window.__zinkSafeYouTubeAdShieldInstalled) return;
    window.__zinkSafeYouTubeAdShieldInstalled = true;

    function isYouTube() {
        return location.hostname.includes('youtube.com');
    }

    function clickSkipButtons() {
        try {
            if (!isYouTube()) return;

            const selectors = [
                '.ytp-ad-skip-button-modern',
                '.ytp-ad-skip-button',
                '.ytp-skip-ad-button',
                '.ytp-skip-ad-button-modern',
                'button[class*=""ytp-ad-skip""]'
            ];

            for (const selector of selectors) {
                for (const button of document.querySelectorAll(selector)) {
                    try {
                        const rect = button.getBoundingClientRect();
                        if (rect.width > 0 && rect.height > 0) {
                            button.click();
                        }
                    } catch {}
                }
            }
        } catch {}
    }

    function shieldAds() {
        try {
            if (!isYouTube()) return;

            const player = document.querySelector('.html5-video-player');
            const adShowing = !!player && (
                player.classList.contains('ad-showing') ||
                player.classList.contains('ad-interrupting')
            );

            const adUi = document.querySelector(
                '.ytp-ad-player-overlay, .ytp-ad-text, .ytp-ad-preview-container, .ytp-ad-message-container, .ytp-ad-simple-ad-badge, .ytp-ad-player-overlay-layout, .ytp-ad-visit-advertiser-button, .video-ads'
            );

            clickSkipButtons();

            if (adShowing || adUi) {
                for (const selector of [
                    '.ytp-ad-player-overlay',
                    '.ytp-ad-text',
                    '.ytp-ad-preview-container',
                    '.ytp-ad-message-container',
                    '.ytp-ad-simple-ad-badge',
                    '.ytp-ad-player-overlay-layout',
                    '.ytp-ad-visit-advertiser-button',
                    '.video-ads'
                ]) {
                    for (const node of document.querySelectorAll(selector)) {
                        try { node.style.setProperty('display', 'none', 'important'); } catch {}
                    }
                }
            }
        } catch {}
    }

    document.addEventListener('click', () => {
        for (let i = 0; i < 20; i++) {
            setTimeout(shieldAds, i * 200);
        }
    }, true);

    setInterval(shieldAds, 500);
})();
";

        private const string YouTubeHighestQualityScript = @"
(() => {
    if (window.__zinkYouTubeQualityInstalled) return;
    window.__zinkYouTubeQualityInstalled = true;

    const qualityRank = [
        'highres',
        'hd2160',
        'hd1440',
        'hd1080',
        'hd720',
        'large',
        'medium',
        'small',
        'tiny',
        'auto'
    ];

    function getPlayer() {
        return document.getElementById('movie_player') ||
               document.querySelector('.html5-video-player');
    }

    function pickHighest(levels) {
        if (!levels || !levels.length) return null;
        for (const quality of qualityRank) {
            if (levels.includes(quality)) return quality;
        }
        return levels[0] || null;
    }

    function forceHighestQuality() {
        try {
            if (!location.hostname.includes('youtube.com') && !location.hostname.includes('youtube-nocookie.com')) return;

            const player = getPlayer();
            if (!player ||
                typeof player.getAvailableQualityLevels !== 'function' ||
                typeof player.setPlaybackQualityRange !== 'function') {
                return;
            }

            const levels = player.getAvailableQualityLevels();
            const best = pickHighest(levels);
            if (!best) return;

            try { player.setPlaybackQualityRange(best, best); } catch {}
            try { player.setPlaybackQuality(best); } catch {}
        } catch {}
    }

    document.addEventListener('yt-navigate-finish', () => {
        for (let i = 0; i < 20; i++) setTimeout(forceHighestQuality, i * 500);
    }, true);

    document.addEventListener('loadedmetadata', () => {
        for (let i = 0; i < 12; i++) setTimeout(forceHighestQuality, i * 500);
    }, true);

    setInterval(forceHighestQuality, 5000);
    for (let i = 0; i < 20; i++) setTimeout(forceHighestQuality, i * 500);
})();
";

        private void LoadSettings()
        {
            LaunchBrowserOnlyToggle.IsOn = LaunchBrowserOnlyEnabled;
            _browsingHistoryEnabled = BrowsingHistoryEnabled;
            BrowsingHistoryToggle.IsOn = _browsingHistoryEnabled;
            _deleteBrowsingDataOnClose = DeleteBrowsingDataOnCloseEnabled;
            DeleteBrowsingDataOnCloseToggle.IsOn = _deleteBrowsingDataOnClose;
        }

        private WebView2? CurrentBrowser()
        {
            return BrowserTabs.SelectedItem is TabViewItem tab && _tabBrowsers.TryGetValue(tab, out var browser)
                ? browser
                : null;
        }

        private void ShowBrowserForTab(TabViewItem tab)
        {
            if (!_tabBrowsers.TryGetValue(tab, out var browser))
                return;

            if (!BrowserHost.Children.Contains(browser))
            {
                BrowserHost.Children.Clear();
                BrowserHost.Children.Add(browser);
            }
        }

        private static string TrimTabTitle(string title)
        {
            title = title.Trim();
            return title.Length <= 28 ? title : title.Substring(0, 27) + "...";
        }

        private static void Navigate(WebView2 browser, string text)
        {
            var url = NormalizeAddress(text);
            browser.CoreWebView2.Navigate(url);
        }

        private void HideInternalPages()
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            HistoryPage.Visibility = Visibility.Collapsed;
        }

        private static string NormalizeAddress(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return HomeUrl;

            if (TryGetYouTubeWatchUrlFromEmbed(text, out var watchUrl))
                return watchUrl;

            if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                return absolute.ToString();
            }

            if (text.Contains('.') && !text.Contains(' '))
                return "https://" + text;

            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
        }

        private static string NormalizeNewWindowTarget(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return "about:blank";

            return TryGetYouTubeWatchUrlFromEmbed(target, out var watchUrl)
                ? watchUrl
                : target;
        }

        private static bool TryGetYouTubeWatchUrlFromEmbed(string? input, out string watchUrl)
        {
            watchUrl = string.Empty;

            if (string.IsNullOrWhiteSpace(input) ||
                !Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.Host.ToLowerInvariant();
            if (!host.Contains("youtube-nocookie.com", StringComparison.Ordinal) &&
                !host.EndsWith(".youtube.com", StringComparison.Ordinal) &&
                host != "youtube.com")
            {
                return false;
            }

            const string embedPrefix = "/embed/";
            if (!uri.AbsolutePath.StartsWith(embedPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var videoId = uri.AbsolutePath[embedPrefix.Length..].Split('/', '?', '#')[0];
            videoId = Uri.UnescapeDataString(videoId.Trim());
            if (string.IsNullOrWhiteSpace(videoId) ||
                videoId.Length > 64 ||
                videoId.Contains('/') ||
                videoId.Contains('\\'))
            {
                return false;
            }

            var builder = new UriBuilder("https://www.youtube.com/watch")
            {
                Query = "v=" + Uri.EscapeDataString(videoId) + BuildYouTubeWatchTimeParameter(uri.Query)
            };
            watchUrl = builder.Uri.ToString();
            return true;
        }

        private static string BuildYouTubeWatchTimeParameter(string query)
        {
            string? raw = GetQueryValue(query, "start");
            return int.TryParse(raw, out int seconds) && seconds > 0
                ? "&t=" + seconds + "s"
                : string.Empty;
        }

        private static async Task NavigateNewWindowFallbackAsync(WebView2 browser, string target)
        {
            await Task.Delay(500);

            try
            {
                var source = browser.CoreWebView2?.Source ?? string.Empty;
                if (string.IsNullOrWhiteSpace(source) ||
                    source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    browser.CoreWebView2?.Navigate(target);
                }
            }
            catch
            {
            }
        }

        private static bool TryGetYouTubeEmbedUrl(string? input, out string embedUrl)
        {
            embedUrl = string.Empty;

            if (string.IsNullOrWhiteSpace(input) ||
                !Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                return false;
            }

            string host = uri.Host.ToLowerInvariant();
            if (host.Contains("youtube-nocookie.com", StringComparison.Ordinal))
                return false;

            bool isYouTube =
                host == "youtube.com" ||
                host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
                host == "youtu.be";

            if (!isYouTube)
                return false;

            string? videoId = null;

            if (host == "youtu.be")
            {
                videoId = uri.AbsolutePath.Trim('/');
            }
            else if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            {
                videoId = GetQueryValue(uri.Query, "v");
            }
            else if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
            {
                videoId = uri.AbsolutePath.Substring("/shorts/".Length).Split('/', '?', '#')[0];
            }
            else if (uri.AbsolutePath.StartsWith("/live/", StringComparison.OrdinalIgnoreCase))
            {
                videoId = uri.AbsolutePath.Substring("/live/".Length).Split('/', '?', '#')[0];
            }

            if (string.IsNullOrWhiteSpace(videoId))
                return false;

            videoId = Uri.UnescapeDataString(videoId.Trim());
            if (videoId.Length > 64 || videoId.Contains('/') || videoId.Contains('\\'))
                return false;

            string start = BuildYouTubeStartParameter(uri.Query);
            embedUrl = $"https://www.youtube-nocookie.com/embed/{Uri.EscapeDataString(videoId)}?autoplay=1&rel=0&modestbranding=1&playsinline=0{start}";
            return true;
        }

        private static string? GetQueryValue(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            string trimmed = query.TrimStart('?');
            foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(parts[1].Replace("+", " "));
            }

            return null;
        }

        private static string BuildYouTubeStartParameter(string query)
        {
            string? raw = GetQueryValue(query, "t") ?? GetQueryValue(query, "start");
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            if (int.TryParse(raw.TrimEnd('s'), out int seconds) && seconds > 0)
                return $"&start={seconds}";

            return string.Empty;
        }

        private void UpdateControls()
        {
            if (SettingsPage.Visibility == Visibility.Visible)
            {
                AddressBox.Text = "zink://settings";
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
                return;
            }

            if (HistoryPage.Visibility == Visibility.Visible)
            {
                AddressBox.Text = "zink://history";
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
                return;
            }

            var core = CurrentBrowser()?.CoreWebView2;
            BackButton.IsEnabled = core?.CanGoBack == true;
            ForwardButton.IsEnabled = core?.CanGoForward == true;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            var core = CurrentBrowser()?.CoreWebView2;
            if (core?.CanGoBack == true)
                core.GoBack();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            var core = CurrentBrowser()?.CoreWebView2;
            if (core?.CanGoForward == true)
                core.GoForward();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentBrowser()?.CoreWebView2?.Reload();
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            var browser = CurrentBrowser();
            if (browser != null)
            {
                HideInternalPages();
                Navigate(browser, HomeUrl);
            }
        }

        private void GoButton_Click(object sender, RoutedEventArgs e)
        {
            var browser = CurrentBrowser();
            if (browser != null)
            {
                HideInternalPages();
                Navigate(browser, AddressBox.Text);
            }
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string url)
                return;

            HideInternalPages();
            var browser = CurrentBrowser();
            if (browser != null)
                Navigate(browser, url);
        }

        private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter)
                return;

            e.Handled = true;
            var browser = CurrentBrowser();
            if (browser != null)
            {
                HideInternalPages();
                Navigate(browser, AddressBox.Text);
            }
        }

        private async void BrowserTabs_AddTabButtonClick(TabView sender, object args)
        {
            await CreateBrowserTabAsync(HomeUrl, true);
        }

        private async void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            HideInternalPages();
            await CreateBrowserTabAsync(HomeUrl, true);
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            HistoryPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
            AddressBox.Text = "zink://settings";
            StatusText.Text = "Settings";
            UpdateControls();
        }

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            HistoryPage.Visibility = Visibility.Visible;
            AddressBox.Text = "zink://history";
            StatusText.Text = "History";
            UpdateHistoryView();
            UpdateControls();
        }

        private void LaunchBrowserOnlyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[LaunchBrowserOnlySettingKey] = LaunchBrowserOnlyToggle.IsOn;
                StatusText.Text = LaunchBrowserOnlyToggle.IsOn
                    ? "Zink Connect will open the browser and send Zink to the tray."
                    : "Zink Connect will open inside Zink normally.";
            }
            catch
            {
                StatusText.Text = "Settings could not be saved.";
            }
        }

        private void BrowsingHistoryToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                _browsingHistoryEnabled = BrowsingHistoryToggle.IsOn;
                ApplicationData.Current.LocalSettings.Values[BrowsingHistoryEnabledSettingKey] = _browsingHistoryEnabled;
                StatusText.Text = _browsingHistoryEnabled
                    ? "Zink Connect browsing history will be saved."
                    : "Zink Connect browsing history is paused.";
                UpdateHistoryView();
            }
            catch
            {
                StatusText.Text = "Settings could not be saved.";
            }
        }

        private void DeleteBrowsingDataOnCloseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                _deleteBrowsingDataOnClose = DeleteBrowsingDataOnCloseToggle.IsOn;
                ApplicationData.Current.LocalSettings.Values[DeleteBrowsingDataOnCloseSettingKey] = _deleteBrowsingDataOnClose;
                StatusText.Text = _deleteBrowsingDataOnClose
                    ? "Zink Connect browsing data will be deleted when the browser closes."
                    : "Zink Connect browsing data will stay available after the browser closes.";
            }
            catch
            {
                StatusText.Text = "Settings could not be saved.";
            }
        }

        private async void ClearBrowsingDataButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Clear all browsing data?",
                Content = "This will wipe Zink Connect history, cookies, cache, site storage, saved passwords, autofill, downloads history, and service worker data.",
                PrimaryButtonText = "Clear data",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            ClearBrowsingDataButton.IsEnabled = false;
            StatusText.Text = "Clearing browsing data...";

            try
            {
                await ClearAllBrowsingDataAsync();
                StatusText.Text = "Zink Connect browsing data was cleared.";
            }
            catch
            {
                StatusText.Text = "Browsing data could not be cleared.";
            }
            finally
            {
                ClearBrowsingDataButton.IsEnabled = true;
            }
        }

        private void BrowserTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BrowserTabs.SelectedItem is TabViewItem tab)
            {
                HideInternalPages();
                ShowBrowserForTab(tab);
            }

            var core = CurrentBrowser()?.CoreWebView2;
            AddressBox.Text = core?.Source ?? string.Empty;
            StatusText.Text = "Ready";
            UpdateControls();
        }

        private async void BrowserTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is not TabViewItem tab)
                return;

            bool closesBrowser = BrowserTabs.TabItems.Count == 1;
            if (closesBrowser)
                await ClearBrowsingDataOnCloseAsync();

            if (_tabBrowsers.TryGetValue(tab, out var browser))
            {
                _tabBrowsers.Remove(tab);
                _adBlockEngines.Remove(browser);
                if (BrowserHost.Children.Contains(browser))
                    BrowserHost.Children.Remove(browser);
                try { browser.Close(); } catch { }
            }

            BrowserTabs.TabItems.Remove(tab);

            if (BrowserTabs.TabItems.Count == 0)
                Close();
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        private const int SW_MAXIMIZE = 3;

        private string BuildReadyStatus(WebView2 browser)
        {
            if (!_adBlockEngines.TryGetValue(browser, out var engine))
                return "Ready";

            if (!engine.IsReady)
                return "Ready - ad filters loading";

            return engine.BlockedCount > 0
                ? $"Ready - blocked {engine.BlockedCount} ad requests"
                : $"Ready - {engine.NetworkRuleCount:N0} filter rules";
        }

        private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not BrowserHistoryEntry entry ||
                string.IsNullOrWhiteSpace(entry.Url))
            {
                return;
            }

            var browser = CurrentBrowser();
            if (browser == null)
                return;

            HideInternalPages();
            Navigate(browser, entry.Url);
        }

        private void AddBrowsingHistoryEntry(string? url, string? title)
        {
            if (!_browsingHistoryEnabled || !IsHttpHistoryUrl(url))
                return;

            string trimmedUrl = url!.Trim();
            for (int i = _historyEntries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_historyEntries[i].Url, trimmedUrl, StringComparison.OrdinalIgnoreCase))
                    _historyEntries.RemoveAt(i);
            }

            _historyEntries.Insert(0, new BrowserHistoryEntry
            {
                Url = trimmedUrl,
                Title = BuildHistoryTitle(title, trimmedUrl),
                VisitedUtc = DateTimeOffset.UtcNow
            });

            while (_historyEntries.Count > MaxHistoryEntries)
                _historyEntries.RemoveAt(_historyEntries.Count - 1);

            SaveBrowsingHistory();
            UpdateHistoryView();
        }

        private async Task ClearAllBrowsingDataAsync()
        {
            var profile = GetAvailableBrowserProfile();
            if (profile != null)
            {
                await profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.BrowsingHistory |
                    CoreWebView2BrowsingDataKinds.DownloadHistory |
                    CoreWebView2BrowsingDataKinds.Cookies |
                    CoreWebView2BrowsingDataKinds.AllDomStorage |
                    CoreWebView2BrowsingDataKinds.DiskCache |
                    CoreWebView2BrowsingDataKinds.GeneralAutofill |
                    CoreWebView2BrowsingDataKinds.PasswordAutosave |
                    CoreWebView2BrowsingDataKinds.ServiceWorkers);
            }

            _historyEntries.Clear();
            SaveBrowsingHistory();
            UpdateHistoryView();
        }

        private async Task ClearBrowsingDataOnCloseAsync()
        {
            if (!_deleteBrowsingDataOnClose || _clearingBrowsingDataOnClose)
                return;

            _clearingBrowsingDataOnClose = true;
            try
            {
                await ClearAllBrowsingDataAsync();
            }
            catch { }
        }

        private CoreWebView2Profile? GetAvailableBrowserProfile()
        {
            var profile = CurrentBrowser()?.CoreWebView2?.Profile;
            if (profile != null)
                return profile;

            foreach (var browser in _tabBrowsers.Values)
            {
                profile = browser.CoreWebView2?.Profile;
                if (profile != null)
                    return profile;
            }

            return null;
        }

        private async void ZinkConnectBrowserWindow_Closed(object sender, WindowEventArgs e)
        {
            await ClearBrowsingDataOnCloseAsync();
            _current = null;
        }

        private void UpdateBrowsingHistoryTitle(string? url, string? title)
        {
            if (!_browsingHistoryEnabled || string.IsNullOrWhiteSpace(title) || !IsHttpHistoryUrl(url))
                return;

            foreach (var entry in _historyEntries)
            {
                if (string.Equals(entry.Url, url!.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    string newTitle = BuildHistoryTitle(title, entry.Url);
                    if (!string.Equals(entry.Title, newTitle, StringComparison.Ordinal))
                    {
                        entry.Title = newTitle;
                        SaveBrowsingHistory();
                        HistoryList.ItemsSource = null;
                        HistoryList.ItemsSource = _historyEntries;
                    }
                    return;
                }
            }
        }

        private void LoadBrowsingHistory()
        {
            try
            {
                string path = GetBrowsingHistoryPath();
                if (!File.Exists(path))
                    return;

                var entries = JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(File.ReadAllText(path));
                if (entries == null)
                    return;

                _historyEntries.Clear();
                foreach (var entry in entries)
                {
                    if (IsHttpHistoryUrl(entry.Url))
                    {
                        entry.Title = BuildHistoryTitle(entry.Title, entry.Url);
                        _historyEntries.Add(entry);
                    }

                    if (_historyEntries.Count >= MaxHistoryEntries)
                        break;
                }
            }
            catch
            {
                StatusText.Text = "History could not be loaded.";
            }
        }

        private void SaveBrowsingHistory()
        {
            try
            {
                string path = GetBrowsingHistoryPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(_historyEntries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                StatusText.Text = "History could not be saved.";
            }
        }

        private void UpdateHistoryView()
        {
            HistoryHintText.Text = _browsingHistoryEnabled
                ? (_historyEntries.Count == 0 ? "No Zink Connect browsing history yet." : "Select a page to open it in the current tab.")
                : "Browsing history is paused. Existing history remains available here.";
        }

        private static bool IsHttpHistoryUrl(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string BuildHistoryTitle(string? title, string url)
        {
            title = (title ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;

            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
                ? uri.Host
                : url;
        }

        private static string GetBrowsingHistoryPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Zink",
                "ZinkConnect",
                "BrowsingHistory.json");
        }

        private sealed class BrowserHistoryEntry
        {
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public DateTimeOffset VisitedUtc { get; set; }
            public string VisitedText => VisitedUtc.ToLocalTime().ToString("g");
        }
    }
}

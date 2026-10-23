using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Zink
{
    public sealed partial class ZinkConnectWindow : Window
    {
        private AppWindow? _appWindow;
        private bool _initialized;
        private bool _isWebVideoFullScreen;
        private Thickness _normalBrowserPadding = new(14);
        private CornerRadius _normalBrowserShellCornerRadius = new(26);
        private Thickness _normalBrowserShellBorderThickness = new(1);
        private CoreWebView2Environment? _browserEnvironment;
        private readonly List<BrowserTab> _tabs = new();
        private BrowserTab? _activeTab;
        private readonly DispatcherTimer _fullScreenStateTimer = new();
        private bool _isCheckingFullScreenState;
        private DateTime _lastFullScreenEnterUtc = DateTime.MinValue;
        private DateTime _lastFullScreenExitUtc = DateTime.MinValue;
        private DateTime _suppressFullScreenReentryUntilUtc = DateTime.MinValue;
        private WebView2? BrowserView => _activeTab?.View;
        private static readonly HttpClient YouTubePlayerClient = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        });

        private static readonly string[] AdPatterns =
        {
            "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
            "adsystem.com", "adnxs.com", "taboola.com", "outbrain.com", "criteo.com", "scorecardresearch.com",
            "analytics.google.com", "googletagmanager.com", "facebook.net", "/ads/", "adserver", "tracking",
            "pixel.", "beacon", "sponsor", "promoted", "pagead", "pubads", "securepubads", "adtrafficquality",
            "ytimg.com/ptracking", "youtube.com/pagead", "youtube.com/api/stats/ads", "youtube.com/get_midroll",
            "youtube.com/ptracking", "youtubei.googleapis.com/youtubei/v1/player/ad_break"
        };

        private static readonly string[] VideoAdPatterns =
        {
            "videoad", "video-ad", "ad_break", "adbreak", "preroll", "midroll", "vast", "vmap",
            "doubleclick", "pubads", "ima3", "pagead", "adformat=video", "oad=",
            "youtube.com/get_video_info?adformat", "youtube.com/api/stats/ads", "youtube.com/get_midroll",
            "youtubei.googleapis.com/youtubei/v1/player/ad_break"
        };

        private static readonly string[] YouTubeMediaAdMarkers =
        {
            "&oad=", "?oad=", "&adformat=", "?adformat=", "&adurl=", "?adurl=", "&ctier=a", "?ctier=a",
            "&afv=", "?afv=", "&ad_preroll=", "?ad_preroll=", "&ad3_module=", "?ad3_module=",
            "&ad_tag=", "?ad_tag=", "&vidad=", "?vidad=", "&pltype=ad", "?pltype=ad"
        };

        private static readonly string[] RequestFilterPatterns =
        {
            "*://*.doubleclick.net/*",
            "*://*.g.doubleclick.net/*",
            "*://ad.doubleclick.net/*",
            "*://googleads.g.doubleclick.net/*",
            "*://pubads.g.doubleclick.net/*",
            "*://*.googlesyndication.com/*",
            "*://pagead2.googlesyndication.com/*",
            "*://*.googleadservices.com/*",
            "*://*.adservice.google.com/*",
            "*://*.adsystem.com/*",
            "*://*.adnxs.com/*",
            "*://*.taboola.com/*",
            "*://*.outbrain.com/*",
            "*://*.criteo.com/*",
            "*://*.scorecardresearch.com/*",
            "*://*.facebook.net/*",
            "*://*.youtube.com/pagead/*",
            "*://*.youtube.com/api/stats/ads*",
            "*://*.youtube.com/get_midroll*",
            "*://*.youtube.com/ptracking*",
            "*://youtubei.googleapis.com/youtubei/v1/player/ad_break*",
            "*://*.youtubei.googleapis.com/youtubei/v1/player/ad_break*"
        };

        private static readonly string[] MediaAllowPatterns =
        {
            "googlevideo.com/videoplayback", "youtube.com/videoplayback", "ytimg.com", "youtube.com/youtubei/v1/player",
            "youtubei.googleapis.com/youtubei/v1/player"
        };

        private static readonly string[] AddressSuggestions =
        {
            "https://www.bing.com",
            "https://www.google.com",
            "https://www.youtube.com",
            "https://www.youtube.com/shorts",
            "https://www.netflix.com",
            "https://www.spotify.com",
            "https://www.twitch.tv",
            "https://www.discord.com",
            "https://www.github.com",
            "https://www.reddit.com",
            "https://www.amazon.co.uk",
            "https://www.bbc.co.uk",
            "https://www.office.com",
            "https://outlook.live.com"
        };

        public ZinkConnectWindow()
        {
            InitializeComponent();
            Zink.Services.LocalizationService.RegisterWindow(this);
            Title = "Zink Connect";
            ConfigureWindow();
            _fullScreenStateTimer.Interval = TimeSpan.FromMilliseconds(150);
            _fullScreenStateTimer.Tick += FullScreenStateTimer_Tick;
            _fullScreenStateTimer.Start();
            _ = InitializeBrowserAsync();
        }

        public void ShowBrowser()
        {
            Activate();
            _appWindow?.Show();
            MaximizeBrowserWindow();
        }

        private void ConfigureWindow()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var id = Win32Interop.GetWindowIdFromWindow(hwnd);
                _appWindow = AppWindow.GetFromWindowId(id);
                _appWindow.Title = "Zink Connect";
                _appWindow.Resize(new SizeInt32(1440, 900));
                MaximizeBrowserWindow();
            }
            catch
            {
            }
        }

        private void MaximizeBrowserWindow()
        {
            try
            {
                if (_appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }
            }
            catch
            {
            }
        }

        private async Task InitializeBrowserAsync()
        {
            if (_initialized)
                return;

            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;

                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZinkConnectChromium");

                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-features=InterestCohort,PrivacySandboxAdsAPIs,AdInterestGroupAPI " +
                        "--autoplay-policy=no-user-gesture-required"
                };

                _browserEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
                _initialized = true;
                await AddNewTabAsync(new Uri("https://www.bing.com"));
            }
            catch
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                throw;
            }
        }

        private async Task<BrowserTab?> AddNewTabAsync(Uri? source = null, bool navigate = true)
        {
            if (_browserEnvironment == null)
                return null;

            var tab = new BrowserTab
            {
                Title = "New tab",
                Source = source?.ToString() ?? "https://www.bing.com",
                View = new WebView2
                {
                    Visibility = Visibility.Collapsed,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                }
            };

            _tabs.Add(tab);
            BrowserHost.Children.Add(tab.View);
            await ConfigureTabBrowserAsync(tab);
            ActivateTab(tab);
            if (navigate)
                tab.View.CoreWebView2.Navigate(tab.Source);

            return tab;
        }

        private async Task ConfigureTabBrowserAsync(BrowserTab tab)
        {
            await tab.View.EnsureCoreWebView2Async(_browserEnvironment);

            var settings = tab.View.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = true;
            settings.AreDevToolsEnabled = true;
            settings.IsStatusBarEnabled = true;
            settings.IsWebMessageEnabled = true;
            settings.AreHostObjectsAllowed = false;
            settings.IsGeneralAutofillEnabled = true;
            settings.IsPasswordAutosaveEnabled = false;

            AddRequestFilters(tab.View.CoreWebView2);
            tab.View.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
            tab.View.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            tab.View.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            tab.View.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;
            tab.View.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
            tab.View.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
            tab.View.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            tab.View.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;
            tab.View.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            await InstallYouTubeCleanupScriptAsync(tab.View.CoreWebView2);
            await InstallFullScreenBridgeScriptAsync(tab.View.CoreWebView2);
            await Services.OneDriveVideoPlayback.InstallAsync(tab.View.CoreWebView2, fullscreen =>
            {
                if (ReferenceEquals(_activeTab, tab))
                    SetWebVideoFullScreen(fullscreen, allowImmediateExit: true);
            });
            await InstallSiteHistoryBridgeScriptAsync(tab.View.CoreWebView2);
            await UpdateDocumentStartBlockerStateAsync(tab.View.CoreWebView2);
            await UpdatePageAdBlockingStateAsync(tab.View.CoreWebView2);
            await UpdateDevToolsAdBlockingAsync(tab.View.CoreWebView2);
        }

        private void ActivateTab(BrowserTab tab)
        {
            if (_isWebVideoFullScreen)
                SetWebVideoFullScreen(false);

            foreach (var item in _tabs)
                item.View.Visibility = item == tab ? Visibility.Visible : Visibility.Collapsed;

            _activeTab = tab;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            AddressBox.Text = tab.View.CoreWebView2?.Source ?? tab.Source;
            if (!string.IsNullOrWhiteSpace(tab.Title))
                Title = $"{tab.Title} - Zink Connect";

            UpdateNavigationButtons();
            RenderTabs();
        }

        private void RenderTabs()
        {
            TabStripPanel.Children.Clear();

            foreach (var tab in _tabs)
            {
                var container = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var tabButton = new Button
                {
                    Tag = tab,
                    MinHeight = 34,
                    MinWidth = 150,
                    MaxWidth = 220,
                    Padding = new Thickness(12, 5, 10, 5),
                    CornerRadius = new CornerRadius(16),
                    Background = new SolidColorBrush(tab == _activeTab ? ColorHelper.FromArgb(82, 103, 247, 243) : ColorHelper.FromArgb(28, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(tab == _activeTab ? ColorHelper.FromArgb(132, 103, 247, 243) : ColorHelper.FromArgb(54, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Colors.White),
                    Content = new TextBlock
                    {
                        Text = GetTabDisplayName(tab),
                        MaxWidth = 170,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                tabButton.Click += TabButton_Click;

                var closeButton = new Button
                {
                    Tag = tab,
                    MinWidth = 30,
                    MinHeight = 30,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(15),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(22, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Colors.White),
                    Content = new FontIcon
                    {
                        Glyph = "\uE711",
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = 10
                    }
                };
                closeButton.Click += CloseTabButton_Click;

                container.Children.Add(tabButton);
                container.Children.Add(closeButton);
                TabStripPanel.Children.Add(container);
            }

            var newTabButton = new Button
            {
                MinWidth = 38,
                MinHeight = 34,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(ColorHelper.FromArgb(34, 103, 247, 243)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(92, 103, 247, 243)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Colors.White),
                Content = new FontIcon
                {
                    Glyph = "\uE710",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 14
                }
            };
            newTabButton.Click += NewTabButton_Click;
            TabStripPanel.Children.Add(newTabButton);
        }

        private BrowserTab? FindTab(CoreWebView2 sender)
        {
            return _tabs.FirstOrDefault(tab => tab.View.CoreWebView2 == sender);
        }

        private static string GetTabDisplayName(BrowserTab tab)
        {
            var source = tab.Source;
            if (string.IsNullOrWhiteSpace(source))
                source = tab.View.CoreWebView2?.Source;

            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                var host = uri.Host;
                return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
            }

            return string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title;
        }

        private async Task InstallYouTubeCleanupScriptAsync(CoreWebView2? webView = null)
        {
            const string script = """
(() => {
  if (window.__zinkConnectAdGuardInstalled) return;
  window.__zinkConnectAdGuardInstalled = true;
  window.__zinkConnectBlockAds = window.__zinkConnectInitialBlockAds !== false;

  try {
    let initialBlockAds = window.__zinkConnectInitialBlockAds !== false;
    Object.defineProperty(window, '__zinkConnectInitialBlockAds', {
      configurable: true,
      get: () => initialBlockAds,
      set: (value) => {
        initialBlockAds = value !== false;
        window.__zinkConnectBlockAds = initialBlockAds;
      }
    });
  } catch {}

  const isBlocking = () => window.__zinkConnectBlockAds === true;
  const adKeyPattern = /(adPlacements|playerAds|adSlots|adBreakHeartbeatParams|adSafetyReason|adSignalsInfo|adServingData|serializedAdServingData|adParams|adSlot|adBreak|adPlacement|companionAd|promoted|paidContentOverlay|auxiliaryUi|mealbar|merchShelf|playerLegacyDesktopWatchAdsRenderer|inStreamAdLayoutRenderer|adPlayerOverlay|statementBannerRenderer|playerAdsRenderer)/i;
  const adRendererPattern = /(promoted|adRenderer|displayAd|searchPyvRenderer|compactPromoted|playerLegacyDesktopWatchAdsRenderer|inStreamAdLayoutRenderer|companionAd|adSlotRenderer|adBreak)/i;

  const stripAds = (value) => {
    try {
      if (!isBlocking() || !value || typeof value !== 'object') return value;
      if (Array.isArray(value)) {
        for (let i = value.length - 1; i >= 0; i--) {
          const item = value[i];
          if (item && typeof item === 'object' && Object.keys(item).some(k => adRendererPattern.test(k))) {
            value.splice(i, 1);
          } else {
            stripAds(item);
          }
        }
        return value;
      }

      for (const key of Object.keys(value)) {
        if (adKeyPattern.test(key) || adRendererPattern.test(key)) {
          delete value[key];
          continue;
        }

        stripAds(value[key]);
      }
    } catch {}
    return value;
  };

  const defineSanitizedGlobal = (name) => {
    let stored;
    try {
      Object.defineProperty(window, name, {
        configurable: true,
        get: () => stored,
        set: (value) => { stored = stripAds(value); }
      });
    } catch {}
  };
  defineSanitizedGlobal('ytInitialPlayerResponse');
  defineSanitizedGlobal('ytInitialData');

  const originalJsonParse = JSON.parse;
  JSON.parse = function(text, reviver) {
    const parsed = originalJsonParse.call(this, text, reviver);
    return stripAds(parsed);
  };

  const mayContainYouTubeAds = (text) =>
    typeof text === 'string' &&
    /adPlacements|playerAds|adSlots|adBreak|adPlacement|playerLegacyDesktopWatchAdsRenderer|inStreamAdLayoutRenderer|adSlotRenderer|promoted|companionAd/i.test(text);

  const originalFetch = window.fetch;
  if (originalFetch) {
    window.fetch = async (...args) => {
      const response = await originalFetch(...args);
      try {
        const url = response?.url || String(args?.[0]?.url || args?.[0] || '');
        if (!isBlocking() || (!url.includes('/youtubei/v1/player') && !url.includes('/youtubei/v1/next'))) return response;
        const clone = response.clone();
        const text = await clone.text();
        if (!mayContainYouTubeAds(text)) return response;
        const data = originalJsonParse.call(JSON, text);
        stripAds(data);
        const headers = new Headers(response.headers);
        headers.set('content-type', 'application/json; charset=utf-8');
        return new Response(JSON.stringify(data), {
          status: response.status,
          statusText: response.statusText,
          headers
        });
      } catch {
        return response;
      }
    };
  }

  const originalXhrOpen = XMLHttpRequest.prototype.open;
  const originalXhrSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function(method, url, ...rest) {
    const value = String(url || '');
    this.__zinkConnectPlayerRequest = value.includes('/youtubei/v1/player') || value.includes('/youtubei/v1/next');
    return originalXhrOpen.call(this, method, url, ...rest);
  };
  XMLHttpRequest.prototype.send = function(...args) {
    if (this.__zinkConnectPlayerRequest) {
      this.addEventListener('readystatechange', () => {
        if (this.readyState !== 4 || !isBlocking()) return;
        try {
          if (!mayContainYouTubeAds(this.responseText)) return;
          const data = originalJsonParse.call(JSON, this.responseText);
          const cleaned = JSON.stringify(stripAds(data));
          Object.defineProperty(this, 'responseText', { configurable: true, get: () => cleaned });
          Object.defineProperty(this, 'response', { configurable: true, get: () => cleaned });
        } catch {}
      });
    }
    return originalXhrSend.apply(this, args);
  };

  let lastNormalRate = 1;
  let wasAdShowing = false;
  let lastAdJumpAt = 0;
  let lastAdRecoveryAt = 0;
  const adSelectors = [
    '.ytp-ad-overlay-container',
    '.ytp-ad-player-overlay',
    '.video-ads',
    '.ytp-ad-module',
    'ytd-promoted-sparkles-web-renderer',
    'ytd-display-ad-renderer',
    'ytd-ad-slot-renderer',
    'ytd-promoted-video-renderer',
    'ytd-compact-promoted-video-renderer',
    'ytd-player-legacy-desktop-watch-ads-renderer',
    'ytd-action-companion-ad-renderer',
    'ytd-companion-slot-renderer',
    'ytd-in-feed-ad-layout-renderer'
  ];

  const removeAdElements = () => {
    for (const selector of adSelectors) {
      try {
        document.querySelectorAll(selector).forEach(e => e.remove());
      } catch {}
    }

    try {
      document.querySelectorAll('ytd-rich-item-renderer').forEach(item => {
        if (item.querySelector('ytd-ad-slot-renderer, ytd-display-ad-renderer, ytd-promoted-video-renderer')) {
          item.remove();
        }
      });
    } catch {}
  };

  const clean = () => {
    try {
      if (!isBlocking()) return;
      removeAdElements();
      const skip = document.querySelector('.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button,.ytp-skip-ad-button__button');
      if (skip) skip.click();
      [...document.querySelectorAll('button')].forEach(button => {
        const text = (button.innerText || button.ariaLabel || '').toLowerCase();
        if (text.includes('skip ad') || text.includes('skip ads') || text.includes('sponsored')) button.click();
      });
      const video = document.querySelector('video');
      const adShowing = document.querySelector('.ad-showing');
      if (video && adShowing) {
        if (video.playbackRate && video.playbackRate <= 2) lastNormalRate = video.playbackRate;
        video.muted = true;
        const now = Date.now();
        if (Number.isFinite(video.duration) && video.duration > 0 && video.duration < 180 && now - lastAdJumpAt > 700) {
          lastAdJumpAt = now;
          video.currentTime = Math.max(video.currentTime, video.duration - 0.25);
        }
        wasAdShowing = true;
      } else if (video) {
        const now = Date.now();
        if (video.playbackRate > 2) video.playbackRate = lastNormalRate || 1;
        if (wasAdShowing) {
          video.muted = false;
          video.playbackRate = lastNormalRate || 1;
          wasAdShowing = false;
          lastAdRecoveryAt = now;
        }
      }
    } catch {}
  };
  clean();
  new MutationObserver(clean).observe(document.documentElement, { childList: true, subtree: true });
  setInterval(clean, 350);
})();
""";

            webView ??= BrowserView?.CoreWebView2;
            if (webView == null)
                return;

            await webView.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }

        private static async Task InstallSiteHistoryBridgeScriptAsync(CoreWebView2 webView)
        {
            const string script = """
(() => {
  if (window.__zinkConnectSiteHistoryBridgeV2Installed) return;
  window.__zinkConnectSiteHistoryBridgeV2Installed = true;

  let lastPostedUrl = '';
  let lastPostedAt = 0;

  const postLocation = (reason) => {
    try {
      const url = location.href;
      const now = Date.now();
      if (url === lastPostedUrl && now - lastPostedAt < 250) return;
      lastPostedUrl = url;
      lastPostedAt = now;
      window.chrome?.webview?.postMessage(`zink-connect-site-history:${reason}:${url}`);
    } catch {}
  };

  const wrapHistoryMethod = (name) => {
    try {
      const original = history[name];
      if (!original || original.__zinkConnectWrapped) return;
      const wrapped = function(...args) {
        const result = original.apply(this, args);
        setTimeout(() => postLocation(name), 0);
        return result;
      };
      wrapped.__zinkConnectWrapped = true;
      history[name] = wrapped;
    } catch {}
  };

  const installHooks = () => {
    wrapHistoryMethod('pushState');
    wrapHistoryMethod('replaceState');
  };

  installHooks();
  window.addEventListener('popstate', () => postLocation('popstate'), true);
  window.addEventListener('hashchange', () => postLocation('hashchange'), true);
  document.addEventListener('yt-navigate-start', () => postLocation('yt-navigate-start'), true);
  document.addEventListener('yt-navigate-finish', () => postLocation('yt-navigate-finish'), true);
  document.addEventListener('yt-page-data-updated', () => postLocation('yt-page-data-updated'), true);
  document.addEventListener('click', () => {
    setTimeout(() => postLocation('click'), 150);
    setTimeout(() => postLocation('click-late'), 650);
  }, true);
  setInterval(() => {
    installHooks();
    postLocation('poll');
  }, 350);
  setTimeout(() => postLocation('initial'), 0);
})();
""";

            await webView.AddScriptToExecuteOnDocumentCreatedAsync(script);
            await webView.ExecuteScriptAsync(script);
        }

        private static async Task InstallFullScreenBridgeScriptAsync(CoreWebView2 webView)
        {
            const string script = """
(() => {
  if (window.__zinkConnectFullScreenBridgeV7Installed) return;
  window.__zinkConnectFullScreenBridgeV7Installed = true;

  let forcedFullScreen = false;
  let suppressAutoEnterUntil = 0;

  const hasFullScreenElement = () =>
    !!(document.fullscreenElement ||
       document.webkitFullscreenElement ||
       document.mozFullScreenElement ||
       document.msFullscreenElement);

  const postFullScreenState = () => {
    try {
      window.chrome?.webview?.postMessage(hasFullScreenElement()
        ? 'zink-connect-fullscreen:enter'
        : 'zink-connect-fullscreen:exit');
    } catch {}
  };

  const ensureFullScreenStyle = () => {
    try {
      if (document.getElementById('zink-connect-youtube-fullscreen-style')) return;
      const style = document.createElement('style');
      style.id = 'zink-connect-youtube-fullscreen-style';
      style.textContent = `
        html.zink-connect-youtube-fullscreen,
        html.zink-connect-youtube-fullscreen body {
          width: 100vw !important;
          height: 100vh !important;
          margin: 0 !important;
          overflow: hidden !important;
          background: #000 !important;
        }

        html.zink-connect-youtube-fullscreen ytd-masthead,
        html.zink-connect-youtube-fullscreen #secondary,
        html.zink-connect-youtube-fullscreen #chat,
        html.zink-connect-youtube-fullscreen #chat-container,
        html.zink-connect-youtube-fullscreen ytd-live-chat-frame,
        html.zink-connect-youtube-fullscreen #below,
        html.zink-connect-youtube-fullscreen #meta,
        html.zink-connect-youtube-fullscreen #info,
        html.zink-connect-youtube-fullscreen #comments,
        html.zink-connect-youtube-fullscreen ytd-watch-next-secondary-results-renderer {
          display: none !important;
          visibility: hidden !important;
          pointer-events: none !important;
        }

        html.zink-connect-youtube-fullscreen ytd-watch-flexy,
        html.zink-connect-youtube-fullscreen #columns,
        html.zink-connect-youtube-fullscreen #primary,
        html.zink-connect-youtube-fullscreen #player,
        html.zink-connect-youtube-fullscreen #player-container,
        html.zink-connect-youtube-fullscreen #player-container-inner,
        html.zink-connect-youtube-fullscreen #player-theater-container {
          position: fixed !important;
          inset: 0 !important;
          width: 100vw !important;
          height: 100vh !important;
          min-width: 100vw !important;
          min-height: 100vh !important;
          max-width: none !important;
          max-height: none !important;
          margin: 0 !important;
          padding: 0 !important;
          overflow: hidden !important;
          background: #000 !important;
          z-index: 2147483646 !important;
        }

        html.zink-connect-youtube-fullscreen #movie_player,
        html.zink-connect-youtube-fullscreen .html5-video-player {
          position: fixed !important;
          inset: 0 !important;
          width: 100vw !important;
          height: 100vh !important;
          max-width: none !important;
          max-height: none !important;
          z-index: 2147483647 !important;
          background: #000 !important;
        }

        html.zink-connect-youtube-fullscreen .html5-main-video,
        html.zink-connect-youtube-fullscreen video {
          position: absolute !important;
          inset: 0 !important;
          width: 100vw !important;
          height: 100vh !important;
          max-width: none !important;
          max-height: none !important;
          object-fit: contain !important;
          background: #000 !important;
        }
      `;
      document.documentElement.appendChild(style);
    } catch {}
  };

  const enterForcedFullScreen = () => {
    try {
      ensureFullScreenStyle();
      forcedFullScreen = true;
      document.documentElement.classList.add('zink-connect-youtube-fullscreen');
      window.chrome?.webview?.postMessage('zink-connect-fullscreen:enter');
    } catch {}
  };

  const exitForcedFullScreen = () => {
    try {
      forcedFullScreen = false;
      suppressAutoEnterUntil = Date.now() + 1000;
      document.documentElement.classList.remove('zink-connect-youtube-fullscreen');
      document.querySelectorAll('#movie_player,.html5-video-player').forEach(player => {
        try {
          player.classList.remove('ytp-fullscreen', 'fullscreen', 'ytp-big-mode');
          player.removeAttribute('fullscreen');
        } catch {}
      });
      try {
        if (document.fullscreenElement && document.exitFullscreen) {
          document.exitFullscreen().catch(() => {});
        }
      } catch {}
      window.chrome?.webview?.postMessage('zink-connect-fullscreen:exit');
    } catch {}
  };

  const isYouTubeFullScreenControl = (target) => {
    try {
      const element = target?.closest?.('.ytp-fullscreen-button,button,[role="button"]');
      if (!element) return false;
      const text = `${element.className || ''} ${element.title || ''} ${element.ariaLabel || ''}`.toLowerCase();
      return text.includes('ytp-fullscreen-button') || text.includes('fullscreen') || text.includes('full screen');
    } catch {
      return false;
    }
  };

  const isExitFullScreenControl = (target) => {
    try {
      const element = target?.closest?.('.ytp-fullscreen-button,button,[role="button"]');
      if (!element) return false;
      const text = `${element.className || ''} ${element.title || ''} ${element.ariaLabel || ''}`.toLowerCase();
      return text.includes('exit fullscreen') || text.includes('exit full screen');
    } catch {
      return false;
    }
  };

  const hasYouTubePlayerFullScreenState = () =>
    !!(document.querySelector('.html5-video-player.ytp-fullscreen') ||
       document.querySelector('#movie_player.ytp-fullscreen'));

  document.addEventListener('click', (event) => {
    try {
      if (!isYouTubeFullScreenControl(event.target)) return;
      if (forcedFullScreen || isExitFullScreenControl(event.target) || hasFullScreenElement()) {
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation?.();
        exitForcedFullScreen();
        return;
      }

      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      if (Date.now() >= suppressAutoEnterUntil && !forcedFullScreen && !hasFullScreenElement()) {
        enterForcedFullScreen();
      }
    } catch {}
  }, true);

  document.addEventListener('keydown', (event) => {
    try {
      const key = (event.key || '').toLowerCase();
      if (key === 'escape' && forcedFullScreen) {
        event.preventDefault();
        event.stopPropagation();
        exitForcedFullScreen();
      } else if (key === 'f' && !hasFullScreenElement()) {
        window.setTimeout(() => {
          if (Date.now() < suppressAutoEnterUntil) return;
          if (!hasFullScreenElement()) enterForcedFullScreen();
        }, 0);
      }
    } catch {}
  }, true);

  document.addEventListener('fullscreenchange', postFullScreenState, true);
  document.addEventListener('webkitfullscreenchange', postFullScreenState, true);
  document.addEventListener('mozfullscreenchange', postFullScreenState, true);
  document.addEventListener('MSFullscreenChange', postFullScreenState, true);
  window.setInterval(() => {
    try {
      if (Date.now() < suppressAutoEnterUntil) return;
      if (hasYouTubePlayerFullScreenState() && !forcedFullScreen && !hasFullScreenElement()) {
        enterForcedFullScreen();
      }
    } catch {}
  }, 250);
})();
""";

            await webView.AddScriptToExecuteOnDocumentCreatedAsync(script);
            await webView.ExecuteScriptAsync(script);
        }

        private async Task UpdatePageAdBlockingStateAsync(CoreWebView2? webView = null)
        {
            try
            {
                webView ??= BrowserView?.CoreWebView2;
                if (webView == null)
                    return;

                var enabled = IsBlockingEnabled() ? "true" : "false";
                await webView.ExecuteScriptAsync($"window.__zinkConnectBlockAds = {enabled}; localStorage.setItem('zinkConnectBlockAds', '{enabled}');");
                if (enabled == "true")
                    await RunYouTubeCleanupNowAsync();
            }
            catch
            {
            }
        }

        private async Task UpdateDocumentStartBlockerStateAsync(CoreWebView2? webView = null)
        {
            try
            {
                webView ??= BrowserView?.CoreWebView2;
                if (webView == null)
                    return;

                var enabled = IsBlockingEnabled() ? "true" : "false";
                await webView.AddScriptToExecuteOnDocumentCreatedAsync($"window.__zinkConnectInitialBlockAds = {enabled};");
            }
            catch
            {
            }
        }

        private async Task UpdateDevToolsAdBlockingAsync(CoreWebView2? webView = null)
        {
            try
            {
                webView ??= BrowserView?.CoreWebView2;
                if (webView == null)
                    return;

                await webView.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                var blockedUrls = IsBlockingEnabled()
                    ? """
{
  "urls": [
    "*://*.doubleclick.net/*",
    "*://*.g.doubleclick.net/*",
    "*://ad.doubleclick.net/*",
    "*://googleads.g.doubleclick.net/*",
    "*://pubads.g.doubleclick.net/*",
    "*://*.googlesyndication.com/*",
    "*://pagead2.googlesyndication.com/*",
    "*://*.googleadservices.com/*",
    "*://*.adservice.google.com/*",
    "*://*.youtube.com/pagead/*",
    "*://*.youtube.com/api/stats/ads*",
    "*://*.youtube.com/get_midroll*",
    "*://*.youtube.com/ptracking*",
    "*://youtubei.googleapis.com/youtubei/v1/player/ad_break*",
    "*://*.youtubei.googleapis.com/youtubei/v1/player/ad_break*"
  ]
}
"""
                    : """
{
  "urls": []
}
""";
                await webView.CallDevToolsProtocolMethodAsync("Network.setBlockedURLs", blockedUrls);
            }
            catch
            {
            }
        }

        private bool IsBlockingEnabled()
        {
            return AdBlockToggle?.IsOn == true || VideoAdBlockToggle?.IsOn == true;
        }

        private static void AddRequestFilters(CoreWebView2 webView)
        {
            foreach (var pattern in RequestFilterPatterns)
            {
                try
                {
                    webView.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
                }
                catch
                {
                }
            }
        }

        private void CoreWebView2_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                var uri = args.Request.Uri ?? string.Empty;
                if (ShouldBlock(uri, args.ResourceContext))
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(null, 204, "No Content", "Content-Type: text/plain");
                }
            }
            catch
            {
            }
        }

        private static bool IsYouTubeDataApi(string uri)
        {
            return uri.Contains("/youtubei/v1/player", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/youtubei/v1/next", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<CoreWebView2WebResourceResponse?> CreateCleanYouTubePlayerResponseAsync(
            CoreWebView2 sender,
            CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                using var outbound = new HttpRequestMessage(new HttpMethod(args.Request.Method), args.Request.Uri);

                var body = await ReadRequestBodyAsync(args.Request.Content);
                if (body.Length > 0)
                {
                    outbound.Content = new ByteArrayContent(body);
                    outbound.Content.Headers.TryAddWithoutValidation("Content-Type", SafeHeader(args.Request, "Content-Type", "application/json"));
                }

                CopyRequestHeaders(args.Request, outbound);

                using var inbound = await YouTubePlayerClient.SendAsync(outbound);
                var text = await inbound.Content.ReadAsStringAsync();
                if (!LooksLikeJson(text))
                    return null;

                var cleaned = StripYouTubeAds(text);
                var bytes = Encoding.UTF8.GetBytes(cleaned);
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }

                stream.Seek(0);
                return sender.Environment.CreateWebResourceResponse(
                    stream,
                    (int)inbound.StatusCode,
                    inbound.ReasonPhrase ?? "OK",
                    "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]> ReadRequestBodyAsync(IRandomAccessStream? source)
        {
            if (source == null)
                return Array.Empty<byte>();

            try
            {
                var size = source.Size;
                if (size <= 0 || size > int.MaxValue)
                    return Array.Empty<byte>();

                var buffer = new global::Windows.Storage.Streams.Buffer((uint)size);
                source.Seek(0);
                await source.ReadAsync(buffer, (uint)size, InputStreamOptions.None);
                var bytes = new byte[buffer.Length];
                DataReader.FromBuffer(buffer).ReadBytes(bytes);
                return bytes;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static void CopyRequestHeaders(CoreWebView2WebResourceRequest source, HttpRequestMessage destination)
        {
            foreach (var header in source.Headers)
            {
                try
                {
                    var name = header.Key;
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (destination.Content != null && name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                        destination.Content.Headers.TryAddWithoutValidation(name, header.Value);
                    else
                        destination.Headers.TryAddWithoutValidation(name, header.Value);
                }
                catch
                {
                }
            }
        }

        private static string SafeHeader(CoreWebView2WebResourceRequest source, string name, string fallback)
        {
            try
            {
                var value = source.Headers.GetHeader(name);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool LooksLikeJson(string value)
        {
            value = value.TrimStart();
            return value.StartsWith("{", StringComparison.Ordinal) || value.StartsWith("[", StringComparison.Ordinal);
        }

        private static string StripYouTubeAds(string json)
        {
            try
            {
                var node = JsonNode.Parse(json);
                RemoveYouTubeAdFields(node);
                return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? json;
            }
            catch
            {
                return json;
            }
        }

        private static void RemoveYouTubeAdFields(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    if (IsYouTubeAdJsonKey(key))
                    {
                        obj.Remove(key);
                        continue;
                    }

                    RemoveYouTubeAdFields(obj[key]);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                    RemoveYouTubeAdFields(child);
            }
        }

        private static bool IsYouTubeAdJsonKey(string key)
        {
            return key.Equals("adPlacements", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("playerAds", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adSlots", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adBreakHeartbeatParams", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adSafetyReason", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adSignalsInfo", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adServingData", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("serializedAdServingData", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adParams", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adSlot", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adBreak", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("adPlacement", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("paidContentOverlay", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("auxiliaryUi", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("adRenderer", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("adPlayer", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("mealbar", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("promoted", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("companionAd", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldBlock(string uri, CoreWebView2WebResourceContext context)
        {
            var value = uri.ToLowerInvariant();

            if (IsBlockingEnabled() && IsYouTubeAdMedia(value))
                return true;

            if (context == CoreWebView2WebResourceContext.Media &&
                MediaAllowPatterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (AdBlockToggle.IsOn && AdPatterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (VideoAdBlockToggle.IsOn &&
                VideoAdPatterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        private static bool IsYouTubeAdMedia(string value)
        {
            if (!value.Contains("googlevideo.com/videoplayback", StringComparison.OrdinalIgnoreCase) &&
                !value.Contains("youtube.com/videoplayback", StringComparison.OrdinalIgnoreCase))
                return false;

            return YouTubeMediaAdMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            var tab = FindTab(sender);
            if (tab != null)
            {
                tab.PendingSource = args.Uri;
            }

            if (tab == _activeTab)
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                AddressBox.Text = args.Uri;
                UpdateNavigationButtons();
            }

            _ = InstallFullScreenBridgeScriptAsync(sender);
            _ = InstallSiteHistoryBridgeScriptAsync(sender);
        }

        private void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            var tab = FindTab(sender);
            if (tab != null)
            {
                TrackTabSource(tab, sender.Source);
                RenderTabs();
            }

            if (tab == _activeTab)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                UpdateNavigationButtons();
            }

            _ = UpdatePageAdBlockingStateAsync();
            _ = RunYouTubeCleanupNowAsync();
        }

        private async Task RunYouTubeCleanupNowAsync()
        {
            try
            {
                if (BrowserView?.CoreWebView2 == null)
                    return;

                await BrowserView.CoreWebView2.ExecuteScriptAsync("""
(() => {
  try {
    [
      '.ytp-ad-overlay-container',
      '.ytp-ad-player-overlay',
      '.video-ads',
      '.ytp-ad-module',
      'ytd-promoted-sparkles-web-renderer',
      'ytd-display-ad-renderer',
      'ytd-ad-slot-renderer',
      'ytd-promoted-video-renderer',
      'ytd-compact-promoted-video-renderer',
      'ytd-player-legacy-desktop-watch-ads-renderer',
      'ytd-action-companion-ad-renderer',
      'ytd-companion-slot-renderer',
      'ytd-in-feed-ad-layout-renderer'
    ].forEach(selector => {
      try { document.querySelectorAll(selector).forEach(e => e.remove()); } catch {}
    });
    document.querySelectorAll('ytd-rich-item-renderer').forEach(item => {
      if (item.querySelector('ytd-ad-slot-renderer, ytd-display-ad-renderer, ytd-promoted-video-renderer')) item.remove();
    });
    const skip = document.querySelector('.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button,.ytp-skip-ad-button__button');
    if (skip) skip.click();
    [...document.querySelectorAll('button')].forEach(button => {
      const text = (button.innerText || button.ariaLabel || '').toLowerCase();
      if (text.includes('skip ad') || text.includes('skip ads') || text.includes('sponsored')) button.click();
    });
    const video = document.querySelector('video');
    if (video && document.querySelector('.ad-showing') && Number.isFinite(video.duration) && video.duration > 0 && video.duration < 180) {
      video.muted = true;
      video.currentTime = Math.max(video.currentTime, video.duration - 0.25);
    } else if (video && video.playbackRate > 2) {
      video.playbackRate = 1;
    }
  } catch {}
})();
""");
            }
            catch
            {
            }
        }

        private void CoreWebView2_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
        {
            var tab = FindTab(sender);
            if (tab != null)
            {
                TrackTabSource(tab, sender.Source);
                RenderTabs();
            }

            if (tab == _activeTab)
            {
                AddressBox.Text = sender.Source;
                UpdateNavigationButtons();
            }
        }

        private void CoreWebView2_HistoryChanged(CoreWebView2 sender, object args)
        {
            if (FindTab(sender) == _activeTab)
                UpdateNavigationButtons();
        }

        private void CoreWebView2_DocumentTitleChanged(CoreWebView2 sender, object args)
        {
            var tab = FindTab(sender);
            if (tab != null)
            {
                tab.Title = string.IsNullOrWhiteSpace(sender.DocumentTitle) ? "New tab" : sender.DocumentTitle;
                RenderTabs();
            }

            if (tab == _activeTab && !string.IsNullOrWhiteSpace(sender.DocumentTitle))
                Title = $"{sender.DocumentTitle} - Zink Connect";
        }

        private async void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;

            try
            {
                var initialUri = GetNewWindowInitialUri(args.Uri);
                await AddNewTabAsync(initialUri);
            }
            catch
            {
                args.Handled = true;
            }
        }

        private static Uri GetNewWindowInitialUri(string? uri)
        {
            if (!string.IsNullOrWhiteSpace(uri) && Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                return parsed;

            return new Uri("about:blank");
        }

        private static async Task NavigateNewTabFallbackAsync(WebView2 view, string target)
        {
            await Task.Delay(500);

            try
            {
                var source = view.CoreWebView2?.Source ?? string.Empty;
                if (string.IsNullOrWhiteSpace(source) ||
                    source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    view.CoreWebView2?.Navigate(target);
                }
            }
            catch
            {
            }
        }

        private void CoreWebView2_ContainsFullScreenElementChanged(CoreWebView2 sender, object args)
        {
            if (FindTab(sender) == _activeTab)
                SetWebVideoFullScreen(sender.ContainsFullScreenElement, allowImmediateExit: true);
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (FindTab(sender) != _activeTab)
                return;

            try
            {
                var message = args.TryGetWebMessageAsString();
                if (message.Equals("zink-connect-fullscreen:enter", StringComparison.Ordinal))
                    SetWebVideoFullScreen(true);
                else if (message.Equals("zink-connect-fullscreen:exit", StringComparison.Ordinal))
                {
                    _suppressFullScreenReentryUntilUtc = DateTime.UtcNow.AddMilliseconds(1000);
                    SetWebVideoFullScreen(false, allowImmediateExit: true);
                }
                else if (message.StartsWith("zink-connect-site-history:", StringComparison.Ordinal))
                {
                    TrackSiteHistoryMessage(sender, message);
                }
            }
            catch
            {
            }
        }

        private async void FullScreenStateTimer_Tick(object? sender, object e)
        {
            if (_isCheckingFullScreenState || BrowserView?.CoreWebView2 == null)
                return;

            _isCheckingFullScreenState = true;
            try
            {
                var result = await BrowserView.CoreWebView2.ExecuteScriptAsync("""
(() => {
  try {
    return !!(
      document.fullscreenElement ||
      document.webkitFullscreenElement ||
      document.mozFullScreenElement ||
      document.msFullscreenElement ||
      document.documentElement.classList.contains('zink-connect-youtube-fullscreen') ||
      document.querySelector('.html5-video-player.ytp-fullscreen') ||
      document.querySelector('#movie_player.ytp-fullscreen') ||
      document.querySelector('.html5-video-player.fullscreen') ||
      document.querySelector('#movie_player.fullscreen')
    );
  } catch {
    return false;
  }
})();
""");

                if (bool.TryParse(result, out var isFullScreenLike))
                {
                    if (isFullScreenLike && DateTime.UtcNow < _suppressFullScreenReentryUntilUtc)
                        return;

                    SetWebVideoFullScreen(isFullScreenLike, allowImmediateExit: false);
                }
            }
            catch
            {
            }
            finally
            {
                _isCheckingFullScreenState = false;
            }
        }

        private void SetWebVideoFullScreen(bool isFullScreen, bool allowImmediateExit = false)
        {
            var now = DateTime.UtcNow;
            if (isFullScreen)
                _lastFullScreenEnterUtc = now;

            if (!isFullScreen && !allowImmediateExit && now - _lastFullScreenEnterUtc < TimeSpan.FromMilliseconds(250))
                return;

            var presenterKind = _appWindow?.Presenter?.Kind;
            if (_isWebVideoFullScreen == isFullScreen &&
                (isFullScreen
                    ? presenterKind == AppWindowPresenterKind.FullScreen
                    : presenterKind != AppWindowPresenterKind.FullScreen))
                return;

            _isWebVideoFullScreen = isFullScreen;

            try
            {
                if (isFullScreen)
                {
                    Activate();
                    if (_appWindow == null)
                        ConfigureWindow();

                    BrowserTabsBar.Visibility = Visibility.Collapsed;
                    BrowserToolbar.Visibility = Visibility.Collapsed;
                    BrowserLayoutRoot.Padding = new Thickness(0);
                    BrowserLayoutRoot.RowSpacing = 0;
                    Grid.SetRow(BrowserShell, 0);
                    Grid.SetRowSpan(BrowserShell, 3);
                    BrowserShell.CornerRadius = new CornerRadius(0);
                    BrowserShell.BorderThickness = new Thickness(0);
                    _appWindow?.SetPresenter(AppWindowPresenterKind.FullScreen);
                    BrowserView?.Focus(FocusState.Programmatic);
                }
                else
                {
                    _lastFullScreenExitUtc = now;
                    BrowserTabsBar.Visibility = Visibility.Visible;
                    BrowserToolbar.Visibility = Visibility.Visible;
                    BrowserLayoutRoot.Padding = _normalBrowserPadding;
                    BrowserLayoutRoot.RowSpacing = 10;
                    Grid.SetRow(BrowserShell, 2);
                    Grid.SetRowSpan(BrowserShell, 1);
                    BrowserShell.CornerRadius = _normalBrowserShellCornerRadius;
                    BrowserShell.BorderThickness = _normalBrowserShellBorderThickness;
                    _appWindow?.SetPresenter(AppWindowPresenterKind.Overlapped);
                    MaximizeBrowserWindow();
                }
            }
            catch
            {
            }
        }

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: BrowserTab tab })
                ActivateTab(tab);
        }

        private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: BrowserTab tab })
                return;

            var wasActive = tab == _activeTab;
            var nextIndex = Math.Max(0, _tabs.IndexOf(tab) - 1);

            BrowserHost.Children.Remove(tab.View);
            _tabs.Remove(tab);

            if (_tabs.Count == 0)
            {
                await AddNewTabAsync(new Uri("https://www.bing.com"));
                return;
            }

            if (wasActive)
                ActivateTab(_tabs[Math.Min(nextIndex, _tabs.Count - 1)]);
            else
                RenderTabs();
        }

        private async void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            await AddNewTabAsync(new Uri("https://www.bing.com"));
        }

        private void BlockerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _ = UpdateDevToolsAdBlockingAsync();
            _ = UpdateDocumentStartBlockerStateAsync();
            _ = UpdatePageAdBlockingStateAsync();
        }

        private void GoButton_Click(object sender, RoutedEventArgs e)
        {
            Navigate(AddressBox.Text);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserView?.CoreWebView2?.CanGoBack == true)
            {
                if (_activeTab != null)
                    _activeTab.IsHistoryNavigation = true;

                BrowserView.CoreWebView2.GoBack();
                UpdateNavigationButtons();
                return;
            }

            if (_activeTab?.BackHistory.Count > 0 && BrowserView?.CoreWebView2 != null)
            {
                var target = _activeTab.BackHistory.Pop();
                var current = BrowserView.CoreWebView2.Source;
                if (!string.IsNullOrWhiteSpace(current) && !SameUrl(current, target))
                    _activeTab.ForwardHistory.Push(current);

                _activeTab.IsHistoryNavigation = true;
                BrowserView.CoreWebView2.Navigate(target);
                UpdateNavigationButtons();
                return;
            }
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserView?.CoreWebView2?.CanGoForward == true)
            {
                if (_activeTab != null)
                    _activeTab.IsHistoryNavigation = true;

                BrowserView.CoreWebView2.GoForward();
                UpdateNavigationButtons();
                return;
            }

            if (_activeTab?.ForwardHistory.Count > 0 && BrowserView?.CoreWebView2 != null)
            {
                var target = _activeTab.ForwardHistory.Pop();
                var current = BrowserView.CoreWebView2.Source;
                if (!string.IsNullOrWhiteSpace(current) && !SameUrl(current, target))
                    _activeTab.BackHistory.Push(current);

                _activeTab.IsHistoryNavigation = true;
                BrowserView.CoreWebView2.Navigate(target);
                UpdateNavigationButtons();
                return;
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserView?.CoreWebView2?.Reload();
        }

        private void AddressBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var query = args.ChosenSuggestion as string ?? args.QueryText;
            Navigate(query);
        }

        private void AddressBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string suggestion)
                sender.Text = suggestion;
        }

        private void AddressBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;

            sender.ItemsSource = BuildAddressSuggestions(sender.Text);
        }

        private void Navigate(string input)
        {
            var uri = BuildUri(input);
            if (uri == null || BrowserView?.CoreWebView2 == null)
                return;

            if (_activeTab != null)
            {
                PushCurrentPageToBackHistory(_activeTab, uri.ToString());
                _activeTab.Source = uri.ToString();
                _activeTab.LastKnownSource = uri.ToString();
                _activeTab.ForwardHistory.Clear();
                RenderTabs();
                UpdateNavigationButtons();
            }

            BrowserView.CoreWebView2.Navigate(uri.ToString());
        }

        private static Uri? BuildUri(string input)
        {
            input = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
                return null;

            if (!input.Contains('.') && !input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(input));

            if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                input = "https://" + input;

            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return null;

            if (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Host = "www.youtube.com"
                };
                return builder.Uri;
            }

            return uri;
        }

        private IEnumerable<string> BuildAddressSuggestions(string input)
        {
            input = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<string>();

            var suggestions = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddSuggestion(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                value = value.Trim();
                if (value.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    return;

                if (!MatchesAddressSuggestion(value, input))
                    return;

                if (seen.Add(value))
                    suggestions.Add(value);
            }

            foreach (var tab in _tabs)
            {
                AddSuggestion(tab.View.CoreWebView2?.Source);
                AddSuggestion(tab.Source);
                AddSuggestion(tab.LastKnownSource);
                foreach (var item in tab.BackHistory)
                    AddSuggestion(item);
                foreach (var item in tab.ForwardHistory)
                    AddSuggestion(item);
            }

            foreach (var suggestion in AddressSuggestions)
                AddSuggestion(suggestion);

            if (suggestions.Count < 8 && BuildUri(input) is Uri uri)
                AddSuggestion(uri.ToString());

            return suggestions.Take(8).ToArray();
        }

        private static bool MatchesAddressSuggestion(string value, string input)
        {
            if (value.Contains(input, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;

            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return host.StartsWith(input, StringComparison.OrdinalIgnoreCase) ||
                   host.Contains(input, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateNavigationButtons()
        {
            if (BrowserView?.CoreWebView2 == null)
            {
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
                return;
            }

            BackButton.IsEnabled = BrowserView.CoreWebView2.CanGoBack || _activeTab?.BackHistory.Count > 0;
            ForwardButton.IsEnabled = BrowserView.CoreWebView2.CanGoForward || _activeTab?.ForwardHistory.Count > 0;
        }

        private static bool SameUrl(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        private static void TrimHistory(Stack<string> history, int maxItems = 80)
        {
            if (history.Count <= maxItems)
                return;

            var kept = history.Take(maxItems).Reverse().ToArray();
            history.Clear();
            foreach (var item in kept)
                history.Push(item);
        }

        private void TrackTabSource(BrowserTab tab, string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return;

            if (tab.IsHistoryNavigation)
            {
                tab.IsHistoryNavigation = false;
                tab.Source = source;
                tab.PendingSource = null;
                tab.LastKnownSource = source;
                return;
            }

            var previous = tab.LastKnownSource;
            if (string.IsNullOrWhiteSpace(previous))
                previous = tab.Source;

            if (!string.IsNullOrWhiteSpace(previous) && !SameUrl(previous, source))
            {
                tab.BackHistory.Push(previous);
                tab.ForwardHistory.Clear();
                TrimHistory(tab.BackHistory);
            }

            tab.Source = source;
            tab.PendingSource = null;
            tab.LastKnownSource = source;
        }

        private void PushCurrentPageToBackHistory(BrowserTab tab, string? nextSource)
        {
            var current = tab.LastKnownSource;
            if (string.IsNullOrWhiteSpace(current))
                current = tab.View.CoreWebView2?.Source;
            if (string.IsNullOrWhiteSpace(current))
                current = tab.Source;

            if (string.IsNullOrWhiteSpace(current) || SameUrl(current, nextSource))
                return;

            tab.BackHistory.Push(current);
            TrimHistory(tab.BackHistory);
        }

        private void TrackSiteHistoryMessage(CoreWebView2 sender, string message)
        {
            var tab = FindTab(sender);
            if (tab == null)
                return;

            const string prefix = "zink-connect-site-history:";
            var payload = message[prefix.Length..];
            var separator = payload.IndexOf(':');
            if (separator < 0 || separator == payload.Length - 1)
                return;

            var reason = payload[..separator];
            var source = payload[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(source) || source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                return;

            if (reason.Equals("initial", StringComparison.OrdinalIgnoreCase))
            {
                tab.Source = source;
                tab.LastKnownSource ??= source;
                if (tab == _activeTab)
                {
                    AddressBox.Text = source;
                    UpdateNavigationButtons();
                    RenderTabs();
                }
                return;
            }

            TrackTabSource(tab, source);
            if (tab == _activeTab)
            {
                AddressBox.Text = source;
                UpdateNavigationButtons();
                RenderTabs();
            }
        }

        private sealed class BrowserTab
        {
            public required WebView2 View { get; init; }
            public string Title { get; set; } = "New tab";
            public string Source { get; set; } = "https://www.bing.com";
            public string? PendingSource { get; set; }
            public string? LastKnownSource { get; set; }
            public bool IsHistoryNavigation { get; set; }
            public Stack<string> BackHistory { get; } = new();
            public Stack<string> ForwardHistory { get; } = new();
        }
    }
}

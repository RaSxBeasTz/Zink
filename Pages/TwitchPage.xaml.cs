using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Navigation;

namespace Zink.Pages
{
    public sealed partial class TwitchPage : Page
    {
        private static readonly Uri TwitchHome = new("https://www.twitch.tv/");
        private static readonly HttpClient TwitchAdBlockClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        private const string TwitchAdBlockScriptUrl =
            "https://raw.githubusercontent.com/ryanbr/TwitchAdSolutions/v68.5.1/vaft/vaft.user.js";
        private const string TwitchAdBlockScriptSha256 =
            "556f0001c62f6650771aa7cade948abbd36dcc7fd12c10d667f85723392984a0";
        private bool _adBlockAttached;
        private bool _webViewInitializing;
        private bool _webViewReady;
        private Uri _initialUri = TwitchHome;

        public Uri CurrentPageUri => TryGetTwitchPageUri(TwitchWebView.CoreWebView2?.Source ?? TwitchWebView.Source?.AbsoluteUri, out var uri)
            ? uri : _initialUri;
        public string TabTitle => CurrentPageUri.AbsolutePath.Trim('/') is string channel && channel.Length > 0
            ? "Twitch · " + channel : "Twitch";

        internal static bool TryGetTwitchPageUri(string? value, out Uri uri)
        {
            uri = TwitchHome;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
                candidate.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(candidate.UserInfo) ||
                (candidate.Host != "www.twitch.tv" && candidate.Host != "twitch.tv" && candidate.Host != "m.twitch.tv"))
                return false;
            uri = candidate;
            return true;
        }

        internal static bool TryGetTwitchStreamUri(string? value, out Uri uri)
        {
            if (!TryGetTwitchPageUri(value, out uri)) return false;
            var channel = uri.AbsolutePath.Trim('/');
            if (channel.Length is < 1 or > 25 || channel.Contains('/')) return false;
            foreach (var character in channel)
                if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
            return channel.ToLowerInvariant() is not ("directory" or "search" or "downloads" or "jobs" or "settings" or
                "subscriptions" or "inventory" or "wallet" or "login" or "signup" or "logout" or "videos" or "friends" or "turbo" or "prime");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var address = e.Parameter is Uri uri ? uri.AbsoluteUri : e.Parameter as string;
            if (TryGetTwitchPageUri(address, out var target)) _initialUri = target;
        }

        // ? saved “card” visuals so we can restore after fullscreen
        private Thickness _savedHostMargin;
        private CornerRadius _savedHostCornerRadius;
        private Thickness _savedHostBorderThickness;
        private Brush _savedPageBackground;
        private bool _savedVisualsCaptured;

        public TwitchPage()
        {
            InitializeComponent();
            Loaded += TwitchPage_Loaded;
            InitializeTwitchFullscreenInput();
            InitializeTwitchInputTrace();
        }

        private async void TwitchPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_webViewInitializing || _webViewReady) return;
            _webViewInitializing = true;
            try
            {
                // Optional: persistent WebView profile
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZinkTwitchWebViewData");

                // All Twitch instances share this profile and must use identical browser options.
                // Muted streams should not be suspended just because their tab is hidden.
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--disable-background-media-suspend --disable-background-timer-throttling"
                };
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
                await TwitchWebView.EnsureCoreWebView2Async(env);
                await TwitchWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(TwitchBackgroundPlaybackScript);
                await TwitchWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(TwitchControlsGuardScript);
                await TwitchWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(TwitchInputTraceScript);
                await ConfigureTwitchAdBlockAsync();

                // ? In-player fullscreen -> app fullscreen + sidebar hide
                TwitchWebView.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;

                TwitchWebView.Source = _initialUri;
                _webViewReady = true;
            }
            catch
            {
                // Keep resilient
            }
            finally { _webViewInitializing = false; }
        }

        private void SetTwitchFullscreen(bool fullscreen)
        {
            if (_twitchFullscreen == fullscreen) return;
            _twitchFullscreen = fullscreen;
            if (!_savedVisualsCaptured)
            {
                _savedHostMargin = HostBorder.Margin;
                _savedHostCornerRadius = HostBorder.CornerRadius;
                _savedHostBorderThickness = HostBorder.BorderThickness;
                _savedPageBackground = Background;
                _savedVisualsCaptured = true;
            }

            if (fullscreen)
            {
                HostBorder.Margin = new Thickness(0);
                HostBorder.CornerRadius = new CornerRadius(0);
                HostBorder.BorderThickness = new Thickness(0);
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black);

                App.MainWindow.EnterFullscreenMode(enableMouseExitHook: false);
            }
            else
            {
                HostBorder.Margin = _savedHostMargin;
                HostBorder.CornerRadius = _savedHostCornerRadius;
                HostBorder.BorderThickness = _savedHostBorderThickness;
                Background = _savedPageBackground;
                App.MainWindow.ExitFullscreenMode();
            }

            try
            {
                _ = TwitchWebView.CoreWebView2?.ExecuteScriptAsync(
                    "window.__zinkTwitchControlsGuard?.(" + (fullscreen ? "true" : "false") + ");");
            }
            catch { }

            // A loading shield must not intercept a playing fullscreen stream.
            LoadingOverlay.IsHitTestVisible = !fullscreen;
            QueueTwitchFullscreenInputRestore();
        }

        private void TwitchWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
        {
            if (sender.CoreWebView2 == null)
                return;

            ApplyStoreCompliantTwitchWebViewPolicy(sender.CoreWebView2);
            sender.CoreWebView2.WebMessageReceived += TwitchInputTrace_WebMessageReceived;

            sender.CoreWebView2.ContextMenuRequested += TwitchWebView_ContextMenuRequested;
            sender.CoreWebView2.SourceChanged += (_, _) => App.MainWindow.RefreshTwitchTab(this);

            // Stream links that request another window get an independent Twitch tab.
            sender.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (TryGetTwitchStreamUri(e.Uri, out var stream))
                    App.MainWindow.OpenTwitchTab(stream);
                else
                    try { TwitchWebView.CoreWebView2.Navigate(e.Uri); } catch { }
            };
        }

        private void TwitchWebView_ContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
        {
            // Capture the clicked link now; selection may happen after the page changes.
            var address = args.ContextMenuTarget.HasLinkUri ? args.ContextMenuTarget.LinkUri : sender.Source;
            if (!TryGetTwitchStreamUri(address, out var stream)) return;
            var open = sender.Environment.CreateContextMenuItem("Open stream in new tab", null, CoreWebView2ContextMenuItemKind.Command);
            open.CustomItemSelected += (_, _) => DispatcherQueue.TryEnqueue(() => App.MainWindow.OpenTwitchTab(stream));
            args.MenuItems.Insert(0, open);
        }

        private async System.Threading.Tasks.Task ConfigureTwitchAdBlockAsync()
        {
            if (_adBlockAttached || TwitchWebView?.CoreWebView2 == null)
                return;

            string script = await LoadVerifiedTwitchAdBlockScriptAsync();
            var core = TwitchWebView.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += TwitchWebView_WebResourceRequested;

            if (!string.IsNullOrWhiteSpace(script))
            {
                // Adapt only the hash-verified, pinned script. Keep playable segments when
                // Twitch supplies no clean backup, rather than substituting 360p or blanks.
                script = script.Replace("scope.PreferLowQualityBackup = true;", "scope.PreferLowQualityBackup = false;")
                    .Replace("scope.FastAutoplayFirstTry = true;", "scope.FastAutoplayFirstTry = false;")
                    .Replace("scope.IsAdStrippingEnabled = true;", "scope.IsAdStrippingEnabled = false;")
                    .Replace("function stripAdSegments(textStr, stripAllSegments, streamInfo) {",
                        "function stripAdSegments(textStr, stripAllSegments, streamInfo) { if (!IsAdStrippingEnabled) return textStr;");
                await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            }

            await core.AddScriptToExecuteOnDocumentCreatedAsync(TwitchHighestQualityScript);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(TwitchCosmeticAdShieldScript);
            _adBlockAttached = true;
        }

        private void TwitchWebView_WebResourceRequested(
            CoreWebView2 sender,
            CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string uri = args.Request.Uri ?? string.Empty;
                if (!IsBlockedTwitchAdRequest(uri))
                    return;

                args.Response = sender.Environment.CreateWebResourceResponse(
                    new global::Windows.Storage.Streams.InMemoryRandomAccessStream(),
                    204,
                    "No Content",
                    "Content-Type: text/plain");
            }
            catch { }
        }

        private static bool IsBlockedTwitchAdRequest(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return false;

            return uri.Contains("doubleclick.net", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("googlesyndication.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("googleadservices.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("amazon-adsystem.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("scorecardresearch.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("adservice.google.", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/pagead/", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/pubads", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/securepubads", StringComparison.OrdinalIgnoreCase);
        }

        private const string TwitchHighestQualityScript = @"
(() => {
    if (location.hostname !== 'twitch.tv' && !location.hostname.endsWith('.twitch.tv')) return;
    if (window.__zinkHighestQualityInstalled) return;
    window.__zinkHighestQualityInstalled = true;
    try { localStorage.setItem('video-quality', JSON.stringify({ default: 'chunked' })); } catch {}

    function selectHighestQuality() {
        try {
            const root = document.querySelector('#root');
            if (!root) return;
            const key = Object.keys(root).find(k => k.startsWith('__reactContainer') || k.startsWith('__reactFiber'));
            const fiber = root._reactRootContainer?._internalRoot?.current || (key && root[key]);
            if (!fiber) return;
            const pending = [fiber];
            const visited = new Set();
            let player;
            while (pending.length && visited.size < 20000) {
                const node = pending.pop();
                if (!node || visited.has(node)) continue;
                visited.add(node);
                const state = node.stateNode;
                const candidate = state?.props?.mediaPlayerInstance || state;
                const instance = candidate?.playerInstance || candidate;
                if (typeof instance?.getQualities === 'function' && typeof instance?.setQuality === 'function') {
                    player = instance;
                    break;
                }
                if (node.child) pending.push(node.child);
                if (node.sibling) pending.push(node.sibling);
            }
            if (!player) return;
            const qualities = player.getQualities();
            if (!Array.isArray(qualities)) return;
            const available = qualities.filter(q => q && q.group !== 'auto' && q.group !== 'audio_only');
            const rank = q => Number(q.height) || parseInt(q.name, 10) || 0;
            available.sort((a, b) => rank(b) - rank(a) || (Number(b.framerate) || 0) - (Number(a.framerate) || 0));
            const best = available.find(q => q.group === 'chunked') || available[0];
            if (!best) return;
            const current = player.getQuality?.();
            if (current?.group !== best.group || player.isAutoQualityMode?.()) {
                player.setAutoQualityMode?.(false);
                player.setQuality(best);
            }
        } catch {}
    }
    // Reapply after channel navigation and ad-block player replacement without
    // pausing, seeking, reloading, or overriding the viewer's pause intent.
    setInterval(selectHighestQuality, 2000);
})();
";

        private const string TwitchCosmeticAdShieldScript = @"
(() => {
    if (window.__zinkTwitchCosmeticAdShieldInstalled) return;
    window.__zinkTwitchCosmeticAdShieldInstalled = true;

    const selectors = [
        '[data-a-target=""video-ad-label""]',
        '[data-a-target=""video-ad-countdown""]',
        '[data-test-selector=""sad-overlay""]',
        '.stream-display-ad',
        '.channel-leaderboard-header-rotating',
        '[class*=""ad-banner""]',
        '[class*=""AdBanner""]',
        '[data-a-target*=""ad-banner""]',
        '[data-test-selector*=""ad-banner""]'
    ];

    function removeAds() {
        try {
            if (!location.hostname.endsWith('twitch.tv')) return;
            for (const selector of selectors) {
                for (const node of document.querySelectorAll(selector)) {
                    try { node.remove(); } catch {}
                }
            }
        } catch {}
    }

    new MutationObserver(removeAds).observe(document.documentElement, {
        childList: true,
        subtree: true
    });
    setInterval(removeAds, 250);
    removeAds();
})();
  ";

        private static async System.Threading.Tasks.Task<string> LoadVerifiedTwitchAdBlockScriptAsync()
        {
            string cacheFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZinkTwitchWebViewData");
            string cachePath = Path.Combine(cacheFolder, "ZinkTwitchAdBlock.v68.5.1.js");

            try
            {
                if (File.Exists(cachePath))
                {
                    string cachedScript = await File.ReadAllTextAsync(cachePath);
                    if (HasExpectedTwitchAdBlockHash(cachedScript))
                        return cachedScript;
                }

                string downloadedScript = await TwitchAdBlockClient.GetStringAsync(TwitchAdBlockScriptUrl);
                if (!HasExpectedTwitchAdBlockHash(downloadedScript))
                    return string.Empty;

                Directory.CreateDirectory(cacheFolder);
                await File.WriteAllTextAsync(cachePath, downloadedScript, new UTF8Encoding(false));
                return downloadedScript;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool HasExpectedTwitchAdBlockHash(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return false;

            byte[] actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(script));
            byte[] expectedHash = Convert.FromHexString(TwitchAdBlockScriptSha256);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }

        private static void ApplyStoreCompliantTwitchWebViewPolicy(CoreWebView2 webView)
        {
            webView.Settings.AreDefaultContextMenusEnabled = true;
            webView.Settings.AreDevToolsEnabled = true;
            webView.Settings.IsZoomControlEnabled = true;
            webView.Settings.IsStatusBarEnabled = false;
            webView.Settings.AreHostObjectsAllowed = false;

            try
            {
                webView.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
            }
            catch { }

            webView.PermissionRequested += (s, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Notifications ||
                    e.PermissionKind == CoreWebView2PermissionKind.Geolocation)
                {
                    e.State = CoreWebView2PermissionState.Deny;
                    e.Handled = true;
                }
            };
        }

        private void TwitchWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (_twitchFullscreen) QueueTwitchFullscreenLayout();
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private async void TwitchWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            await SetPlaybackTabActiveAsync(_isPlaybackTabActive);
            LoadingOverlay.Visibility = Visibility.Collapsed;
            App.MainWindow.RefreshTwitchTab(this);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            try
            {
                if (TwitchWebView != null)
                    TwitchWebView.Source = new Uri("about:blank");
            }
            catch { }
        }
    }
}

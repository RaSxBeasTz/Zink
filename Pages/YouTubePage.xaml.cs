using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Animation;
using System.Text.Json;
using System.Threading;
using Zink.Models;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class YouTubePage : Page
    {
        private AppWindow _appWindow;
        private bool _webViewInitialized;
        private bool _navigationHandlerAttached;
        private string _pendingSearchQuery;
        private bool _isResolvingBestMatch;
        private bool _hasShownCurrentLoad;
        private bool _adBlockAttached;
        private LikedRadioSong _pendingSong;
        private string _resolvedVideoUrl;
        private CancellationTokenSource? _navigationCts;

        public YouTubePage()
        {
            this.InitializeComponent();
            this.Loaded += YouTubePage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _navigationCts?.Cancel();
            _navigationCts?.Dispose();
            _navigationCts = new CancellationTokenSource();

            _pendingSong = null;
            _pendingSearchQuery = null;
            _resolvedVideoUrl = null;

            if (e.Parameter is LikedRadioSong song)
            {
                _pendingSong = song;
                _pendingSearchQuery = BuildYouTubeQuery(song);
                _resolvedVideoUrl = song.YouTubeVideoUrl?.Trim();
            }
            else if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
            {
                _pendingSearchQuery = query.Trim();
            }

            if (_webViewInitialized)
            {
                _ = NavigateToInitialTargetAsync();
            }
        }

        private async void YouTubePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_webViewInitialized)
            {
                await NavigateToInitialTargetAsync();
                return;
            }

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZinkYouTubeWebViewData");

            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
            await YouTubeWebView.EnsureCoreWebView2Async(env);

            YouTubeWebView.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;
            YouTubeWebView.CoreWebView2.WebMessageReceived += YouTubeWebView_WebMessageReceived;

            if (!_navigationHandlerAttached)
            {
                YouTubeWebView.CoreWebView2.NavigationCompleted += YouTubeWebView_NavigationCompleted;
                _navigationHandlerAttached = true;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            _webViewInitialized = true;

            await NavigateToInitialTargetAsync();
        }

        private async Task ConfigureYouTubeAdBlockAsync()
        {
            if (_adBlockAttached || YouTubeWebView?.CoreWebView2 == null)
                return;

            var core = YouTubeWebView.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += YouTubeWebView_WebResourceRequested;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
(() => {
    if (window.__zinkYouTubeGuardInstalled) return;
    window.__zinkYouTubeGuardInstalled = true;

    const post = (state) => {
        try { chrome.webview.postMessage({ type: 'zink-youtube-fullscreen', state }); } catch {}
    };

    const hideSelectors = [
        '.ytp-ad-module',
        '.ytp-ad-overlay-container',
        '.ytp-ad-player-overlay',
        '.video-ads',
        '.ytp-ad-image-overlay',
        '.ytp-ad-text-overlay',
        '.ytp-ad-preview-container',
        '.ytp-ad-survey',
        '.ytp-ad-message-container',
        '.ytp-ad-button',
        '.ytp-ad-button-link',
        '.ytp-ad-simple-ad-badge',
        '.ytp-ad-player-overlay-layout',
        '.ytp-ad-player-overlay-layout__player-card-container',
        '.ytp-ad-visit-advertiser-button',
        '.ytp-ad-visit-advertiser-button-content',
        '.ytp-ad-player-overlay-instream-info',
        'ytd-ad-slot-renderer',
        'ytd-companion-slot-renderer',
        'ytd-promoted-sparkles-web-renderer',
        'ytd-display-ad-renderer',
        'ytd-in-feed-ad-layout-renderer',
        'ytd-action-companion-ad-renderer',
        'ytd-statement-banner-renderer',
        'ytd-rich-item-renderer:has(ytd-ad-slot-renderer)'
    ];

    function scrubPlayerJson(value) {
        if (!value || typeof value !== 'object') return value;
        try {
            const stack = [value];
            const badKeys = new Set([
                'adPlacements',
                'adSlots',
                'adBreakHeartbeatParams',
                'playerAds',
                'playerLegacyDesktopYpcOfferRenderer',
                'playerResponseAds',
                'instreamVideoAdRenderer',
                'companionAdRenderer',
                'mealbarPromoRenderer',
                'promotedSparklesWebRenderer',
                'statementBannerRenderer'
            ]);

            while (stack.length) {
                const obj = stack.pop();
                if (!obj || typeof obj !== 'object') continue;

                for (const key of Object.keys(obj)) {
                    if (badKeys.has(key) || key.toLowerCase().includes('adplacement')) {
                        delete obj[key];
                        continue;
                    }

                    const child = obj[key];
                    if (child && typeof child === 'object') stack.push(child);
                }
            }
        } catch {}
        return value;
    }

    const originalFetch = window.fetch;
    if (typeof originalFetch === 'function') {
        window.fetch = async function(...args) {
            const response = await originalFetch.apply(this, args);
            try {
                const url = String(args[0] && (args[0].url || args[0]) || '');
                if (/\/youtubei\/v1\/(player|next)/i.test(url)) {
                    const clone = response.clone();
                    const text = await clone.text();
                    const json = scrubPlayerJson(JSON.parse(text));
                    return new Response(JSON.stringify(json), {
                        status: response.status,
                        statusText: response.statusText,
                        headers: response.headers
                    });
                }
            } catch {}
            return response;
        };
    }

    const OriginalXHR = window.XMLHttpRequest;
    if (OriginalXHR) {
        window.XMLHttpRequest = function() {
            const xhr = new OriginalXHR();
            let requestUrl = '';
            const open = xhr.open;
            xhr.open = function(method, url, ...rest) {
                requestUrl = String(url || '');
                return open.call(xhr, method, url, ...rest);
            };
            xhr.addEventListener('readystatechange', function() {
                try {
                    if (xhr.readyState !== 4 || !/\/youtubei\/v1\/(player|next)/i.test(requestUrl)) return;
                    const json = scrubPlayerJson(JSON.parse(xhr.responseText));
                    Object.defineProperty(xhr, 'responseText', { get: () => JSON.stringify(json) });
                    Object.defineProperty(xhr, 'response', { get: () => JSON.stringify(json) });
                } catch {}
            });
            return xhr;
        };
    }

    function forcePlayerFullscreen(on) {
        try {
            document.documentElement.classList.toggle('zink-youtube-fullscreen', !!on);
            document.body.classList.toggle('zink-youtube-fullscreen', !!on);
        } catch {}
    }

    function zapAds() {
        const player = document.querySelector('.html5-video-player');
        const video = document.querySelector('video');
        const visibleAdUi = !!document.querySelector(
            '.ytp-ad-player-overlay, .ytp-ad-text, .ytp-ad-preview-container, .ytp-ad-message-container, .ytp-ad-button, .ytp-ad-simple-ad-badge, .ytp-ad-player-overlay-layout, .ytp-ad-visit-advertiser-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button, .ytp-skip-ad-button, .ytp-skip-ad-button-modern'
        );
        const adTextVisible = /\b(sponsored|visit site|skip|advertiser|ad)\b/i.test(
            Array.from(document.querySelectorAll('.ytp-ad-player-overlay, .ytp-ad-message-container, .ytp-ad-button, .ytp-ad-simple-ad-badge, .ytp-ad-visit-advertiser-button, .ytp-skip-ad-button, .ytp-ad-skip-button-modern'))
                .map(el => el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '')
                .join(' ')
        );
        const adShowing = !!player && (
            player.classList.contains('ad-showing') ||
            player.classList.contains('ad-interrupting')
        );

        const skipButtons = document.querySelectorAll(
            '.ytp-ad-skip-button-modern, .ytp-ad-skip-button, .ytp-skip-ad-button, .ytp-skip-ad-button-modern, button[class*=""ytp-ad-skip""], button[class*=""skip""]'
        );
        for (const skipButton of skipButtons) {
            try { skipButton.click(); } catch {}
        }

        if (adShowing && (visibleAdUi || adTextVisible) && video) {
            try {
                video.muted = true;
                video.playbackRate = 16;

                if (Number.isFinite(video.duration) && video.duration > 0 && video.duration <= 300) {
                    video.currentTime = Math.max(0, video.duration - 0.25);
                } else {
                    video.currentTime = Math.min(video.currentTime + 10, Math.max(video.currentTime + 10, 30));
                }
            } catch {}
        } else if (video) {
            try {
                video.muted = false;
                video.playbackRate = 1;
            } catch {}
        }

        for (const selector of hideSelectors) {
            try {
                document.querySelectorAll(selector).forEach(node => {
                    node.style.setProperty('display', 'none', 'important');
                });
            } catch {}
        }
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

    document.addEventListener('click', () => {
        burstZap();
    }, true);

    function burstZap() {
        for (let i = 0; i < 40; i++) {
            setTimeout(zapAds, i * 100);
        }
    }

    window.__zinkZapYouTubeAds = function() { burstZap(); zapAds(); };

    setInterval(zapAds, 500);
    installFullscreenBridge();
    new MutationObserver(zapAds).observe(document.documentElement, { childList: true, subtree: true, attributes: true });
    burstZap();
    zapAds();
})();
");

            _adBlockAttached = true;
        }

        private void YouTubeWebView_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string uri = args.Request.Uri ?? string.Empty;
                if (!IsBlockedYouTubeAdRequest(uri))
                    return;

                args.Response = sender.Environment.CreateWebResourceResponse(
                    new global::Windows.Storage.Streams.InMemoryRandomAccessStream(),
                    204,
                    "No Content",
                    "Content-Type: text/plain");
            }
            catch { }
        }

        private static bool IsBlockedYouTubeAdRequest(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return false;

            return uri.Contains("doubleclick.net", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("googleads.g.doubleclick.net", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("googlesyndication.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("googleadservices.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/pagead/", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/ptracking", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/api/stats/ads", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/api/stats/qoe", StringComparison.OrdinalIgnoreCase) && uri.Contains("adformat", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/api/stats/watchtime", StringComparison.OrdinalIgnoreCase) && uri.Contains("adformat", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/pcs/activeview", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/pagead/interaction", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/pubads", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/securepubads", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("adservice.google.", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("static.doubleclick.net", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("/get_midroll_", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("ad_break", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("adformat=", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("ctier=L", StringComparison.OrdinalIgnoreCase) && uri.Contains("oad=", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("youtube.com/pagead/", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("youtube.com/get_video_info", StringComparison.OrdinalIgnoreCase) && uri.Contains("adformat", StringComparison.OrdinalIgnoreCase);
        }

        private void YouTubeWebView_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string message = args.WebMessageAsJson ?? "";
                if (message.Contains("zink-youtube-fullscreen", StringComparison.OrdinalIgnoreCase) &&
                    message.Contains("enter", StringComparison.OrdinalIgnoreCase))
                {
                    App.MainWindow.EnterFullscreenMode();
                }
                else if (message.Contains("zink-youtube-fullscreen", StringComparison.OrdinalIgnoreCase) &&
                         message.Contains("exit", StringComparison.OrdinalIgnoreCase))
                {
                    App.MainWindow.ExitFullscreenMode();
                }
            }
            catch { }
        }

        private async Task NavigateToInitialTargetAsync()
        {
            if (YouTubeWebView?.CoreWebView2 == null)
                return;

            var navigationToken = _navigationCts?.Token ?? CancellationToken.None;
            if (navigationToken.IsCancellationRequested)
                return;

            _hasShownCurrentLoad = false;

            YouTubeWebView.Visibility = Visibility.Collapsed;
            YouTubeWebView.Opacity = 0;
            YouTubeLoader.Opacity = 1;
            YouTubeLoader.Visibility = Visibility.Visible;

            string? videoUrl = NormalizeYouTubeVideoUrl(_resolvedVideoUrl);
            if (videoUrl != null)
            {
                _isResolvingBestMatch = false;
                _resolvedVideoUrl = videoUrl;
                YouTubeWebView.Source = new Uri(videoUrl);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_pendingSearchQuery))
            {
                _isResolvingBestMatch = true;
                string searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(_pendingSearchQuery)}";
                YouTubeWebView.Source = new Uri(searchUrl);
            }
            else
            {
                _isResolvingBestMatch = false;
                YouTubeWebView.Source = new Uri("https://www.youtube.com/");
            }

            await Task.CompletedTask;
        }

        private async void YouTubeWebView_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            var navigationToken = _navigationCts?.Token ?? CancellationToken.None;
            if (navigationToken.IsCancellationRequested)
                return;

            if (!args.IsSuccess)
            {
                _isResolvingBestMatch = false;
                ShowWebViewOnce();
                return;
            }

            ShowWebViewOnce();

            try
            {
                await WaitForReadyStateAsync(navigationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (navigationToken.IsCancellationRequested)
                return;

            string currentUrl = "";
            try
            {
                currentUrl = sender.Source ?? "";
            }
            catch { }

            if (_isResolvingBestMatch &&
                !string.IsNullOrWhiteSpace(_pendingSearchQuery) &&
                currentUrl.Contains("/results?", StringComparison.OrdinalIgnoreCase))
            {
                bool navigated = await TryNavigateToBestVideoWithRetriesAsync(_pendingSearchQuery);
                if (navigated)
                    return;
            }

            string? videoUrl = NormalizeYouTubeVideoUrl(currentUrl);
            if (videoUrl != null)
            {
                _resolvedVideoUrl = videoUrl;

                if (_pendingSong != null && _pendingSong.Id != Guid.Empty)
                {
                    try
                    {
                        await LikedRadioLikesService.Instance.MarkYouTubeMatchAsync(_pendingSong.Id, videoUrl);
                    }
                    catch { }
                }
            }

            ShowWebViewOnce();
        }

        private async Task WaitForReadyStateAsync(CancellationToken cancellationToken)
        {
            const int maxAttempts = 20;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    string result = await YouTubeWebView.CoreWebView2.ExecuteScriptAsync("document.readyState");
                    if (result.Contains("complete", StringComparison.OrdinalIgnoreCase))
                        break;
                }
                catch { }

                await Task.Delay(200, cancellationToken);
            }
        }

        private async Task<bool> TryNavigateToBestVideoWithRetriesAsync(string query)
        {
            for (int i = 0; i < 8; i++)
            {
                bool navigated = await TryNavigateToBestVideoAsync(query);
                if (navigated)
                {
                    _isResolvingBestMatch = false;
                    return true;
                }

                await Task.Delay(200);
            }

            _isResolvingBestMatch = false;
            return false;
        }

        private async Task<bool> TryNavigateToBestVideoAsync(string query)
        {
            string[] parts = SplitArtistAndTitle(query);
            string artist = parts[0];
            string title = parts[1];

            string artistJson = JsonSerializer.Serialize(artist ?? "");
            string titleJson = JsonSerializer.Serialize(title ?? "");

            string script = $@"
(function() {{
    function normalize(text) {{
        return (text || '')
            .toLowerCase()
            .replace(/[^\w\s]/g, ' ')
            .replace(/\s+/g, ' ')
            .trim();
    }}

    function containsAllWords(haystack, needle) {{
        var n = normalize(needle);
        if (!n) return false;
        var words = n.split(' ').filter(Boolean);
        if (words.length === 0) return false;
        var h = normalize(haystack);
        return words.every(function(w) {{ return h.indexOf(w) >= 0; }});
    }}

    function parseDurationSeconds(durationText) {{
        var raw = (durationText || '').trim();
        if (!raw) return -1;

        var parts = raw.split(':').map(function(p) {{ return parseInt(p, 10); }});
        if (parts.some(function(n) {{ return isNaN(n); }})) return -1;

        if (parts.length === 2) return (parts[0] * 60) + parts[1];
        if (parts.length === 3) return (parts[0] * 3600) + (parts[1] * 60) + parts[2];

        return -1;
    }}

    var artist = {artistJson};
    var title = {titleJson};
    var artistNorm = normalize(artist);
    var titleNorm = normalize(title);

    var badWords = [
        'cover','karaoke','instrumental','slowed','reverb','nightcore',
        'remix','reaction','live','concert','fan made','fanmade','8d',
        'sped up','spedup','edit audio','bass boosted','tribute'
    ];

    var goodWords = [
        'official audio','official video','audio','topic','vevo','visualizer','lyrics'
    ];

    var anchors = Array.from(document.querySelectorAll(
        'a#video-title[href*=""watch?v=""], a#video-title-link[href*=""watch?v=""], a[href*=""/watch?v=""]'));

    if (!anchors || anchors.length === 0)
        return JSON.stringify({{ found: false, reason: 'no-results' }});

    var best = null;
    var bestScore = -999999;

    for (var i = 0; i < anchors.length; i++) {{
        var a = anchors[i];
        var titleText = normalize(a.textContent || a.title || '');
        if (!titleText) continue;
        var container = a.closest('ytd-video-renderer,ytd-rich-item-renderer,ytd-compact-video-renderer') || a.parentElement || document;
        var channelAnchor = container.querySelector('#channel-name a, ytd-channel-name a');
        var channelText = normalize(channelAnchor ? channelAnchor.textContent : '');

        var durationElement =
            container.querySelector('ytd-thumbnail-overlay-time-status-renderer span') ||
            container.querySelector('#text.ytd-thumbnail-overlay-time-status-renderer') ||
            container.querySelector('.ytd-thumbnail-overlay-time-status-renderer');

        var durationText = durationElement ? durationElement.textContent : '';
        var durationSeconds = parseDurationSeconds(durationText);

        var score = 0;

        if (artistNorm && titleText.indexOf(artistNorm) >= 0) score += 90;
        if (titleNorm && titleText.indexOf(titleNorm) >= 0) score += 130;

        if (artistNorm && containsAllWords(titleText, artistNorm)) score += 35;
        if (titleNorm && containsAllWords(titleText, titleNorm)) score += 55;

        if (artistNorm && channelText.indexOf(artistNorm) >= 0) score += 55;
        if (channelText.indexOf('topic') >= 0) score += 40;
        if (channelText.indexOf('vevo') >= 0) score += 35;

        for (var g = 0; g < goodWords.length; g++) {{
            if (titleText.indexOf(goodWords[g]) >= 0) score += 12;
        }}

        for (var b = 0; b < badWords.length; b++) {{
            if (titleText.indexOf(badWords[b]) >= 0) score -= 70;
        }}

        if (artistNorm && titleNorm && titleText.indexOf(artistNorm) >= 0 && titleText.indexOf(titleNorm) >= 0)
            score += 80;

        if (durationSeconds > 0) {{
            if (durationSeconds >= 90 && durationSeconds <= 420) score += 35;
            else if (durationSeconds > 420 && durationSeconds <= 900) score += 5;
            else if (durationSeconds > 900) score -= 70;
            else if (durationSeconds < 60) score -= 45;
        }}

        if (!best || score > bestScore) {{
            best = a;
            bestScore = score;
        }}
    }}

    if (!best)
        return JSON.stringify({{ found: false, reason: 'no-best' }});

    var href = best.href || '';
    if (!href)
        return JSON.stringify({{ found: false, reason: 'no-href' }});

    if (href.startsWith('/'))
        href = 'https://www.youtube.com' + href;

    window.location.href = href;
    return JSON.stringify({{ found: true, href: href }});
}})();";

            try
            {
                string result = await YouTubeWebView.CoreWebView2.ExecuteScriptAsync(script);
                if (string.IsNullOrWhiteSpace(result))
                    return false;

                return result.Contains(@"""found"":true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string[] SplitArtistAndTitle(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new[] { "", "" };

            int sep = query.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
            {
                string artist = query.Substring(0, sep).Trim();
                string title = query.Substring(sep + 3).Trim();
                return new[] { artist, title };
            }

            return new[] { "", query.Trim() };
        }

        private static string BuildYouTubeQuery(LikedRadioSong song)
        {
            string artist = song?.Artist?.Trim() ?? "";
            string title = song?.Title?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                return $"{artist} - {title}";

            if (!string.IsNullOrWhiteSpace(title))
                return title;

            if (!string.IsNullOrWhiteSpace(artist))
                return artist;

            return "";
        }

        private static string? NormalizeYouTubeVideoUrl(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return null;

            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                string id = uri.AbsolutePath.Trim('/');
                return string.IsNullOrWhiteSpace(id)
                    ? null
                    : $"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}";
            }

            if (!uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
                return null;

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string? videoId = query["v"];
            return string.IsNullOrWhiteSpace(videoId)
                ? null
                : $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}";
        }

        private void ShowWebViewOnce()
        {
            if (_hasShownCurrentLoad)
                return;

            _hasShownCurrentLoad = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                YouTubeWebView.Visibility = Visibility.Visible;

                var fadeIn = (Storyboard)Resources["FadeInWebViewStoryboard"];
                var fadeOut = (Storyboard)Resources["FadeOutLoaderStoryboard"];
                fadeIn.Begin();
                fadeOut.Begin();
            });
        }

        private void CoreWebView2_ContainsFullScreenElementChanged(CoreWebView2 sender, object args)
        {
            if (_appWindow == null) return;

            if (sender.ContainsFullScreenElement)
            {
                App.MainWindow.EnterFullscreenMode();
            }
            else
            {
                App.MainWindow.ExitFullscreenMode();
            }
        }

        public void StopWebView()
        {
            _navigationCts?.Cancel();

            if (YouTubeWebView == null)
                return;

            try { YouTubeWebView.CoreWebView2?.Stop(); } catch { }
            try { YouTubeWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            YouTubeWebView.Visibility = Visibility.Collapsed;
            YouTubeWebView.Opacity = 0;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            StopWebView();

            _pendingSearchQuery = null;
            _isResolvingBestMatch = false;
            _hasShownCurrentLoad = false;
            _pendingSong = null;
            _resolvedVideoUrl = null;

        }
    }
}

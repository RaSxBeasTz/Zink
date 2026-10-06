using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Windowing;
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
        private AppWindow _appWindow;
        private bool _adBlockAttached;

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
        }

        private async void TwitchPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Optional: persistent WebView profile
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZinkTwitchWebViewData");

                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
                await TwitchWebView.EnsureCoreWebView2Async(env);
                await ConfigureTwitchAdBlockAsync();

                // ? In-player fullscreen -> app fullscreen + sidebar hide
                TwitchWebView.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;

                // AppWindow for fullscreen toggle
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _appWindow = AppWindow.GetFromWindowId(windowId);

                TwitchWebView.Source = TwitchHome;
            }
            catch
            {
                // Keep resilient
            }
        }

        private void CoreWebView2_ContainsFullScreenElementChanged(CoreWebView2 sender, object args)
        {
            if (_appWindow == null) return;

            // ? capture original visuals once
            if (!_savedVisualsCaptured)
            {
                _savedHostMargin = HostBorder.Margin;
                _savedHostCornerRadius = HostBorder.CornerRadius;
                _savedHostBorderThickness = HostBorder.BorderThickness;
                _savedPageBackground = Background;
                _savedVisualsCaptured = true;
            }

            if (sender.ContainsFullScreenElement)
            {
                // ? TRUE edge-to-edge: remove the “card” styling
                HostBorder.Margin = new Thickness(0);
                HostBorder.CornerRadius = new CornerRadius(0);
                HostBorder.BorderThickness = new Thickness(0);

                // ? FIX: WinUI 3 uses Microsoft.UI.Colors (NOT Windows.UI.Colors)
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black);

                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                App.MainWindow.SetSidebarVisibility(false);
            }
            else
            {
                // ? restore visuals
                HostBorder.Margin = _savedHostMargin;
                HostBorder.CornerRadius = _savedHostCornerRadius;
                HostBorder.BorderThickness = _savedHostBorderThickness;
                Background = _savedPageBackground;

                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                App.MainWindow.SetSidebarVisibility(true);
            }
        }

        private void TwitchWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
        {
            if (sender.CoreWebView2 == null)
                return;

            ApplyStoreCompliantTwitchWebViewPolicy(sender.CoreWebView2);

            // Keep window.open inside same WebView
            sender.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                try { TwitchWebView.CoreWebView2.Navigate(e.Uri); } catch { }
            };
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
                await core.AddScriptToExecuteOnDocumentCreatedAsync(script);

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
                   uri.Contains("/securepubads", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("spade.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("video-ad-stats", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("ad-impression", StringComparison.OrdinalIgnoreCase) ||
                   uri.Contains("ad_quartile", StringComparison.OrdinalIgnoreCase);
        }

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
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void TwitchWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class SpotifyLoginPage : Page
    {
        private bool _attached = false;
        private bool _tokenExchanged = false;
        private bool _initialized = false;

        public SpotifyLoginPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            SpotifyWebView.NavigationStarting += SpotifyWebView_NavigationStarting;
            SpotifyWebView.NavigationCompleted += SpotifyWebView_NavigationCompleted;

            this.Loaded += async (_, __) => await InitializeSpotifyWebViewAsync();

            SpotifyWebView.CoreWebView2Initialized += SpotifyWebView_CoreWebView2Initialized;
        }

        private async Task InitializeSpotifyWebViewAsync()
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Zink",
                    "WebView2",
                    "Spotify");

                Directory.CreateDirectory(userDataFolder);

                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: null);

                await SpotifyWebView.EnsureCoreWebView2Async(environment);
                SpotifyWebView.CoreWebView2.Navigate(
                    SpotifyAuthHelper.GetNativeAuthorizationUrl(showDialog: false));
            }
            catch (Exception ex)
            {
                _initialized = false;
                System.Diagnostics.Debug.WriteLine("Spotify WebView initialization failed: " + ex.Message);
            }
        }

        private async void SpotifyWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            var url = args.Uri;
            if (url.StartsWith(SpotifyAuthHelper.DefaultRedirectUri, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;

                if (_tokenExchanged)
                {
                    try
                    {
                        SpotifyWebView.Source = new Uri("https://open.spotify.com/");
                    }
                    catch { }
                    return;
                }

                try
                {
                    var uri = new Uri(url);
                    var code = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("code");

                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        await SpotifyAuthHelper.ExchangeCodeForTokenAsync(code);
                        _tokenExchanged = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Spotify token exchange failed: " + ex.Message);
                }

                try
                {
                    SpotifyWebView.Source = new Uri("https://open.spotify.com/");
                }
                catch { }
            }
        }

        private async void SpotifyWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (SpotifyWebView.Source != null &&
                SpotifyWebView.Source.AbsoluteUri.StartsWith("https://open.spotify.com", StringComparison.OrdinalIgnoreCase) &&
                !_attached)
            {
                try
                {
                    if (SpotifyWebView.CoreWebView2 == null)
                        await SpotifyWebView.EnsureCoreWebView2Async();

                    SpotifyControllerService.Instance.Attach(SpotifyWebView.CoreWebView2);
                    _attached = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to attach SpotifyControllerService: " + ex.Message);
                }
            }
        }

        private void SpotifyWebView_CoreWebView2Initialized(
            WebView2 sender,
            Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
        {
            if (_attached) return;

            try
            {
                if (sender.CoreWebView2 != null)
                {
                    SpotifyControllerService.Instance.Attach(sender.CoreWebView2);
                    _attached = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to early-attach SpotifyControllerService: " + ex.Message);
            }
        }
    }
}

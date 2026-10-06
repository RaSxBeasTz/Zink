using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;
using Zink.Services.Streaming;

namespace Zink.Windows
{
    public sealed class TwitchConnectWindow : Window
    {
        private const int WindowWidthDips = 1275;
        private const int WindowHeightDips = 850;

        private readonly TaskCompletionSource<TwitchConnectResult> _completion = new();
        private readonly string _state = TwitchViewerCountService.CreateState();
        private readonly WebView2 _webView = new();
        private readonly TextBlock _statusText = new()
        {
            Text = "Opening Twitch sign-in...",
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap
        };
        private bool _completed;

        public TwitchConnectWindow()
        {
            Title = "Connect Twitch";
            Content = BuildUi();
            Zink.Services.LocalizationService.RegisterWindow(this);
            Closed += (_, _) =>
            {
                if (!_completed)
                    _completion.TrySetResult(new TwitchConnectResult(false, null, "Twitch sign-in was cancelled."));
            };
        }

        public static async Task<TwitchConnectResult> ConnectAsync()
        {
            if (!TwitchViewerCountService.HasConfiguredClientId)
                return new TwitchConnectResult(false, null, "Zink does not have a Twitch Client ID configured yet.");

            var window = new TwitchConnectWindow();
            window.Activate();
            await window.InitializeAsync();
            return await window._completion.Task;
        }

        public static async Task ResetSavedSignInAsync()
        {
            try
            {
                TwitchViewerCountService.Instance.Disconnect();
                var userDataFolder = GetUserDataFolder();
                if (Directory.Exists(userDataFolder))
                    await Task.Run(() => Directory.Delete(userDataFolder, true));
            }
            catch
            {
            }
        }

        private Grid BuildUi()
        {
            var root = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 8, 12, 16))
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid
            {
                Padding = new Thickness(16, 12, 16, 12),
                ColumnSpacing = 12
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_statusText);

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 82
            };
            cancelButton.Click += (_, _) =>
            {
                _completed = true;
                _completion.TrySetResult(new TwitchConnectResult(false, null, "Twitch sign-in was cancelled."));
                Close();
            };
            Grid.SetColumn(cancelButton, 1);
            header.Children.Add(cancelButton);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Grid.SetRow(_webView, 1);
            root.Children.Add(_webView);
            return root;
        }

        private async Task InitializeAsync()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var windowWidth = DipsToPixels(hwnd, WindowWidthDips);
            var windowHeight = DipsToPixels(hwnd, WindowHeightDips);
            appWindow.Resize(new SizeInt32(windowWidth, windowHeight));

            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            appWindow.Move(new PointInt32(
                area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - windowWidth) / 2),
                area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - windowHeight) / 2)));

            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, GetUserDataFolder(), null);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            _webView.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;
            _webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _webView.CoreWebView2.AddWebResourceRequestedFilter("http://localhost/*", CoreWebView2WebResourceContext.Document);
            _webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
            _webView.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try { _webView.CoreWebView2.Navigate(e.Uri); } catch { }
            };
            _webView.CoreWebView2.Navigate(TwitchViewerCountService.BuildAuthorizeUri(_state).ToString());
        }

        private async void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            await TryCompleteFromUriAsync(args.Uri);
        }

        private async void CoreWebView2_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
        {
            await TryCompleteFromUriAsync(sender.Source);
            if (!_completed && IsTwitchCallbackHost(sender.Source))
                await TryCompleteFromBrowserLocationAsync(sender);
        }

        private async void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            await TryCompleteFromUriAsync(sender.Source);
            if (!_completed && IsTwitchCallbackHost(sender.Source))
                await TryCompleteFromBrowserLocationAsync(sender);
        }

        private async void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            await TryCompleteFromUriAsync(args.TryGetWebMessageAsString());
        }

        private void CoreWebView2_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (!IsTwitchCallbackHost(args.Request.Uri))
                return;

            var html = """
                <!doctype html>
                <html>
                <head><meta charset="utf-8"><title>Connecting Twitch</title></head>
                <body style="margin:0;font-family:Segoe UI,Arial,sans-serif;background:#080c10;color:#d8f6ff;display:grid;place-items:center;height:100vh">
                    <div>Completing Twitch connection...</div>
                    <script>
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage(window.location.href);
                        }
                    </script>
                </body>
                </html>
                """;
            var bytes = Encoding.UTF8.GetBytes(html);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            stream.Seek(0);
            args.Response = sender.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
        }

        private async Task TryCompleteFromUriAsync(string uriText)
        {
            if (_completed ||
                !HasTwitchOAuthPayload(uriText) ||
                !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            {
                return;
            }

            _completed = true;
            _statusText.Text = "Completing Twitch connection...";
            var result = await TwitchViewerCountService.Instance.CompleteImplicitAuthAsync(uri, _state);
            _completion.TrySetResult(result);
            Close();
        }

        private async Task TryCompleteFromBrowserLocationAsync(CoreWebView2 sender)
        {
            if (_completed)
                return;

            try
            {
                var json = await sender.ExecuteScriptAsync("window.location.href");
                var uriText = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                await TryCompleteFromUriAsync(uriText);
                if (!_completed && IsTwitchCallbackHost(uriText))
                    _statusText.Text = "Twitch returned to localhost without an access token. Press Reset, then Connect Twitch again.";
            }
            catch
            {
            }
        }

        private static bool IsTwitchCallbackHost(string uriText)
        {
            return Uri.TryCreate(uriText, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasTwitchOAuthPayload(string uriText)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return uri.Fragment.Contains("access_token=", StringComparison.OrdinalIgnoreCase) ||
                uri.Fragment.Contains("error=", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetUserDataFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZinkTwitchOAuthWebViewData");
        }

        private static int DipsToPixels(IntPtr hwnd, int dips)
        {
            try
            {
                return Math.Max(1, (int)Math.Round(dips * GetDpiForWindow(hwnd) / 96.0));
            }
            catch
            {
                return dips;
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}

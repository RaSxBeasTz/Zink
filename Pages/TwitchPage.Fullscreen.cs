using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class TwitchPage
    {
        private bool _twitchFullscreen;
        private DispatcherTimer? _twitchFullscreenLayoutTimer;
        private DispatcherTimer? _twitchFullscreenInputTimer;

        private void InitializeTwitchFullscreenInput()
        {
            // Restore browser focus after the borderless window and sidebar
            // have finished resizing.
            TwitchWebView.SizeChanged += (_, _) =>
            {
                if (_twitchFullscreen) QueueTwitchFullscreenInputRestore();
            };
            Unloaded += (_, _) => _twitchFullscreenInputTimer?.Stop();
        }

        private void QueueTwitchFullscreenInputRestore()
        {
            if (_twitchFullscreenInputTimer == null)
            {
                _twitchFullscreenInputTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                _twitchFullscreenInputTimer.Tick += async (_, _) =>
                {
                    _twitchFullscreenInputTimer.Stop();
                    if (!IsLoaded || !_isPlaybackTabActive ||
                        !ReferenceEquals(App.MainWindow.ContentFrame.Content, this)) return;
                    try
                    {
                        App.MainWindow.Activate();
                        TwitchWebView.UpdateLayout();
                        TwitchWebView.IsHitTestVisible = true;
                        var focused = TwitchWebView.Focus(FocusState.Programmatic);
                        if (TwitchWebView.CoreWebView2 != null)
                            await TwitchWebView.CoreWebView2.ExecuteScriptAsync("window.focus(); window.__zinkTwitchInputTrace?.();");
                        DiagnosticLogService.WriteLine("Twitch fullscreen input restored; browser focused: " + focused);
                    }
                    catch (Exception error)
                    {
                        DiagnosticLogService.WriteLine("Twitch fullscreen input: " + error);
                    }
                };
            }
            _twitchFullscreenInputTimer.Stop();
            _twitchFullscreenInputTimer.Start();
        }


        private void CoreWebView2_ContainsFullScreenElementChanged(CoreWebView2 sender, object args)
        {
            QueueTwitchFullscreenLayout();
        }

        private void QueueTwitchFullscreenLayout()
        {
            // Twitch and Chromium own the fullscreen element, controls, and Escape.
            // Let the native event callback unwind before resizing the XAML host.
            // Restart the timer to coalesce enter/exit notifications and read the
            // current browser state rather than replaying stale event arguments.
            if (_twitchFullscreenLayoutTimer == null)
            {
                _twitchFullscreenLayoutTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _twitchFullscreenLayoutTimer.Tick += (_, _) =>
                {
                    _twitchFullscreenLayoutTimer.Stop();
                    try
                    {
                        var fullscreen = TwitchWebView.CoreWebView2?.ContainsFullScreenElement == true;
                        if (fullscreen && !ReferenceEquals(App.MainWindow.ContentFrame.Content, this)) return;
                        SetTwitchFullscreen(fullscreen);
                    }
                    catch (Exception error)
                    {
                        DiagnosticLogService.WriteLine("Twitch fullscreen layout: " + error);
                    }
                };
            }
            _twitchFullscreenLayoutTimer.Stop();
            _twitchFullscreenLayoutTimer.Start();
        }
    }
}

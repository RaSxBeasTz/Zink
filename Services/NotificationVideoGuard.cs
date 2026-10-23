using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Zink.Services
{
    // Observes video playback without changing the page's player or navigation handlers.
    internal static class NotificationVideoGuard
    {
        private sealed class State { public bool Playing; public bool Installed; }
        private static readonly ConditionalWeakTable<WebView2, State> States = new();
        public static event Action? Changed;
        private const string Prefix = "__zink_notification_video:";
        private const string Script = @"
(() => {
    if (window.__zinkNotificationVideoGuard) return;
    window.__zinkNotificationVideoGuard = true;
    const key = Math.random().toString(36).slice(2);
    const frames = new Map();
    let last = null;
    function report() {
        const now = Date.now();
        for (const [id, value] of frames) if (now - value.when > 2500) frames.delete(id);
        const own = [...document.querySelectorAll('video')].some(v => !v.paused && !v.ended);
        const playing = own || [...frames.values()].some(v => v.playing);
        if (window !== window.top) {
            window.parent.postMessage({ zinkVideoGuard: true, key, playing }, '*');
        } else if (last !== playing && window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage('__zink_notification_video:' + playing);
        }
        last = playing;
    }
    window.addEventListener('message', e => {
        const value = e.data;
        if (!value || value.zinkVideoGuard !== true || typeof value.key !== 'string' || typeof value.playing !== 'boolean') return;
        frames.set(value.key, { playing: value.playing, when: Date.now() });
        report();
    });
    for (const type of ['play', 'playing', 'pause', 'ended', 'emptied']) document.addEventListener(type, report, true);
    window.addEventListener('pagehide', () => {
        if (window !== window.top) window.parent.postMessage({ zinkVideoGuard: true, key, playing: false }, '*');
    });
    setInterval(report, 1000);
    report();
})();";

        public static bool IsPlaying(DependencyObject? node)
        {
            if (node == null) return false;
            if (node is WebView2 view && States.TryGetValue(view, out var state) && state.Playing)
                return true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (IsPlaying(VisualTreeHelper.GetChild(node, i))) return true;
            return false;
        }

        public static void Track(WebView2 view)
        {
            if (States.TryGetValue(view, out _)) return;
            var state = new State();
            States.Add(view, state);
            view.CoreWebView2Initialized += async (_, _) => await InstallAsync(view, state);
            view.NavigationStarting += (_, _) => SetPlaying(state, false);
            view.Unloaded += (_, _) => SetPlaying(state, false);
            _ = InstallAsync(view, state);
        }

        public static void TrackTree(DependencyObject? node)
        {
            if (node == null) return;
            if (node is WebView2 view) { Track(view); return; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                TrackTree(VisualTreeHelper.GetChild(node, i));
        }

        private static async Task InstallAsync(WebView2 view, State state)
        {
            if (state.Installed || view.CoreWebView2 == null) return;
            state.Installed = true;
            try
            {
                view.CoreWebView2.WebMessageReceived += (_, args) =>
                {
                    try
                    {
                        var message = args.TryGetWebMessageAsString();
                        if (message == Prefix + "true") SetPlaying(state, true);
                        else if (message == Prefix + "false") SetPlaying(state, false);
                    }
                    catch { /* Other pages can post JSON messages. */ }
                };
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Script);
                await view.CoreWebView2.ExecuteScriptAsync(Script);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Notification video guard: " + ex.Message); }
        }

        public static async Task RefreshAsync(DependencyObject? node)
        {
            if (node == null) return;
            if (node is WebView2 view)
            {
                Track(view);
                if (view.CoreWebView2 != null && States.TryGetValue(view, out var state))
                {
                    try
                    {
                        var playing = await view.CoreWebView2.ExecuteScriptAsync(
                            "[...document.querySelectorAll('video')].some(v => !v.paused && !v.ended)");
                        // A top-level query must not reset a playing cross-origin frame.
                        if (playing == "true") SetPlaying(state, true);
                    }
                    catch { }
                }
                return;
            }
            // Capture children before awaiting; navigation may replace the tree.
            var children = new DependencyObject[VisualTreeHelper.GetChildrenCount(node)];
            for (var i = 0; i < children.Length; i++) children[i] = VisualTreeHelper.GetChild(node, i);
            foreach (var child in children) await RefreshAsync(child);
        }

        private static void SetPlaying(State state, bool playing)
        {
            if (state.Playing == playing) return;
            state.Playing = playing;
            Changed?.Invoke();
        }
    }
}

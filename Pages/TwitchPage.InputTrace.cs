using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Text.Json;

namespace Zink.Pages
{
    public sealed partial class TwitchPage
    {
        private static readonly string TwitchInputTracePath = Path.Combine(Path.GetTempPath(), "Zink-Twitch-input-trace.jsonl");

        private void InitializeTwitchInputTrace()
        {
            try { File.Delete(TwitchInputTracePath); } catch { }
            WriteTwitchInputTrace(new { type = "page-created", build = typeof(TwitchPage).Assembly.ManifestModule.ModuleVersionId });
            AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                if (!_twitchFullscreen) return;
                var point = e.GetCurrentPoint(TwitchWebView).Position;
                WriteTwitchInputTrace(new { type = "xaml-pointer", x = point.X, y = point.Y,
                    source = e.OriginalSource?.GetType().Name, focus = TwitchWebView.FocusState.ToString() });
            }), true);
            AddHandler(KeyDownEvent, new KeyEventHandler((_, _) =>
            {
                if (_twitchFullscreen)
                    WriteTwitchInputTrace(new { type = "xaml-key", focus = TwitchWebView.FocusState.ToString() });
            }), true);
        }

        private static void WriteTwitchInputTrace(object value)
        {
            try
            {
                if (File.Exists(TwitchInputTracePath) && new FileInfo(TwitchInputTracePath).Length > 12582912) return;
                File.AppendAllText(TwitchInputTracePath, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, data = value }) + Environment.NewLine);
            }
            catch { }
        }

        private void TwitchInputTrace_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!TryGetTwitchPageUri(args.Source, out _)) return;
            try
            {
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (message.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "zink-twitch-input")
                    WriteTwitchInputTrace(message.RootElement);
            }
            catch { }
        }

        private const string TwitchInputTraceScript = @"
(() => {
    if (location.hostname !== 'twitch.tv' && !location.hostname.endsWith('.twitch.tv')) return;
    if (window.__zinkTwitchInputTrace) return;
    let count = 0, lastMove = 0;
    const describe = node => {
        if (!(node instanceof Element)) return null;
        const css = getComputedStyle(node), rect = node.getBoundingClientRect();
        return { tag: node.tagName, target: node.getAttribute('data-a-target'),
            classes: String(node.className || '').slice(0, 160), inert: node.hasAttribute('inert'),
            pointer: css.pointerEvents, opacity: css.opacity, visibility: css.visibility,
            display: css.display, zIndex: css.zIndex, x: rect.x, y: rect.y, w: rect.width, h: rect.height };
    };
    function trace(type, event) {
        if (++count > 650) return;
        const buttons = [...document.querySelectorAll('.player-controls button, .video-player__controls button')]
            .filter(button => button.getBoundingClientRect().width > 0 &&
                ['player-play-pause-button', 'player-mute-unmute-button', 'player-settings-button', 'player-fullscreen-button'].includes(button.getAttribute('data-a-target'))).slice(0, 4)
            .map(button => {
                const rect = button.getBoundingClientRect();
                const ancestors = [];
                for (let node = button; node && ancestors.length < 6; node = node.parentElement) ancestors.push(describe(node));
                return { button: describe(button), hit: describe(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2)), ancestors };
            });
        window.chrome?.webview?.postMessage({ kind: 'zink-twitch-input', type,
            focused: document.hasFocus(), active: describe(document.activeElement),
            fullscreen: describe(document.fullscreenElement), width: innerWidth, height: innerHeight, scale: devicePixelRatio,
            target: describe(event?.target), x: event?.clientX, y: event?.clientY,
            hit: event && Number.isFinite(event.clientX) ? document.elementsFromPoint(event.clientX, event.clientY).slice(0, 6).map(describe) : null,
            buttons });
    }
    window.__zinkTwitchInputTrace = () => trace('host-snapshot');
    for (const type of ['pointerdown', 'pointerup', 'click', 'keydown', 'focus', 'blur'])
        window.addEventListener(type, event => trace(type, event), true);
    window.addEventListener('pointermove', event => {
        if (!document.fullscreenElement || Date.now() - lastMove < 600) return;
        lastMove = Date.now(); trace('pointermove', event);
    }, true);
    window.addEventListener('pointerenter', event => trace('pointerenter', event), true);
    window.addEventListener('pointerleave', event => trace('pointerleave', event), true);
    document.addEventListener('fullscreenchange', () => {
        trace('fullscreenchange');
        for (const delay of [250, 1000, 3000]) setTimeout(() => trace('fullscreen-settled'), delay);
    });
})();";
    }
}

using System;
using System.Threading.Tasks;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class TwitchPage
    {
        private bool _isPlaybackTabActive = true;

        internal async Task SetPlaybackTabActiveAsync(bool active)
        {
            _isPlaybackTabActive = active;
            if (TwitchWebView.CoreWebView2 == null) return;
            try
            {
                await TwitchWebView.CoreWebView2.ExecuteScriptAsync(
                    "window.__zinkTwitchSetTabActive?.(" + (active ? "true" : "false") + ");");
            }
            catch (System.Exception error)
            {
                DiagnosticLogService.WriteLine("Twitch tab playback state: " + error.Message);
            }
        }

        // Twitch can pause through focus/player logic as well as document visibility.
        // Remember playback intent and repair only automatic pauses; never change mute/volume.
        private const string TwitchBackgroundPlaybackScript = @"
(() => {
    const host = location.hostname;
    if (host !== 'twitch.tv' && !host.endsWith('.twitch.tv')) return;
    if (window.__zinkTwitchBackgroundPlaybackInstalled) return;
    window.__zinkTwitchBackgroundPlaybackInstalled = true;

    for (const [property, value] of [
        ['hidden', false], ['visibilityState', 'visible'],
        ['webkitHidden', false], ['webkitVisibilityState', 'visible']
    ]) {
        try { Object.defineProperty(document, property, { configurable: true, get: () => value }); } catch {}
    }
    for (const name of ['visibilitychange', 'webkitvisibilitychange']) {
        window.addEventListener(name, event => event.stopImmediatePropagation(), true);
    }

    const states = new WeakMap();
    let tabActive = true;
    let windowFocused = document.hasFocus();
    let transitionUntil = 0;
    const background = () => !tabActive || !windowFocused;
    function stateFor(video) {
        let state = states.get(video);
        if (!state) {
            state = { wanted: !video.paused && !video.ended, pending: false };
            states.set(video, state);
        }
        return state;
    }
    function resume(video) {
        const state = stateFor(video);
        if (!state.wanted || !video.paused || video.ended || video.readyState < 2 || state.pending) return;
        state.pending = true;
        try {
            Promise.resolve(video.play()).catch(() => {}).finally(() => { state.pending = false; });
        } catch { state.pending = false; }
    }
    const videos = () => [...document.querySelectorAll('video')];
    document.addEventListener('play', event => {
        if (event.target instanceof HTMLVideoElement) stateFor(event.target).wanted = true;
    }, true);
    document.addEventListener('pause', event => {
        if (!(event.target instanceof HTMLVideoElement)) return;
        const video = event.target;
        if (background() || Date.now() < transitionUntil) resume(video);
        else stateFor(video).wanted = false;
    }, true);
    document.addEventListener('ended', event => {
        if (event.target instanceof HTMLVideoElement) stateFor(event.target).wanted = false;
    }, true);

    // Record explicit play/pause controls BEFORE their media event fires. A media pause event
    // alone cannot distinguish a viewer's click from Twitch's background-pause code.
    function recordToggle(target) {
        const player = target.closest?.('[data-a-target=video-player], .video-player');
        const video = target instanceof HTMLVideoElement ? target : player?.querySelector('video') || videos()[0];
        if (video) stateFor(video).wanted = video.paused;
    }
    document.addEventListener('pointerdown', event => {
        const target = event.target;
        if (target instanceof HTMLVideoElement || target.closest?.('[data-a-target=player-play-pause-button], [data-test-selector=player-play-pause-button]'))
            recordToggle(target);
    }, true);
    document.addEventListener('keydown', event => {
        if (event.repeat || event.ctrlKey || event.altKey || event.metaKey) return;
        if (event.target.closest?.('input, textarea, select, [contenteditable]')) return;
        const playerSpace = event.key === ' ' && (event.target.tagName === 'BODY' || event.target.tagName === 'HTML' ||
            event.target.closest?.('[data-a-target=video-player], .video-player'));
        if (event.key?.toLowerCase() === 'k' || event.key === 'MediaPlayPause' || playerSpace)
            recordToggle(event.target);
    }, true);

    // Called by the host BEFORE collapsing a tab so focus/visibility pauses cannot win the race.
    window.__zinkTwitchSetTabActive = active => {
        for (const video of videos()) stateFor(video);
        tabActive = Boolean(active);
        transitionUntil = Date.now() + 1500;
        for (const video of videos()) resume(video);
    };
    window.addEventListener('blur', () => {
        for (const video of videos()) stateFor(video);
        windowFocused = false;
    }, true);
    window.addEventListener('focus', () => {
        windowFocused = true;
        transitionUntil = Date.now() + 1500;
        for (const video of videos()) resume(video);
    }, true);
    setInterval(() => {
        for (const video of videos()) {
            stateFor(video);
            if (background() || Date.now() < transitionUntil) resume(video);
        }
    }, 1000);
})();
";
    }
}

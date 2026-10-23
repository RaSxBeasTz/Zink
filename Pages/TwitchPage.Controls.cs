namespace Zink.Pages
{
    public sealed partial class TwitchPage
    {
        private const string TwitchControlsGuardScript = @"
(() => {
    if (location.hostname !== 'twitch.tv' && !location.hostname.endsWith('.twitch.tv')) return;
    if (window.__zinkTwitchControlsGuardInstalled) return;
    window.__zinkTwitchControlsGuardInstalled = true;

    // Preserve Twitch's own layout and hit-testing. Moving the controls with
    // CSS transforms or forcing every ancestor visible can make buttons appear
    // clickable while Chromium still routes input to an overlay.
    const style = document.createElement('style');
    style.textContent = `
        :fullscreen .player-controls,
        :fullscreen .video-player__controls,
        :fullscreen [data-a-target='player-controls'],
        :fullscreen [data-test-selector='player-controls'],
        [data-zink-twitch-fullscreen] .player-controls,
        [data-zink-twitch-fullscreen] .video-player__controls,
        [data-zink-twitch-fullscreen] [data-a-target='player-controls'],
        [data-zink-twitch-fullscreen] [data-test-selector='player-controls'] {
            visibility: visible !important;
            pointer-events: auto !important;
        }
    `;
    function install() {
        if (!style.isConnected) (document.head || document.documentElement)?.appendChild(style);
    }
    // The host also supplies its fullscreen state after resizing the WebView.
    // Keep this independent of the browser's fullscreen pseudo-class timing.
    window.__zinkTwitchControlsGuard = enabled => {
        install();
        document.documentElement?.toggleAttribute('data-zink-twitch-fullscreen', Boolean(enabled));
    };
    document.addEventListener('fullscreenchange', () => {
        window.__zinkTwitchControlsGuard(Boolean(document.fullscreenElement));
    });
    install();
    document.addEventListener('DOMContentLoaded', install, { once: true });
})();";
    }
}

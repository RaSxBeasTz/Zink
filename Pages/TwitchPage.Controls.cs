namespace Zink.Pages
{
    public sealed partial class TwitchPage
    {
        private const string TwitchControlsGuardScript = @"
(() => {
    if (location.hostname !== 'twitch.tv' && !location.hostname.endsWith('.twitch.tv')) return;
    if (window.__zinkTwitchControlsGuardInstalled) return;
    window.__zinkTwitchControlsGuardInstalled = true;

    // Browser fullscreen must own its layout, hit testing and popover stacking.
    // Do not override Twitch's native fullscreen player CSS. In particular,
    // transforms, z-index and pointer-events rules break settings/quality menus.
    const style = document.createElement('style');
    style.textContent = `
        /* Keep Twitch's own controls and menus in their native positions.
           WinUI fullscreen can prevent Twitch's hover-to-reveal state from
           activating, so reveal only the existing control layer. */
        html[data-zink-twitch-fullscreen] .player-controls,
        html[data-zink-twitch-fullscreen] .player-controls__left-control-group,
        html[data-zink-twitch-fullscreen] .player-controls__right-control-group,
        html[data-zink-twitch-fullscreen] [data-a-target="player-controls"] {
            opacity: 1 !important;
            visibility: visible !important;
            pointer-events: auto !important;
        }
        html[data-zink-twitch-fullscreen] .player-controls button,
        html[data-zink-twitch-fullscreen] .player-controls input,
        html[data-zink-twitch-fullscreen] .player-controls [role="slider"] {
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

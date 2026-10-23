namespace Zink.Pages
{
    public sealed partial class TwitchPage
    {
        private const string TwitchControlsGuardScript = @"
(() => {
    if (location.hostname !== 'twitch.tv' && !location.hostname.endsWith('.twitch.tv')) return;
    if (window.__zinkTwitchControlsGuardInstalled) return;
    window.__zinkTwitchControlsGuardInstalled = true;

    const controls = `:is(.player-controls, .video-player__controls,
        [data-a-target='player-controls'], [data-test-selector='player-controls'])`;
    const scope = ':is(:fullscreen, [data-zink-twitch-fullscreen])';
    const style = document.createElement('style');
    style.textContent = `
        /* An opaque child cannot reveal itself through a transparent parent. */
        ${scope}:has(${controls}),
        ${scope} :has(${controls}),
        ${scope} ${controls} {
            opacity: 1 !important;
            visibility: visible !important;
        }
        /* Keep Twitch's original controls above the host's non-interactive
           bottom edge. Translate the existing strip, including its popovers,
           so its displayed bounds and browser hit-testing bounds stay together. */
        ${scope} ${controls} {
            translate: 0 -80px !important;
        }
        ${scope} ${controls},
        ${scope} ${controls} :is(button, input, select, [role='button'], [role='slider'], a[href]) {
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

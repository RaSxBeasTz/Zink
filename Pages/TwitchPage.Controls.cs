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
    style.textContent = '';
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

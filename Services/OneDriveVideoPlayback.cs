using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;

namespace Zink.Services
{
    internal static class OneDriveVideoPlayback
    {
        public static async Task InstallAsync(CoreWebView2 core, Action<bool> setFullscreen)
        {
            core.NavigationStarting += (_, __) =>
            {
                if (Uri.TryCreate(core.Source, UriKind.Absolute, out var previous) && IsOneDriveHost(previous.Host))
                    setFullscreen(false);
            };
            core.WebMessageReceived += (_, args) =>
            {
                if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
                    source.Scheme != Uri.UriSchemeHttps || !IsOneDriveHost(source.Host))
                    return;

                try
                {
                    var message = args.TryGetWebMessageAsString();
                    if (message == "zink-onedrive-video:enter") setFullscreen(true);
                    else if (message == "zink-onedrive-video:exit") setFullscreen(false);
                }
                catch { }
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Script);
        }

        private static bool IsOneDriveHost(string host) =>
            host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("onedrive.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase);

        // Short sharing links redirect normally, preserving OneDrive sign-in and permissions.
        internal const string Script = """
(() => {
    const host = location.hostname.toLowerCase();
    if (location.protocol !== 'https:' ||
        !(host === 'onedrive.live.com' || host === 'onedrive.com' || host.endsWith('.onedrive.com'))) return;
    if (window.__zinkOneDriveVideo) return;
    window.__zinkOneDriveVideo = true;
    const handled = new WeakSet();
    let active = null;
    let exitButton = null;
    let resolutionPanel = null;
    let resolutionTimer = null;
    const qualityStates = new WeakMap();
    const post = state => window.chrome?.webview?.postMessage('zink-onedrive-video:' + state);
    const exit = () => {
        if (!active) return;
        active.classList.remove('zink-onedrive-video');
        document.querySelectorAll('.zink-onedrive-ancestor').forEach(e => e.classList.remove('zink-onedrive-ancestor'));
        exitButton?.remove();
        exitButton = null;
        resolutionPanel?.remove();
        resolutionPanel = null;
        clearInterval(resolutionTimer);
        resolutionTimer = null;
        active = null;
        if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
        post('exit');
    };
    const enter = video => {
        if (active === video) return;
        exit();
        active = video;
        video.controls = true;
        video.classList.add('zink-onedrive-video');
        for (let parent = video.parentElement; parent; parent = parent.parentElement)
            parent.classList.add('zink-onedrive-ancestor');
        exitButton = document.createElement('button');
        exitButton.className = 'zink-onedrive-exit';
        exitButton.textContent = 'Exit fullscreen (Esc)';
        exitButton.addEventListener('click', exit);
        document.body.appendChild(exitButton);
        showResolutionControls(video);
        post('enter');
    };
    const visible = e => !!e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden';
    const label = e => (e.getAttribute('aria-label') || e.getAttribute('title') || e.textContent || '').trim();
    const setQuality = async (video, requested = 'highest') => {
        // Use only the options offered by the actual player; never replace authenticated media URLs.
        let state = qualityStates.get(video);
        if (!state) {
            state = { options: [], selected: 'highest', busy: false };
            qualityStates.set(video, state);
        }
        if (state.busy) return;
        state.busy = true;
        let settings = null;
        try {
        const controls = 'button,[role="button"],[role="menuitem"],[role="menuitemradio"]';
        const isSettings = e => !e.closest('.zink-onedrive-resolution') && visible(e) &&
            /\b(settings|playback options|video quality|quality settings)\b/i.test(label(e));
        // OneDrive can put its toolbar several ancestors above the video, and portal its menus to body.
        for (let root = video.parentElement; root && !settings; root = root.parentElement)
            settings = [...root.querySelectorAll(controls)].find(isSettings);
        if (!settings && document.querySelectorAll('video').length === 1)
            settings = [...document.querySelectorAll(controls)].find(isSettings);
        if (!settings) return;
        if (settings.getAttribute('aria-expanded') !== 'true') settings.click();
        const delay = () => new Promise(resolve => setTimeout(resolve, 150));
        const menuItems = () => [...document.querySelectorAll(
            '[role="menuitem"],[role="menuitemradio"],[role="option"],button,[role="button"],input[type="radio"]')]
            .filter(e => visible(e) && !e.closest('.zink-onedrive-resolution'));
        const waitFor = async predicate => {
            for (let attempt = 0; attempt < 10; attempt++) {
                const result = predicate();
                if (result) return result;
                await delay();
            }
            return null;
        };
        const resolution = e => label(e).match(/\b(\d{3,4})\s*p\b/i);
        const quality = await waitFor(() => menuItems().find(e => /^(video quality|quality|resolution)(\b|$)/i.test(label(e))) ||
            (menuItems().some(resolution) ? settings : null));
        if (quality) {
            if (quality !== settings) quality.click();
            await waitFor(() => menuItems().some(resolution));
            const options = menuItems().map(e => ({ e, text: label(e), match: resolution(e) }))
                .filter(item => item.match || /^auto(matic)?\b/i.test(item.text))
                .map(item => ({ ...item, value: item.match ? item.match[1] + 'p' : 'auto', height: item.match ? Number(item.match[1]) : 0 }))
                .sort((a, b) => b.height - a.height);
            state.options = options.filter((item, index) => options.findIndex(other => other.value === item.value) === index)
                .map(item => ({ value: item.value, text: item.text }));
            const choice = requested === 'highest'
                ? options.find(item => item.height > 0)
                : options.find(item => item.value === requested);
            if (choice) {
                choice.e.click();
                state.selected = requested;
            }
        }
        } finally {
            if (settings?.getAttribute('aria-expanded') === 'true') settings.click();
            state.busy = false;
        }
    };
    const showResolutionControls = video => {
        const panel = document.createElement('div');
        panel.className = 'zink-onedrive-resolution';
        const current = document.createElement('span');
        current.setAttribute('aria-live', 'polite');
        const caption = document.createElement('label');
        caption.textContent = 'Playback resolution ';
        const select = document.createElement('select');
        select.setAttribute('aria-label', 'Playback resolution');
        caption.appendChild(select);
        const refresh = document.createElement('button');
        refresh.textContent = 'Refresh resolutions';
        panel.append(current, caption, refresh);
        const updateCurrent = () => {
            const text = video.videoWidth && video.videoHeight
                ? `Playing: ${video.videoWidth} × ${video.videoHeight}`
                : 'Playing: resolution pending';
            if (current.textContent !== text) current.textContent = text;
        };
        const populate = () => {
            const state = qualityStates.get(video);
            select.replaceChildren();
            const add = (value, text) => {
                const option = document.createElement('option');
                option.value = value;
                option.textContent = text;
                select.appendChild(option);
            };
            add('highest', state?.options.some(item => item.value !== 'auto')
                ? 'Highest available' : 'Current stream (no quality menu detected)');
            for (const item of state?.options || []) add(item.value, item.text);
            select.value = state?.selected || 'highest';
            select.disabled = !state?.options.length;
        };
        const apply = async requested => {
            select.disabled = true;
            refresh.disabled = true;
            try { await setQuality(video, requested); }
            catch { /* Keep the actual decoded resolution visible if the site changes its menu. */ }
            finally {
                populate();
                refresh.disabled = false;
                updateCurrent();
            }
        };
        select.addEventListener('change', () => apply(select.value));
        refresh.addEventListener('click', () => apply(qualityStates.get(video)?.selected || 'highest'));
        populate();
        updateCurrent();
        let retryCount = 0;
        let retrying = false;
        resolutionTimer = setInterval(() => {
            updateCurrent();
            // Player settings often arrive after metadata and the first decoded frame.
            if (!qualityStates.get(video)?.options.length && !retrying && retryCount < 3 && video.readyState >= 2) {
                retryCount++;
                retrying = true;
                apply('highest').finally(() => { retrying = false; });
            }
        }, 2000);
        resolutionPanel = panel;
        document.body.appendChild(panel);
    };
    const start = async video => {
        if (handled.has(video) || !visible(video)) return;
        if (!video.currentSrc && !video.src && !video.querySelector('source')) return;
        handled.add(video);
        await setQuality(video).catch(() => {});
        if (!video.isConnected) return;
        // If autoplay is blocked, the next real Play click still enters fullscreen.
        video.addEventListener('playing', () => enter(video), { once: true });
        video.play().then(() => enter(video)).catch(() => {});
    };
    const scan = () => {
        if (active && (!active.isConnected || !visible(active))) exit();
        document.querySelectorAll('video').forEach(start);
    };
    const init = () => {
        const style = document.createElement('style');
        style.textContent = `
            .zink-onedrive-ancestor { transform:none!important; filter:none!important; perspective:none!important; contain:none!important; overflow:visible!important; }
            video.zink-onedrive-video { position:fixed!important; inset:0!important; width:100vw!important; height:100vh!important; max-width:none!important; max-height:none!important; z-index:2147483646!important; object-fit:contain!important; background:black!important; }
            .zink-onedrive-exit { position:fixed; top:12px; right:12px; z-index:2147483647; padding:8px 12px; color:white; background:#222; border:1px solid #888; border-radius:6px; cursor:pointer; }
            .zink-onedrive-resolution { position:fixed; top:12px; left:12px; max-width:calc(100vw - 240px); z-index:2147483647; display:flex; flex-wrap:wrap; align-items:center; gap:10px; padding:8px 12px; color:white; background:#222e; border:1px solid #888; border-radius:6px; font:14px system-ui; }
            .zink-onedrive-resolution select, .zink-onedrive-resolution button { color:white; background:#222; border:1px solid #888; border-radius:4px; padding:5px; font:inherit; }
        `;
        document.head.appendChild(style);
        let queued = false;
        new MutationObserver(() => {
            if (queued) return;
            queued = true;
            setTimeout(() => { queued = false; scan(); }, 200);
        }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
        document.addEventListener('loadedmetadata', scan, true);
        document.addEventListener('keydown', e => { if (e.key === 'Escape') exit(); }, true);
        window.addEventListener('pagehide', exit);
        scan();
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once: true });
    else init();
})();
""";
    }
}

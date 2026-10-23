namespace Zink.Services
{
    internal static class OneDriveStreamingControls
    {
        // Installed only in the dedicated page. Keep the authenticated media element and its source intact.
        internal const string Script = """
(() => {
    const host = location.hostname.toLowerCase();
    if (location.protocol !== 'https:' || !(host === 'onedrive.live.com' || host === 'onedrive.com' || host.endsWith('.onedrive.com'))) return;
    if (window.__zinkStreamingControls) return;
    window.__zinkStreamingControls = true;
    let video = null;
    let panel = null;
    // A maximum z-index still sits below a fullscreen element or a site's stacking context.
    // Manual popovers put our controls in the browser's top layer without stealing focus.
    const present = element => {
        if (!element.isConnected) document.documentElement.appendChild(element);
        if (typeof element.showPopover === 'function') {
            element.setAttribute('popover', 'manual');
            if (!element.matches(':popover-open')) {
                try { element.showPopover(); } catch { }
            }
        }
    };
    const formatTime = value => {
        if (!Number.isFinite(value)) return '--:--';
        const seconds = Math.max(0, Math.floor(value));
        const hours = Math.floor(seconds / 3600);
        return (hours ? hours + ':' : '') + String(Math.floor(seconds / 60) % 60).padStart(2, '0') + ':' + String(seconds % 60).padStart(2, '0');
    };
    const mount = target => {
        panel?.remove();
        video = target;
        panel = document.createElement('div');
        panel.className = 'zink-stream-controls';
        const makeButton = (text, action) => {
            const button = document.createElement('button');
            button.textContent = text;
            button.addEventListener('click', action);
            panel.appendChild(button);
            return button;
        };
        const play = makeButton('Pause', () => {
            if (video.paused) video.play().catch(() => {});
            else video.pause();
        });
        const seekBy = seconds => {
            if (Number.isFinite(video.duration)) video.currentTime = Math.max(0, Math.min(video.duration, video.currentTime + seconds));
        };
        makeButton('−10s', () => seekBy(-10));
        makeButton('+10s', () => seekBy(10));
        const seek = document.createElement('input');
        seek.type = 'range'; seek.min = '0'; seek.max = '1000'; seek.step = '1';
        seek.setAttribute('aria-label', 'Seek video'); seek.className = 'zink-stream-seek';
        seek.addEventListener('input', () => {
            if (Number.isFinite(video.duration)) video.currentTime = Number(seek.value) / 1000 * video.duration;
        });
        panel.appendChild(seek);
        const time = document.createElement('span'); panel.appendChild(time);
        const mute = makeButton('Mute', () => { video.muted = !video.muted; });
        const volume = document.createElement('input');
        volume.type = 'range'; volume.min = '0'; volume.max = '1'; volume.step = '0.05'; volume.value = String(video.volume);
        volume.setAttribute('aria-label', 'Volume'); volume.style.width = '90px';
        volume.addEventListener('input', () => { video.volume = Number(volume.value); video.muted = video.volume === 0; });
        panel.appendChild(volume);
        const speed = document.createElement('select'); speed.setAttribute('aria-label', 'Playback speed');
        for (const value of [0.5, 0.75, 1, 1.25, 1.5, 2]) {
            const option = document.createElement('option'); option.value = String(value); option.textContent = value + '×'; speed.appendChild(option);
        }
        speed.value = String(video.playbackRate);
        speed.addEventListener('change', () => { video.playbackRate = Number(speed.value); });
        panel.appendChild(speed);
        const error = document.createElement('span'); error.setAttribute('role', 'status'); panel.appendChild(error);
        const update = () => {
            const showing = video.isConnected && video.classList.contains('zink-onedrive-video');
            panel.hidden = !showing;
            if (!showing) {
                if (panel.matches(':popover-open')) panel.hidePopover();
                video.controls = true;
                return;
            }
            present(panel);
            for (const overlay of document.querySelectorAll('.zink-onedrive-resolution,.zink-onedrive-exit'))
                present(overlay);
            const bounds = panel.getBoundingClientRect();
            // Never leave the user without controls if an overlay fails to render.
            video.controls = !(bounds.width > 0 && bounds.height > 0 &&
                bounds.top < innerHeight && bounds.bottom > 0 && getComputedStyle(panel).visibility !== 'hidden');
            play.textContent = video.paused ? 'Play' : 'Pause';
            mute.textContent = video.muted ? 'Unmute' : 'Mute';
            time.textContent = formatTime(video.currentTime) + ' / ' + formatTime(video.duration);
            seek.disabled = !Number.isFinite(video.duration) || video.duration <= 0;
            if (document.activeElement !== seek) seek.value = seek.disabled ? '0' : String(video.currentTime / video.duration * 1000);
            if (document.activeElement !== volume) volume.value = String(video.volume);
            error.textContent = video.error ? 'Playback error. Reopen the sharing link to retry.' : '';
        };
        document.documentElement.appendChild(panel);
        return update;
    };
    const init = () => {
        const style = document.createElement('style');
        style.textContent = `
            .zink-stream-controls { position:fixed!important; top:auto!important; bottom:0!important; left:0!important; right:0!important; width:auto!important; height:auto!important; max-height:50vh; overflow:auto; margin:0!important; box-sizing:border-box; border:0; z-index:2147483647; display:flex; align-items:center; flex-wrap:wrap; gap:10px; padding:18px; background:linear-gradient(transparent,#101820 25%); color:white; visibility:visible!important; opacity:1!important; font:14px system-ui; }
            .zink-stream-controls[hidden] { display:none; }
            .zink-onedrive-resolution[popover], .zink-onedrive-exit[popover] { margin:0!important; bottom:auto!important; width:fit-content!important; height:auto!important; visibility:visible!important; opacity:1!important; }
            .zink-onedrive-resolution[popover] { right:auto!important; }
            .zink-onedrive-exit[popover] { left:auto!important; }
            .zink-stream-controls button,.zink-stream-controls select { color:white; background:#152933; border:1px solid #42616c; border-radius:7px; padding:8px 12px; font:inherit; cursor:pointer; }
            .zink-stream-controls input { accent-color:#11d8e8; }
            .zink-stream-seek { flex:1; min-width:120px; }
        `;
        document.head.appendChild(style);
        let update = () => {};
        document.addEventListener('fullscreenchange', () => {
            // Reinsert after the fullscreen element so controls remain above its top layer.
            for (const overlay of [panel, ...document.querySelectorAll('.zink-onedrive-resolution,.zink-onedrive-exit')]) {
                if (overlay?.matches(':popover-open')) overlay.hidePopover();
            }
            update();
        });
        const timer = setInterval(() => {
            const target = document.querySelector('video.zink-onedrive-video');
            if (target && target !== video) update = mount(target);
            update();
        }, 250);
        window.addEventListener('pagehide', () => { clearInterval(timer); panel?.remove(); });
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once:true });
    else init();
})();
""";
    }
}

(config => {
    'use strict';
    // Runs only inside the NAS desktop, never in the file-service document or a frame.
    const trusted = () => window === window.top && location.protocol === 'https:' &&
        location.hostname.endsWith('.ug.link') && location.pathname.toLowerCase() === '/desktop/' &&
        (!location.port || location.port === '443') && !location.username && !location.password;
    if (!trusted()) return false;
    const key = JSON.stringify([config.expectedOrigin, config.tileName, config.tileId]);
    const previous = window.__ugreenRemoteDriveDesktop;
    const alreadyClicked = previous?.key === key && previous.clicked;
    previous?.dispose();

    const normalize = value => (value || '').trim().replace(/\s+/g, ' ').toLocaleLowerCase();
    const visible = element => element && !element.hidden && element.getClientRects().length > 0 &&
        getComputedStyle(element).visibility !== 'hidden' && getComputedStyle(element).display !== 'none';
    const seenCheckboxes = new WeakSet();
    let clicked = !!alreadyClicked;
    let disposed = false;
    let scheduled;
    const post = selection => window.chrome?.webview?.postMessage({
        kind: 'ugreen-desktop-selection', id: selection.id, name: selection.name
    });
    const describe = tile => ({ id: tile.getAttribute('moduleid') || '',
        name: (tile.querySelector('.app-name')?.textContent || '').trim().slice(0, 128) });
    const recordSelection = event => {
        if (!trusted() || !(event.target instanceof Element)) return;
        const tile = event.target.closest('.apply-list .list-item');
        post(tile ? describe(tile) : { id: '', name: '' });
    };
    // Capture runs before UGOS's Vue click handler opens the shortcut popup.
    document.addEventListener('click', recordSelection, true);

    const exactShortcut = anchor => {
        let url;
        try { url = new URL(anchor.href, location.href); } catch { return false; }
        const root = url.pathname === '/' && !url.search;
        const bootstrap = url.pathname === '/api/ugreen/auth' && url.search.length > 1 && url.search.length <= 2049;
        return url.protocol === 'https:' && url.origin === config.expectedOrigin && !url.username && !url.password &&
            (!url.port || url.port === '443') && !url.hash && (root || bootstrap);
    };
    const findTile = () => {
        const tiles = Array.from(document.querySelectorAll('.apply-list .list-item')).filter(visible);
        // A learned module id is more reliable than a translated/renamed display label.
        if (config.tileId) {
            const matches = tiles.filter(tile => tile.getAttribute('moduleid') === config.tileId);
            return matches.length === 1 ? matches[0] : null;
        }
        const name = normalize(config.tileName);
        const exact = tiles.filter(tile => normalize(describe(tile).name) === name);
        if (exact.length > 0) return exact.length === 1 ? exact[0] : null;
        const defaults = ['remote drive', 'ugreen remote drive', 'ugreen-remote-drive'];
        const names = defaults.includes(name) ? defaults : [name];
        const matches = tiles.filter(tile => names.includes(normalize(describe(tile).name)));
        // Never pick the first of several equally named Docker shortcuts.
        return matches.length === 1 ? matches[0] : null;
    };
    const tick = () => {
        scheduled = undefined;
        if (disposed || !trusted()) return;
        // UGOS's own "Keep me logged in" control; do not read credentials or tokens.
        if (config.rememberLogin) {
            for (const checkbox of document.querySelectorAll('.is-login .login-public-checkbox input[type="checkbox"]')) {
                if (!visible(checkbox.closest('.login-public-checkbox')) || checkbox.disabled || seenCheckboxes.has(checkbox)) continue;
                seenCheckboxes.add(checkbox);
                if (!checkbox.checked) checkbox.click();
            }
        }
        if (clicked || !config.autoOpen || !config.expectedOrigin ||
            Array.from(document.querySelectorAll('input[type="password"]')).some(visible)) return;
        const anchors = Array.from(document.querySelectorAll('a[href]')).filter(a => visible(a) && exactShortcut(a));
        if (anchors.length > 1) return;
        const anchor = anchors[0];
        const tile = anchor ? null : findTile();
        const target = anchor || tile?.querySelector('.click-region') || tile?.querySelector('.app-name');
        if (!visible(target)) return;
        clicked = true; // Mark before invoking the page; mutations must not issue a second click.
        if (tile) post(describe(tile));
        const previousTarget = anchor?.getAttribute('target');
        if (anchor) anchor.setAttribute('target', '_blank');
        try { target.click(); }
        finally {
            if (anchor) {
                if (previousTarget === null) anchor.removeAttribute('target');
                else anchor.setAttribute('target', previousTarget);
            }
        }
    };
    const schedule = () => {
        if (!disposed && scheduled === undefined) scheduled = setTimeout(tick, 75);
    };
    const observer = new MutationObserver(schedule);
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true,
        attributeFilter: ['href', 'class', 'style', 'hidden', 'disabled', 'moduleid'], characterData: true });
    window.addEventListener('hashchange', schedule);
    window.addEventListener('popstate', schedule);
    const expiry = setTimeout(() => api.dispose(), config.waitMilliseconds);
    const api = {
        key,
        get clicked() { return clicked; },
        dispose() {
            disposed = true;
            observer.disconnect();
            clearTimeout(expiry);
            clearTimeout(scheduled);
            document.removeEventListener('click', recordSelection, true);
            window.removeEventListener('hashchange', schedule);
            window.removeEventListener('popstate', schedule);
        }
    };
    window.__ugreenRemoteDriveDesktop = api;
    tick();
    return true;
})(__CONFIG__);

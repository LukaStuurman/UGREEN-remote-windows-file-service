# Easier UGREENlink connection

## First-time setup without copying the Docker app address

The client accepts either:

- the NAS's UGREENlink web address, normally `https://ug.link/<UGREENlink-ID>`; or
- the Docker desktop shortcut's HTTPS address ending in `.ugapp.link` or `.ugdocker.link`.

With the NAS address, the user clicks **Verbinden en schijf openen**, signs in in the client's embedded browser, then clicks the **Remote Drive** tile on the UGOS desktop. The client recognizes only a user-initiated tile popup from the trusted UGREENlink desktop and only when the destination is the exact allowed Remote Drive shortcut root or its one-time auth-bootstrap route. It then stores the HTTPS origin and continues in the same window.

The NAS address and discovered shortcut origin are saved in `%LOCALAPPDATA%\UGREEN Remote Drive\config.json`. The app address field is repopulated from those settings on the next start. The user can enable Windows sign-in startup and automatic mounting for a one-step reconnect when the saved UGREENlink session and shortcut are available.

## Remembered sign-in

The fixed `%LOCALAPPDATA%\UGREEN Remote Drive\WebView2` profile is reused across app exits, installer updates, and Windows restarts. With **Aanmelding onthouden** enabled (default), the browser's native password-save prompt is enabled and the helper selects UGOS's own Keep me logged in checkbox. It never reads password fields, cookie values, local-storage tokens, or the password database. Password saving is handled by the dedicated browser profile after the user accepts its native prompt, not by app configuration. Existing short-lived NAS sessions may require one new sign-in with Keep me logged in enabled. Server expiry, revocation, MFA, and password changes still apply.

**Wis lokale aanmelding** clears this app profile's cookies, site storage, saved passwords and autofill data. Disabling the remember preference alone does not erase existing data. The dedicated browser is disposed on a full tray exit. No Edge state is imported.

## Automatic tile opening on reconnect

With a known shortcut origin the helper watches the trusted `/desktop/` document for up to fifteen minutes, including the login form and subsequent SPA route changes. It activates at most one uniquely matched shortcut per document/configuration: an exact root/bootstrap link, or the real desktop tile's `.click-region`. It does not click while a visible password form is present. A matching popup is allowed only once, from that exact trusted desktop origin to the exact saved service origin and allowed root/bootstrap path.

The actual publicly served UGOS frontend inspected on 2026-10-15 renders desktop icons as `.apply-list .list-item`, their display names as `.app-name`, their stable IDs as `moduleid`, and opens apps from a click on `.click-region` or `.app-name`. The web-login session checkbox is inside `.is-login .login-public-checkbox`. These generic selectors are used instead of assuming every desktop shortcut is an anchor. The desktop source uses a single click for these icons; no repeated/double click is issued. They are frontend compatibility selectors, not a documented UGREEN activation API, and may change with firmware.

The default exact label is `ugreen-remote-drive`. **Naam van de NAS-tegel** allows a different label; the common default names `Remote Drive` and `UGREEN Remote Drive` are accepted as fallbacks only when uniquely matched. A recent desktop selection is learned only after the resulting popup passes destination validation; only its stable module ID and display name enter local settings. A learned ID takes priority over the name, and missing/ambiguous identities are not guessed. First-time discovery still requires one manual click because the generated service origin is not known yet. No personal NAS address, account, or shortcut is hardcoded.

## Ticket and sign-in handling

- The app does not read Edge history, passwords, or cookies. Sign-in and optional native password saving use the app's dedicated WebView2 profile.
- The one-time shortcut query is held only long enough to finish UGREENlink's bootstrap. It is not copied to settings, logs, or release notes. Bootstrap cleanup removes browsing history and disk cache while preserving sign-in cookies.
- The file API remains bound to the configured shortcut origin; a shortcut URL learned during discovery is validated again before navigation.
- If UGREENlink's own session/tunnel needs activation again, the helper waits for the embedded desktop and activates its exact saved tile/link. If it cannot identify a unique tile, the user can correct the configured label or select the tile once; the approved result is routed into the same app window.

UGREEN's published setup describes the NAS URL as `https://ug.link/<ID>` and the container shortcut workflow as clicking its desktop tile while signed in. The docs do not describe an API that reveals a Docker shortcut's generated hostname before the tile is opened, so this flow uses the documented tile click instead of guessing that address: [NAS remote access](https://support.ugnas.com/detail/article/en-US/86), [Docker desktop shortcuts](https://support.ugnas.com/detail/article/en-US/715).

## Release verification

The .NET tests cover settings migration/serialization, bounded learned identities, root-only helper configuration, NAS URL validation, trusted-tile discovery and strict popup targets. The executable JavaScript is tested in jsdom using UGOS-shaped fixtures: Keep me logged in without credential reads, delayed login beyond the old four-second timeout, SPA rendering, actual tile click targets, one activation, learned/renamed IDs, duplicates/hidden tiles, exact links, and rejected origins/paths. These tests do not prove a live signed-in NAS launch or long-lived session. Verify app exit/restart, Windows restart, actual tile activation and session renewal on the real NAS before stable release.

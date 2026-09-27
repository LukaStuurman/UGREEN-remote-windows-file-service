# Easier UGREENlink connection

## First-time setup without copying the Docker app address

The client accepts either:

- the NAS's UGREENlink web address, normally `https://ug.link/<UGREENlink-ID>`; or
- the Docker desktop shortcut's HTTPS address ending in `.ugapp.link` or `.ugdocker.link`.

With the NAS address, the user clicks **Verbinden en schijf openen**, signs in in the client's embedded browser, then clicks the **Remote Drive** tile on the UGOS desktop. The client recognizes only a user-initiated tile popup from the trusted UGREENlink desktop and only when the destination is the exact allowed Remote Drive shortcut root or its one-time auth-bootstrap route. It then stores the HTTPS origin and continues in the same window.

The NAS address and discovered shortcut origin are saved in `%LOCALAPPDATA%\UGREEN Remote Drive\config.json`. The app address field is repopulated from those settings on the next start. The user can enable Windows sign-in startup and automatic mounting for a one-step reconnect when the saved UGREENlink session and shortcut are available.

## Best-effort automatic tile opening on reconnect

When a shortcut origin is already saved and the embedded browser reaches the trusted UGREENlink `/desktop/` page, the app makes one bounded attempt to find a desktop anchor whose HTTPS URL exactly matches that configured shortcut root or its approved auth-bootstrap route. It clicks only that matching link, and grants a single short-lived popup permit for that same origin. If the matching tile link is absent or does not open, the app asks the user to click Remote Drive once. It does not retry continuously. First-time discovery still requires one manual tile click because the generated shortcut hostname is not known yet.

This is a compatibility fallback, not a UGREEN-supported auto-activation API. It depends on the desktop exposing the tile as a link in the current page; a UGOS update or a fresh sign-in flow may still require a manual click. The app does not inspect tile labels, enumerate unrelated links, read Edge state, or copy the one-time ticket into settings or logs.

## Ticket and sign-in handling

- The app does not read Edge history, passwords, or cookies. Sign-in uses the app's dedicated WebView2 profile.
- The one-time shortcut query is held only long enough to finish UGREENlink's bootstrap. It is not copied to settings, logs, or release notes. Bootstrap cleanup removes browsing history and disk cache while preserving sign-in cookies.
- The file API remains bound to the configured shortcut origin; a shortcut URL learned during discovery is validated again before navigation.
- If UGREENlink's own session/tunnel requires the user to activate the NAS tile again, the app first tries the exact saved tile link on the embedded NAS desktop. If that is unavailable or fails, click the tile there; the app routes the approved tile result into its own window.

UGREEN's published setup describes the NAS URL as `https://ug.link/<ID>` and the container shortcut workflow as clicking its desktop tile while signed in. The docs do not describe an API that reveals a Docker shortcut's generated hostname before the tile is opened, so this flow uses the documented tile click instead of guessing that address: [NAS remote access](https://support.ugnas.com/detail/article/en-US/86), [Docker desktop shortcuts](https://support.ugnas.com/detail/article/en-US/715).

## Release verification

The automated tests cover NAS URL validation, trusted-tile origin discovery, strict popup target validation, and preventing ticket queries from becoming saved settings. They cannot confirm that the UGREENlink desktop exposes an auto-clickable tile link or that its redirect/tunnel works on every UGOS firmware. Verify first-time discovery, reconnect auto-open, the manual fallback, and session renewal on the actual NAS before promoting a preview to stable.

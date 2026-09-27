# Easier UGREENlink connection

## First-time setup without copying the Docker app address

The client accepts either:

- the NAS's UGREENlink web address, normally `https://ug.link/<UGREENlink-ID>`; or
- the Docker desktop shortcut's HTTPS address ending in `.ugapp.link` or `.ugdocker.link`.

With the NAS address, the user clicks **Verbinden en schijf openen**, signs in in the client's embedded browser, then clicks the **Remote Drive** tile on the UGOS desktop. The client recognizes only a user-initiated tile popup from the trusted UGREENlink desktop and only when the destination is the exact allowed Remote Drive shortcut root or its one-time auth-bootstrap route. It then stores the HTTPS origin and continues in the same window.

The NAS address and discovered shortcut origin are saved in `%LOCALAPPDATA%\UGREEN Remote Drive\config.json`. The app address field is repopulated from those settings on the next start. The user can enable Windows sign-in startup and automatic mounting for a one-step reconnect when the saved UGREENlink session and shortcut are available.

## Ticket and sign-in handling

- The app does not read Edge history, passwords, or cookies. Sign-in uses the app's dedicated WebView2 profile.
- The one-time shortcut query is held only long enough to finish UGREENlink's bootstrap. It is not copied to settings, logs, or release notes. Bootstrap cleanup removes browsing history and disk cache while preserving sign-in cookies.
- The file API remains bound to the configured shortcut origin; a shortcut URL learned during discovery is validated again before navigation.
- If UGREENlink's own session/tunnel requires the user to activate the NAS tile again, do that from the embedded NAS desktop. The app routes the approved tile result into its own window. It does not scrape or automate the private UGOS interface.

UGREEN's published setup describes the NAS URL as `https://ug.link/<ID>` and the container shortcut workflow as clicking its desktop tile while signed in. The docs do not describe an API that reveals a Docker shortcut's generated hostname before the tile is opened, so this flow uses the documented tile click instead of guessing that address: [NAS remote access](https://support.ugnas.com/detail/article/en-US/86), [Docker desktop shortcuts](https://support.ugnas.com/detail/article/en-US/715).

## Release verification

The automated tests cover NAS URL validation, trusted-tile origin discovery, strict popup target validation, and preventing ticket queries from becoming saved settings. They cannot confirm the UGREENlink redirect and tile behavior on every UGOS firmware. Verify first-time discovery, a subsequent app restart, and any session-renewal behavior on the actual NAS before promoting a preview to stable.

# UGREEN Remote Windows File Service

Latest preview: [`v0.4.0-preview.1`](https://github.com/LukaStuurman/UGREEN-remote-windows-file-service/releases/tag/v0.4.0-preview.1). Adds an optional direct WireGuard Docker project and SMB-only Windows connections alongside the existing UGREENlink route. Download **`UGREENRemoteDrive-Setup-win-x64.exe`**, with Dokany included. **`UGREENDirectWireGuard-docker.zip`** contains the separate Docker project and instructions; it does not install or configure a router/VPN by itself. Real NAS/router integration remains unverified.

Mount only the UGREEN NAS shared folder named `Techbase` as a fixed Windows drive letter. At home, the client prefers the `Techbase` SMB share. Away from home, it uses a small API container reached through a UGREENlink Docker desktop shortcut (`*.ugapp.link` or `*.ugdocker.link`).

The Windows client opens the shortcut in its own WebView2 window. Sign in with the normal UGREENlink login; **there is no second access code to enter**. UGREEN states that remote access to Docker container shortcuts is available only to users signed in via UGREENlink ([official guide](https://support.ugnas.com/detail/article/en-US/715)). The client uses that authenticated browser session with `credentials: include`; it never reads or exports cookies, password fields, or the browser password database. The browser can save your password locally if you accept its Save Password prompt; no password is placed in app settings or logs.

## Components

- `server/`: a small API container limited to the one host path mounted at `/data`.
- `client/`: Windows tray/configuration app and Dokany filesystem mount.
- `docker-compose.yaml`: local/development Compose file.
- `docker-compose.ugreen.yaml`: UGREEN NAS Docker Project Compose file.
- `docker-compose.wireguard.yaml` and `direct-wireguard/`: optional direct WireGuard endpoint with an SMB-only proxy to the existing NAS; no NAS folder mounts or cloud relay. See the [Dutch WireGuard installation and router guide](docs/direct-wireguard.md).

## Optional direct connection

For an own direct tunnel, use the separate WireGuard Docker project and the
official WireGuard Windows client. This needs a reachable public IPv4/DDNS,
router UDP port forwarding and NAS TUN/NET_ADMIN support. It is not another
UGREENlink shortcut. The private client profile routes only `10.203.0.1/32`;
container firewall rules block general LAN/internet forwarding and proxy only
to the configured NAS TCP 445. NAS SMB account permissions must restrict the
account to Techbase; the proxy itself cannot filter share names. The Windows
app additionally rejects any other share root. No public SMB port is opened.

Activate the tunnel and authenticate once in Windows to `\\10.203.0.1\Techbase`.
In the Remote Drive app set that SMB share under Optional; the UGREENlink field
can be blank for SMB-only use. A reachable SMB share is mounted immediately,
without waiting for browser sign-in. See [complete setup, export and external
handshake checks](docs/direct-wireguard.md). The existing relay container stays
available separately; an active mount keeps its backend until unmounted.

## NAS setup

1. In the UGREEN Docker app, create a project named `ugreen-remote-drive` and paste `docker-compose.ugreen.yaml` into the Compose editor.
2. Set `DATA_PATH` to the exact NAS host path of the existing shared folder named `Techbase`. Verify the path in UGOS first. The container refuses a path that is not absolute or whose final directory name is not `Techbase`; Compose also refuses to create a missing source directory. The name check cannot prove that a same-named directory is the UGOS share, so verify its UGOS **Location** field. Set `PUID` and `PGID` to a NAS account with read/write permission for `Techbase`. Never mount `/volume1`, a parent folder, or another share.
3. Set `UGREEN_DRIVE_REF` to the exact GitHub release tag you intend to run. Use `main` only for an explicitly identified development test.
4. Deploy the Docker project. The service binds to NAS loopback port `18765`; it does not publish the API port on the LAN interface.
5. In Docker > Container, open the `ugreen-remote-drive` menu and create a desktop shortcut for port `18765`. Sign in to UGOS via UGREENlink and open the shortcut once. Copy its HTTPS address (`ugapp.link` or `ugdocker.link`) into the Windows client.

The UGREENlink sign-in is the access boundary for the remote shortcut. Any UGREENlink user who is allowed to open this shortcut can use the service's full read/write access to `Techbase`; the ordinary Compose container does not receive separate per-user UGREEN account identities. Grant shortcut access only to accounts trusted with the whole `Techbase` share. The service also binds its host port to `127.0.0.1`, and its file API requires a non-simple client header to prevent ordinary cross-site browser forms from issuing file changes.

## Windows setup

1. Download and run **`UGREENRemoteDrive-Setup-win-x64.exe`** from the GitHub release. The installer includes the self-contained app and the unmodified official Dokany 2.3.1.1000 installer, installs Dokany automatically if missing, and creates a Start-menu shortcut. Approve the Windows administrator prompt. No separate Dokany download is needed. If requested, restart Windows before opening the app. A compatible existing Dokany 2.3+ driver is reused; an older/incomplete Dokany 2.x installation must be removed through Windows Settings > Apps and Windows restarted before retrying, because upstream does not support an in-place driver upgrade. This installer supports x64 Windows 10/11, not ARM64.
2. Start **UGREEN Remote Drive** from the Start menu. For automatic shortcut discovery, enter the NAS web address `https://ug.link/<your-UGREENlink-ID>` (from Control Panel > Device Connection > Remote access). You can also paste the Docker shortcut HTTPS address (`ugapp.link` or `ugdocker.link`) if you already have it.
3. Click **Verbinden en schijf openen**. For a NAS address, sign in inside the app and click the **Remote Drive** desktop tile once. The app captures the tile's approved shortcut origin and saves it locally; it never stores the one-time ticket/query. **No access code or token is needed.**
4. On later starts the app reuses its fixed browser profile, automatically connects, and opens the saved Remote Drive tile when necessary. **Aanmelding onthouden** and **Automatisch verbinden en Remote Drive-tegel openen** default to enabled under **Optioneel**. The app enables UGOS's own Keep me logged in checkbox when the login form appears. Sign in once after upgrading so the NAS can issue that persistent session; accept the browser's Save Password prompt if desired. UGREEN can still expire/revoke a session or require MFA; the client does not bypass that. Select **Start en koppel automatisch aan bij Windows-aanmelding** to start the app at Windows sign-in too.
5. LAN/SMB path and an alternate drive letter are optional under **Optioneel: LAN/SMB en andere schijfletter**. If configured and reachable, the `Techbase` SMB share is preferred at home; otherwise the app uses the remote API.

UGREEN documents remote Docker access as opening a container's desktop shortcut while signed in with UGREENlink ([NAS remote access](https://support.ugnas.com/detail/article/en-US/86), [Docker desktop shortcuts](https://support.ugnas.com/detail/article/en-US/715)). First-time discovery needs one manual tile click because its generated address is not yet known. On reconnect the client watches through slow sign-in and SPA rendering, then clicks the actual UGOS `.click-region` of the uniquely matching tile, or an exact configured shortcut link. The default tile name is **ugreen-remote-drive**; set **Naam van de NAS-tegel** if renamed. An approved tile click also learns its stable module ID. Duplicate names are not guessed. Every popup must still match the exact configured shortcut origin and permitted root/bootstrap path; no private NAS activation API is used. Real-NAS behavior remains to be checked for the current UGOS firmware.

The SMB path uses the Windows user's existing SMB authentication. The app does not store SMB credentials. The SMB field accepts only the share root `\\NASNAME\Techbase`; it rejects other shares and subfolders.

The installer preserves existing settings and the app's WebView2 sign-in profile. Close an older copy started from Downloads/the tray and use the installed Start-menu shortcut. If automatic startup was enabled for a portable copy, toggle it off and on in the installed app to update its startup path. Uninstalling the app preserves settings and the shared Dokany installation because other apps may need it. Microsoft Edge WebView2 Runtime must still be available on Windows. The separate `UGREENRemoteDrive.exe` and ZIP remain portable downloads for systems where Dokany is already installed; they are **not** installers.

**Wis lokale aanmelding** removes this app browser's cookies, site storage, and saved login/autofill data. Disabling **Aanmelding onthouden** stops enabling UGOS's Keep me logged in option and stops offering to save new passwords; use **Wis lokale aanmelding** to remove already saved data too. No Edge profile is imported or changed.

## Updating an existing installation

`v0.3.7-preview.1` adds raw shared-memory binary transfers, bounded asynchronous
prefetch of the next sequential read window, and shared in-flight metadata
requests. These three improvements require only the new Windows installer and a
current Microsoft Edge WebView2 Runtime. They work with the existing NAS API;
the server transport update from `v0.3.6-preview.1` remains recommended separately.

`v0.3.6-preview.1` reduces remote read requests when opening/copying files and
avoids repeated connection checks. Install the new Windows setup; for persistent
server connections also rebuild/recreate the NAS container with
`UGREEN_DRIVE_REF=v0.3.6-preview.1`. A restart of an old image is insufficient.
See [performance, measured request counts and cache behavior](docs/performance.md).

Deploy the matching release of `docker-compose.ugreen.yaml` and set `UGREEN_DRIVE_REF` to that release tag, then install the matching Windows release. The new Compose file no longer passes `UGREEN_DRIVE_TOKEN`; remove the old variable from the project's environment settings if the UGREEN UI retains unused entries. Keep `DATA_PATH`, `PUID`, `PGID`, the Techbase-only bind mount, and the existing UGREENlink shortcut port unchanged. The new app ignores a legacy encrypted token in its local configuration and removes that obsolete field the next time settings are saved.

## GitHub checks and releases

The GitHub Actions workflow in `.github/workflows/ci-release.yml` runs Python API tests, builds and smoke-tests the Docker image against an isolated temporary share, and builds the Windows client.

The hosted smoke test verifies container behavior but does not prove access control on a real UGREENlink shortcut, the actual NAS share, or Dokany mounting. Keep releases marked as previews until those integration checks have passed. See `RELEASING.md` for the checklist.

## Supported file operations

Directory listing and metadata, read, create, write by byte range or append, truncate, make directory, rename, delete, modification time, and free-space queries. Symbolic links are refused. Windows file locking, alternate data streams, security-descriptor editing, and offline write caching are not provided; do not use this mount for databases or applications that require reliable byte-range locks.

## Security and limits

- Keep the Docker volume limited to the `Techbase` share. The mount is read/write so Explorer can create, rename, and delete files.
- Only give UGREENlink shortcut access to NAS accounts allowed to read and modify all of `Techbase`.
- The container runs as a non-root UID/GID with a read-only image filesystem, no added Linux capabilities, and `no-new-privileges`.
- The Docker host port is bound to `127.0.0.1`; use the UGREENlink shortcut for remote access and SMB for LAN access.
- The file API accepts only the exact configured HTTPS `.ugapp.link` or `.ugdocker.link` origin. Initial NAS discovery is restricted to HTTPS UGREENlink pages and the exact Remote Drive tile target; reconnect auto-open has a single-use permit for that same saved origin. The app saves only the shortcut origin and discards its one-time query.
- Requests are chunked to a maximum of 4 MiB. Network interruptions can fail an in-progress write; reconnect and retry from Explorer if needed.

## Build and local checks

The NAS service uses Python's standard library:

```powershell
Push-Location server
python -m unittest discover -s tests -v
Pop-Location
```

Build the Windows client with the .NET 8 SDK:

```powershell
dotnet restore client/UGREENRemoteDrive.sln
dotnet build client/UGREENRemoteDrive.sln -c Release
dotnet test client/UGREENRemoteDrive.sln -c Release --no-build
```

The Dokany driver is not installed by the build. Install it from the official Dokany release when you are ready to mount a drive.

To build the bundled installer after publishing the client, use PowerShell with Inno Setup 6 installed:

```powershell
./installer/Build-Installer.ps1 -PublishDirectory ./client/UGREENRemoteDrive/bin/Release/net8.0-windows/win-x64/publish -OutputDirectory ./installer/output
```

The build downloads a fixed official Dokany release and verifies both SHA-256 and Authenticode before embedding it. Building does not install the driver on the developer's machine. `installer/Test-Installer.ps1` is restricted to disposable GitHub-hosted runners; CI checks first installation, native DLL/driver loading, repeated installation without driver replacement, and uninstall with shared dependencies/settings preserved.

## Logging and configuration

Client settings and the dedicated WebView2 profile live under `%LOCALAPPDATA%\UGREEN Remote Drive`. Logs are written under `%LOCALAPPDATA%\UGREEN Remote Drive\logs`; the client does not log file names, request bodies, or cookies. A legacy encrypted access token from an older version is ignored and discarded when settings are next saved.

The server logs client IP, method, API route, status, and byte count. It does not log query strings containing file paths, request bodies, or cookies.

For read-only diagnosis of blank WebView/login redirects, Dokany root errors, backend selection, and verification of the Techbase mount root, see [Drive diagnostics and root verification](docs/drive-diagnostics.md).

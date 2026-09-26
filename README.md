# UGREEN Remote Windows File Service

Mount only the UGREEN NAS shared folder named `Techbase` as a fixed Windows drive letter. At home, the client prefers the `Techbase` SMB share. Away from home, it uses a small API container reached through a UGREENlink Docker desktop shortcut (`*.ugapp.link` or `*.ugdocker.link`).

The Windows client opens the shortcut in its own WebView2 window. Sign in with the normal UGREENlink login; **there is no second access code to enter**. UGREEN states that remote access to Docker container shortcuts is available only to users signed in via UGREENlink ([official guide](https://support.ugnas.com/detail/article/en-US/715)). The client uses that authenticated browser session with `credentials: include`; it never reads or exports cookies and does not store the NAS password.

## Components

- `server/`: a small API container limited to the one host path mounted at `/data`.
- `client/`: Windows tray/configuration app and Dokany filesystem mount.
- `docker-compose.yaml`: local/development Compose file.
- `docker-compose.ugreen.yaml`: UGREEN NAS Docker Project Compose file.

## NAS setup

1. In the UGREEN Docker app, create a project named `ugreen-remote-drive` and paste `docker-compose.ugreen.yaml` into the Compose editor.
2. Set `DATA_PATH` to the exact NAS host path of the existing shared folder named `Techbase`. Verify the path in UGOS first. The container refuses a path that is not absolute or whose final directory name is not `Techbase`; Compose also refuses to create a missing source directory. The name check cannot prove that a same-named directory is the UGOS share, so verify its UGOS **Location** field. Set `PUID` and `PGID` to a NAS account with read/write permission for `Techbase`. Never mount `/volume1`, a parent folder, or another share.
3. Set `UGREEN_DRIVE_REF` to the exact GitHub release tag you intend to run. Use `main` only for an explicitly identified development test.
4. Deploy the Docker project. The service binds to NAS loopback port `18765`; it does not publish the API port on the LAN interface.
5. In Docker > Container, open the `ugreen-remote-drive` menu and create a desktop shortcut for port `18765`. Sign in to UGOS via UGREENlink and open the shortcut once. Copy its HTTPS address (`ugapp.link` or `ugdocker.link`) into the Windows client.

The UGREENlink sign-in is the access boundary for the remote shortcut. Any UGREENlink user who is allowed to open this shortcut can use the service's full read/write access to `Techbase`; the ordinary Compose container does not receive separate per-user UGREEN account identities. Grant shortcut access only to accounts trusted with the whole `Techbase` share. The service also binds its host port to `127.0.0.1`, and its file API requires a non-simple client header to prevent ordinary cross-site browser forms from issuing file changes.

## Windows setup

1. Install the official Dokany 2.3 runtime once; its Windows filesystem driver is required for a drive letter.
2. Start `UGREENRemoteDrive.exe` and paste the NAS Docker shortcut's HTTPS address (`ugapp.link` or `ugdocker.link`). **No access code or token is needed.**
3. Click **Verbinden en schijf openen**. If asked, sign in to UGREENlink in the embedded browser; the app then connects the `U:` drive automatically. It uses its own WebView2 profile and does not save your NAS password.
4. LAN/SMB path and an alternate drive letter are optional under **Optioneel: LAN/SMB en andere schijfletter**. If configured and reachable, the `Techbase` SMB share is preferred at home; otherwise the app uses the remote API.
5. To reconnect automatically after Windows sign-in, select **Start en koppel automatisch aan bij Windows-aanmelding**. Keep the app running in the system tray while using the drive.

The SMB path uses the Windows user's existing SMB authentication. The app does not store SMB credentials. The SMB field accepts only the share root `\\NASNAME\Techbase`; it rejects other shares and subfolders.

## Updating an existing installation

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
- The client accepts only HTTPS origins ending in `.ugapp.link` or `.ugdocker.link` and only sends API requests to the exact configured origin.
- Requests are chunked to a maximum of 4 MiB. Network interruptions can fail an in-progress write; reconnect and retry from Explorer if needed.

## Build and local checks

The NAS service uses Python's standard library:

```powershell
python -m unittest discover -s server/tests -v
```

Build the Windows client with the .NET 8 SDK:

```powershell
dotnet restore client/UGREENRemoteDrive.sln
dotnet build client/UGREENRemoteDrive.sln -c Release
dotnet test client/UGREENRemoteDrive.sln -c Release --no-build
```

The Dokany driver is not installed by the build. Install it from the official Dokany release when you are ready to mount a drive.

## Logging and configuration

Client settings and the dedicated WebView2 profile live under `%LOCALAPPDATA%\UGREEN Remote Drive`. Logs are written under `%LOCALAPPDATA%\UGREEN Remote Drive\logs`; the client does not log file names, request bodies, or cookies. A legacy encrypted access token from an older version is ignored and discarded when settings are next saved.

The server logs client IP, method, API route, status, and byte count. It does not log query strings containing file paths, request bodies, or cookies.

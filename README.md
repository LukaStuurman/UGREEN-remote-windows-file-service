# UGREEN Remote Windows File Service

Mount only the UGREEN NAS shared folder named `Techbase` as a fixed Windows drive letter. On the home network, the client prefers the `Techbase` SMB share. Away from home, it uses a small API service in a Docker container reached through a UGREENlink desktop shortcut (`*.ugapp.link`).

The Windows client opens UGREENlink in its own WebView2 window so the user can sign in with the normal UGREENlink login. It does not copy browser cookies or store the NAS account password. File API requests include a separate bearer token configured by the NAS administrator; Windows protects that token with DPAPI for the current Windows user.

## Components

- `server/`: token-authenticated API container. It can access only the one host path mounted at `/data`.
- `client/`: Windows tray/configuration app and Dokany filesystem mount. It uses WebView2 for the authenticated UGREENlink session and Dokany for a drive letter in Explorer.
- `docker-compose.yaml`: local/development Compose file.
- `docker-compose.ugreen.yaml`: NAS Docker Project Compose file; it builds the image from this public GitHub repository.

## NAS setup

1. In the UGREEN Docker app, create a project named `ugreen-remote-drive` and paste `docker-compose.ugreen.yaml` into the Compose editor.
2. Set `DATA_PATH` to the exact NAS host path of the existing shared folder named `Techbase`. Verify the path in UGOS first; do not assume it is `/volume1/Techbase`. Set `PUID` and `PGID` to a NAS account that has read/write permission for `Techbase`. Never mount `/volume1`, a parent folder, or another share.
3. Generate a random token with at least 32 characters and set `UGREEN_DRIVE_TOKEN` to it. In PowerShell, this prints a cryptographically random token:

   ```powershell
   $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
   $bytes = New-Object byte[] 32
   $rng.GetBytes($bytes)
   [Convert]::ToBase64String($bytes)
   $rng.Dispose()
   ```

   The token is stored in the Docker project configuration; do not commit it or post it publicly.
4. Deploy the Docker project. The service binds to NAS loopback port `18765`; it does not publish the API port on the LAN interface.
5. In Docker > Container, open the `ugreen-remote-drive` menu and create a desktop shortcut for port `18765`. Sign in to UGOS via UGREENlink and open the shortcut once. Copy the resulting `https://...ugapp.link/` address into the Windows client.

UGREENlink protects remote browser access to the shortcut. The API also requires the bearer token, including for a direct request to the container port. The client uses the authenticated browser context for remote API requests and never reads or exports UGREENlink cookies.

## Windows setup

1. Install the official Dokany 2.3 runtime once; its Windows filesystem driver is required for a drive letter.
2. Start `UGREENRemoteDrive.exe` and enter the container's `ugapp.link` shortcut URL, the token from the NAS Docker project, the SMB path for `Techbase` (for example `\\NASNAME\Techbase`), and a drive letter such as `U:`.
3. Select **Sign in / Connect** and complete the UGREENlink sign-in in the embedded WebView. The client keeps a separate WebView2 profile for that Windows user.
4. Select **Mount**. When the SMB path is reachable, it is the active backend; otherwise the client uses the remote API through UGREENlink. The drive letter stays mounted while the client runs and can be started automatically when Windows signs in.

The SMB path uses the Windows user's existing SMB authentication. The app does not store SMB credentials.

## Supported file operations

Directory listing and metadata, read, create, write by byte range, truncate, make directory, rename, delete, modification time, and free-space queries. Symbolic links are deliberately refused by the container. Windows file locking, alternate data streams, security-descriptor editing, and offline write caching are not provided; do not use this mount for databases or applications that require reliable byte-range locks.

## Security and limits

- Use a random bearer token of at least 32 characters. Rotate it by changing the Docker project value and the client configuration.
- Keep the Docker volume limited to the `Techbase` share. The mount is read/write because Explorer needs to create, rename, and delete files.
- The container runs as a non-root UID/GID, with a read-only image filesystem, no added Linux capabilities, and no-new-privileges.
- The Docker host port is bound to `127.0.0.1`; use the UGREENlink shortcut for remote access and SMB for LAN access.
- The client uses WebView2's own sign-in storage and DPAPI for its bearer token. The token is only sent to the configured `ugapp.link` origin.
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
```

The Dokany driver is intentionally not installed by the build. Install it from the official Dokany release when you are ready to mount a drive.

## Logging and configuration

Client configuration and its dedicated WebView profile live in `%LOCALAPPDATA%\UGREEN Remote Drive`. The token is DPAPI-encrypted for the current Windows user. Logs are written under `%LOCALAPPDATA%\UGREEN Remote Drive\logs`; the client does not log file names, request bodies, tokens, or cookies.

The server logs method, API route, status and byte count only. It does not log query strings, file paths, or authorization headers.

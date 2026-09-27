# Release process

GitHub Actions validates and packages source tags beginning with `v`. A release is published only after the server tests, isolated container smoke test, and Windows build pass.

## Required checks before stable release

Do not publish a stable release until the actual NAS path and UGREENlink route have been verified with the `Techbase` share only. Record results for:

- Windows LAN connection to the `Techbase` SMB share.
- Remote access through the UGREENlink desktop shortcut when outside the LAN.
- First-time shortcut discovery from `https://ug.link/<ID>` and a subsequent app restart without manually entering the generated Docker shortcut address.
- On reconnect, verify the one-shot automatic Remote Drive tile attempt on the saved shortcut origin, plus the manual-click fallback when automatic discovery cannot find/open the tile.
- If UGREENlink requires a fresh shortcut activation, verify the approved tile result returns to the mount flow without exposing its one-time query in settings or logs.
- Shortcut access denied to a browser session that is not signed in, and available after signing in.
- The file API rejects requests without its required client header and works through the signed-in UGREENlink shortcut.
- Listing, read, create, write, resize, rename, delete, and modification-time behavior.
- SMB preference on LAN and backend switch/reconnect behavior.
- Confirmation that no other NAS share or parent directory is mounted.
- Compose has exactly one data bind mount, targets `/data`, refuses to create a missing host path, and both server and Windows client reject non-Techbase share paths.
- Windows client build and Dokany/WebView2 runtime requirements.

Any UGREENlink account with permission to open the Docker shortcut gets the same read/write access to all of `Techbase`; verify that shortcut permissions are limited to trusted accounts. The GitHub container smoke test uses a disposable directory and does not prove UGREENlink access control or NAS behavior.

If NAS integration is incomplete, publish only a clearly named prerelease such as `v0.3.0-preview.1`. A hyphenated tag is published as a GitHub prerelease. Do not claim NAS validation or promote it to stable before the checklist passes.

## Versioning and publishing

1. Review the changes; update `VERSION` in `server/app.py` when changing the server API.
2. Run Python tests and the Windows client build. Review GitHub Actions and any NAS test record.
3. Choose a new semantic version. Do not reuse or move a published tag.
4. Create and push an annotated tag from the exact tested commit, for example:

   ```powershell
   git tag -a v0.3.0-preview.1 -m "UGREEN Remote Drive v0.3.0-preview.1"
   git push origin v0.3.0-preview.1
   ```

5. GitHub Actions repeats the API tests, Docker build/header smoke tests, and Windows build. On success, it publishes a self-contained `UGREENRemoteDrive-win-x64.zip` and SHA-256 checksum. GitHub also provides source archives.
6. For NAS deployment, set `UGREEN_DRIVE_REF` to the exact release tag and update the Compose project without `UGREEN_DRIVE_TOKEN`. Use `main` only for a clearly identified development build.
7. Verify the release page and downloaded checksum. Update README and this file when the procedure changes.

The Windows ZIP includes the .NET runtime, but Windows still needs the Microsoft Edge WebView2 Runtime and official Dokany filesystem driver. Never place NAS IPs, UGREENlink URLs, share host paths, passwords, or cookies in release notes or assets.

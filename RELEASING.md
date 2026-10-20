# Release process

GitHub Actions validates and packages source tags beginning with `v`. A release is published only after the server tests, isolated container smoke test, and Windows build pass.

## Required checks before stable release

For the normal app's opt-in Windows-SMB mode, build `native-smb/Build-WindowsSmbComponents.ps1` before publishing. Verify direct LAN mapping, same-share reuse, other occupied-letter refusal, non-forced disconnect with open files, private Wintun UAC startup under the same Windows user, separate enrollment/approval, stopped-node resume, NAS TCP 445 reachability and an actual AutoCAD save on a copy. Synthetic mapping tests and CI adapter startup are not live NAS validation. Distribute the single `UGREENRemoteDrive.exe` for this mode; the existing installer still installs Dokany for the original virtual filesystem.

Do not publish a stable release until the actual NAS path and UGREENlink route have been verified with the `Techbase` share only. Record results for:

- Windows LAN connection to the `Techbase` SMB share.
- Remote access through the UGREENlink desktop shortcut when outside the LAN.
- First-time shortcut discovery from `https://ug.link/<ID>` and a subsequent app restart without manually entering the generated Docker shortcut address.
- On reconnect, verify the one-shot automatic Remote Drive tile attempt on the saved shortcut origin, plus the manual-click fallback when automatic discovery cannot find/open the tile.
- With **Aanmelding onthouden** enabled, sign in once with UGOS Keep me logged in selected, fully exit/restart the app and restart Windows. Check actual NAS session reuse; server-enforced expiration/MFA must remain effective. Verify saved browser credentials can be cleared.
- Check the non-anchor desktop tile, delayed SPA rendering after sign-in, renamed learned module ID, and duplicate-name refusal. Verify the actual configured label under **Naam van de NAS-tegel**.
- If UGREENlink requires a fresh shortcut activation, verify the approved tile result returns to the mount flow without exposing its one-time query in settings or logs.
- Shortcut access denied to a browser session that is not signed in, and available after signing in.
- The file API rejects requests without its required client header and works through the signed-in UGREENlink shortcut.
- Listing, read, create, write, resize, rename, delete, and modification-time behavior.
- SMB preference on LAN and backend switch/reconnect behavior.
- Embedded Tailscale without the system client/service: one-time auth-key enrollment, own protected identity after restart, exact configured NAS peer, NAS popup credentials, direct/relay reporting, logout, and reconnect without replaying writes. Verify the NAS TCP 445 service is actually exposed to its Tailscale peer. Test UGREENlink sign-in in the separate popup and profile reuse. CI fixtures do not prove real tailnet/NAS behavior. Verify random-access CAD saves, flush, allocation/EOF, temporary-file backup and atomic replacement with retained handles. Synthetic Samba/native fixtures do not prove actual AutoCAD/NAS saves or cross-client byte-range locking.
- Native NAS sign-in with correct/incorrect local UGOS credentials on LAN and an active WireGuard tunnel, account-conflict handling without disconnecting other SMB sessions, optional remember across app/Windows restart, per-address forgetting, and browser fallback for UGREENlink. The CI Credential Manager fixture proves storage interop only, not a real NAS login or WNet server authentication.
- Confirmation that no other NAS share or parent directory is mounted.
- Compose has exactly one data bind mount, targets `/data`, refuses to create a missing host path, and both server and Windows client reject non-Techbase share paths.
- Windows client build, bundled Dokany fresh install/reinstall/uninstall, and WebView2 runtime requirements.

Any UGREENlink account with permission to open the Docker shortcut gets the same read/write access to all of `Techbase`; verify that shortcut permissions are limited to trusted accounts. The GitHub container smoke test uses a disposable directory and does not prove UGREENlink access control or NAS behavior.

If NAS integration is incomplete, publish only a clearly named prerelease such as `v0.3.3-preview.2`. A hyphenated tag is published as a GitHub prerelease. Do not claim NAS validation or promote it to stable before the checklist passes. The current reconnect auto-open implementation is best effort and still needs verification on the actual NAS.

## Versioning and publishing

1. Review the changes; update `VERSION` in `server/app.py` when changing the server API.
2. Run Python tests and the Windows client build. Review GitHub Actions and any NAS test record.
3. Choose a new semantic version. Do not reuse or move a published tag.
4. Create and push an annotated tag from the exact tested commit, for example:

   ```powershell
   git tag -a v0.3.3-preview.2 -m "UGREEN Remote Drive v0.3.3-preview.2"
   git push origin v0.3.3-preview.2
   ```

5. GitHub Actions repeats the API tests, Docker build/header smoke tests, Windows build, and installer smoke test on a disposable Windows runner. On success, it publishes **`UGREENRemoteDrive-Setup-win-x64.exe`** with the pinned official Dokany 2.3.1.1000 bundle embedded, plus a SHA-256 checksum. The standalone EXE/ZIP remain available as portable alternatives requiring an existing Dokany installation. GitHub also provides source archives.
6. For NAS deployment, set `UGREEN_DRIVE_REF` to the exact release tag and update the Compose project without `UGREEN_DRIVE_TOKEN`. Use `main` only for a clearly identified development build.
7. Verify the release page and downloaded checksum. Update README and this file when the procedure changes.

The installer includes the .NET runtime and installs the official Dokany driver if missing. Its build verifies the upstream bundle's pinned SHA-256 and Authenticode signature; do not change the version/hash without reviewing the official upstream release. Existing compatible Dokany 2.3+ is reused. Older or incomplete Dokany 2.x is reported without automatically removing a shared driver; upstream requires uninstall/reboot first. The setup reports required restarts, suppresses app launch until then, and preserves Dokany and user settings on uninstall. Microsoft Edge WebView2 Runtime is still required. The portable ZIP/EXE do not install Dokany. Never place NAS IPs, UGREENlink URLs, share host paths, passwords, or cookies in release notes or assets.

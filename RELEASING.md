# Release process

This repository's GitHub Actions workflow validates and packages source tags that start with `v`. A GitHub Release is created only after the tagged revision passes the server tests, isolated container smoke test, and Windows build.

The existing GitHub prerelease `v0.1.0-preview.1` points to commit `69c8eba`, before the Techbase-only path guards and without real NAS validation. Treat it as an early preview only; it is not evidence that remote UGREENlink access or mounting works. The next usable release must include the Techbase-only guards and pass the actual NAS and Windows integration checks below.

## Required checks before tagging

Do not publish a stable release until the actual NAS path and UGREENlink route have been verified with the user-approved `Techbase` share only. Record results for:

- Windows LAN connection to the SMB share named `Techbase`.
- Remote access through the UGREENlink desktop shortcut when outside the LAN.
- Bearer-token acceptance and rejection.
- Listing, read, create, write, resize, rename, delete, and modification-time behavior.
- SMB preference on LAN and backend switch/reconnect behavior.
- Confirmation that no other NAS share or parent directory is mounted.
- The Compose configuration has exactly one data bind mount, its target is `/data`, it refuses to create a missing host path, and both the server and Windows client reject non-Techbase share paths.
- Windows client build and Dokany/WebView2 runtime requirements.

The GitHub container smoke test uses a disposable empty directory on a hosted runner. It proves that the Docker image builds, the health endpoint responds, and the API enforces its token; it does not prove UGREENlink or NAS behavior.

If implementation is ready for wider testing but the NAS write/integration checklist is incomplete, publish only a clearly named prerelease tag such as `v0.2.0-preview.1`. The workflow recognizes any hyphenated version tag as a GitHub prerelease. Do not call it NAS-validated or promote it to stable until every required integration check passes.

## Versioning and publishing

1. Review changes on `main`; update `VERSION` in `server/app.py` when changing the server API.
2. Run the documented Python tests and Windows client build. Review the current GitHub Actions run and NAS test record.
3. Choose a new semantic version tag; use `v0.2.0-preview.1` while integration remains incomplete, or a stable tag such as `v0.2.0` only after the required checklist passes. Do not reuse or move a published tag.
4. Create and push an annotated tag from the exact tested commit:

   ```powershell
   git tag -a v0.2.0 -m "UGREEN Remote Drive v0.2.0"
   git push origin v0.2.0
   ```

5. GitHub Actions repeats API tests, Docker build/health/auth smoke tests, and the Windows build. If they pass, it publishes a GitHub Release with a self-contained `UGREENRemoteDrive-win-x64.zip` and its SHA-256 checksum. GitHub provides source archives for the same tag.
6. For a NAS deployment, set `UGREEN_DRIVE_REF` to the exact release tag. Use `main` only for a clearly identified development build.
7. Verify the published release page and download/checksum after the workflow completes. Update README and this file when the install or release procedure changes.

The Windows ZIP includes the repository README. It includes the .NET runtime, but Windows still needs the Microsoft Edge WebView2 Runtime and the official Dokany filesystem driver. Never place NAS IPs, UGREENlink URLs, share host paths, passwords, cookies, or bearer tokens in release notes or assets.

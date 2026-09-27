# Drive diagnostics and Techbase verification

This guide records the failure pattern, fixes, and read-only checks for the Windows Dokany drive. Do not list unrelated NAS names, open file contents, or create test data while diagnosing.

## Observed failure pattern and fixes

- **Black WebView or login redirects.** The client uses a dedicated WebView2 profile; it does not share Edge's session or export Edge cookies. Sign in inside the client WebView. Only the expected UGREENlink desktop-login redirect may be upgraded from HTTP to HTTPS, once, for the matching navigation and approved login host/path. Other HTTP destinations are rejected.
- **Shortcut authentication uses a one-time query.** A trusted UGREENlink desktop may open only the configured shortcut root or the exact authentication-bootstrap target. The query-bearing bootstrap is allowed for one matching navigation; its sensitive query is omitted from diagnostics. Afterward, the WebView clears browsing history/cache while preserving sign-in cookies. Confirm/create the Docker desktop shortcut once and reuse that tile; repeated creation can leave duplicate tiles. Never copy a ticket/query into settings, logs, screenshots, or Git.
- **Capacity is visible, but Explorer says the root is invalid.** Capacity (`GetSpace`) is not proof of a valid directory root. Authentication now requires HTTP 200 plus a valid root `Stat` entry with an empty name and `isDirectory=true`. Dokany must then open that directory and successfully enumerate it. If root `Stat` fails, the client returns a path error; opening Explorer must not create the root as a side effect.
- **Root callbacks appear to use inconsistent data.** The cause was resolving `BackendSelector.Current` on every Dokany callback: an SMB availability change could switch backends between the capacity query and root `Stat`/listing. The mount now pins one backend instance for its lifetime, so capacity, root metadata, listing, and file operations use the same SMB or UGREENlink backend until unmount/remount.
- **Paths could escape the intended share.** The SMB guard rejects reparse points (including junctions/symlinks) in the share root and traversed path. The API rejects symbolic links and paths resolving outside its configured data root. NAS host paths are validated with POSIX semantics, including when the test suite runs on Windows.

Root callback diagnostics contain only backend, operation, status, entry classification, and counts/flags—never file names or contents.

## Regression coverage

The Windows client tests cover backend selection changing during a mount, consistent root metadata/listing, invalid root-stat responses, bounded login redirects and auth popups, and SMB reparse-point rejection. The server tests cover API traversal/symlink rejection and Techbase host-path validation. Run the Python and .NET commands in [Build and local checks](../README.md#build-and-local-checks); CI also validates the Compose configuration and runs an isolated container smoke test. Those local/isolated checks do not substitute for checking the active NAS mount source.

## Read-only verification checklist

1. **Check Dokany and the drive.** In Windows **Settings → Apps → Installed apps**, confirm the official Dokany runtime is installed. In UGREEN Remote Drive, confirm the selected backend and that the configured drive (normally `U:`) is mounted. Open `U:\` in Explorer. A visible capacity alone is not a successful root check.
2. **Check only metadata.** These checks do not enumerate a directory:

   ```powershell
   Get-Item -LiteralPath 'U:\' | Select-Object PSIsContainer, Attributes
   Test-Path -LiteralPath 'U:\Techbase' -PathType Container
   Get-Item -LiteralPath '\\NASNAME\Techbase' -ErrorAction SilentlyContinue |
       Select-Object PSIsContainer, Attributes
   ```

   `U:\Techbase` tells only whether a child by that name exists. Its absence does **not** prove that the mounted root is Techbase.
3. **Verify the configured source, not the drive label or capacity.** For LAN-SMB, the client path must be the share root `\\NASNAME\Techbase`, not a parent share or subfolder. For UGREENlink, inspect the active container's read-only mount metadata: the host source must be the UGOS-verified `Techbase` share location and the container target must be `/data`, with no parent volume or extra share. The API serves that data root as its root directory. Verify the host source against the `Techbase` share's UGOS **Location** property; a same-named folder is not sufficient proof. Do not commit or log the actual host path.
4. **Verify root behavior without listing names.** Root `Stat` must report a directory with an empty name; the Dokany root-open and `FindFiles` callbacks must succeed. The callback log may be checked for status and entry/directory counts only. If the active source cannot be verified, do not claim that the root is Techbase or change the mapping.
5. **Keep the check read-only.** Do not use `dir`, `Get-ChildItem`, or screenshots of the NAS listing. Do not create, rename, or delete a test item without separate approval.

The user confirmed that Techbase opens and files are visible in Explorer. No file names or contents were captured. This confirms user-verified access; the configuration checklist above remains the way to independently verify which share is mounted as the drive root.

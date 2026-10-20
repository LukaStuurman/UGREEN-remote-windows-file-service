# UGREEN Remote Drive – Native SMB preview

This is a separate edition of UGREEN Remote Drive that connects Windows directly to the existing NAS SMB share using the operating system's SMB redirector. It does not use or install Dokany and it does not require the separate Windows Tailscale app.

## Architecture

LAN: Windows SMB → \\NASNAME\Techbase.

Remote: Windows SMB → 100.64.0.0/10 → the private signed Wintun network adapter → a bundled tailscaled 1.104.1 daemon → the NAS's existing Tailscale peer on TCP 445.

The private tailscaled process is installed as an isolated SYSTEM scheduled task at startup. It uses its own state under %ProgramData%\UGREEN Remote Drive Native and its own protected local control pipe. It does not reuse or modify an installed Tailscale service or the former embedded tsnet identity. Only the Techbase share can be connected. Tailscale DNS hostnames must match an authenticated peer in the private node's peer list; no public DNS fallback is allowed. Tailscale is configured not to change Windows DNS or accept subnet routes.

## Installation

Install UGREENRemoteDrive-NativeSMB-Setup-win-x64.exe as administrator. This installer includes the app, an upstream Tailscale CLI/daemon built from the pinned v1.104.1 source dependency and a signed Wintun 0.14.1 DLL verified by SHA-256. No Dokany driver is installed. The NAS must already have Tailscale enabled and its Techbase SMB share exposed to the NAS peer.

Run the separate UGREEN Remote Drive Native SMB application. Enter a LAN path such as \\NASNAME\Techbase, or the NAS's Tailscale IPv4 / exact .ts.net hostname. First-time tailnet enrollment requires launching the app as administrator and supplying a one-time non-ephemeral Tailscale auth key. If your tailnet requires device approval, approve the private node in the Tailscale admin console. Enter local NAS SMB username/password and choose an unused drive letter. The connection is mapped with WNetAddConnection2 directly by Windows, including normal SMB file handles, locking, and writes. Only addresses and the username are saved, never passwords or keys.

## Important validation warning

This is a preview, not a verified real-NAS or AutoCAD integration. Native SMB should eliminate Dokany callback-related save problems, but do not test with an irreplaceable drawing: start with a copy and compare the file after saving. The dedicated CI is intended to confirm Windows build, signed component packaging, tests, and private daemon startup. No live tailnet enrollment or UGREEN NAS save can be established from GitHub CI.

Installing Wintun and creating the SYSTEM task require administrator access. Enterprise-managed PCs may forbid this. Other active Tailscale instances and VPNs can compete for 100.64.0.0/10 routes; test this before daily use. The private daemon persists after closing the GUI. The GUI can bring the tailnet down; uninstall stops and removes only its private scheduled task, preserving its private state for upgrades. The existing UGREENlink/Dokany edition stays installed separately.

Build on Windows x64 with .NET 8, Go matching embedded-tailscale/go.mod, and Inno Setup 6 via the Build-Native.ps1 script.

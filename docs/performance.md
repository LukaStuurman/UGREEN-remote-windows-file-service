# Drive performance

The client can use either the remote UGREENlink API or the NAS's Techbase SMB
share on the local network. SMB is normally faster for bulk file transfers when
the NAS and computer are on a trusted LAN (or connected through a suitable VPN).
The client prefers SMB only while the configured share is reachable; otherwise
it falls back to UGREENlink. Do not expose SMB/port 445 directly to the public
internet.

## Remote-drive optimizations

- Writes are sent in chunks of up to 4 MiB, matching the server's request limit.
  This reduces request count for large files without increasing a single API
  request beyond the server's configured cap.
- Repeated metadata lookups and directory listings are cached for one second to
  avoid duplicate remote round trips during Explorer navigation. Client-side
  create, write, rename, delete, resize, and timestamp changes invalidate the
  cache. Directory emptiness checks always fetch a fresh listing.
- File data is not cached locally and writes are not deferred, so there is no
  offline/write-behind queue that could make Explorer report success before the
  NAS has accepted the data.

## Checking which route is active

The client status/log identifies the mounted backend as `UGREENlink` or
`LAN-SMB`. If it says `UGREENlink` while the computer is at home, verify that
the configured SMB path is the Techbase share root and that the NAS name
resolves and port 445 is reachable from the computer. If that route cannot be
reached, remote-drive performance remains dependent on internet latency and
UGREENlink availability.

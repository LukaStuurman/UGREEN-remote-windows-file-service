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
- Since `v0.3.6-preview.1`, a following contiguous remote read enables up to
  1 MiB of read-ahead. The first or a random small read stays small, so opening a
  tiny header does not download a whole megabyte. Identical simultaneous reads
  share a request; different offsets can still run in parallel.
- Read windows are memory-only, expire after one second and retain at most eight
  files of at most 1 MiB each. Changes made by another NAS client can therefore
  remain visible as old bytes for up to one second. Every local mutation clears
  the windows before and after the request, including failures. Session changes
  isolate windows and an invalid sign-in cannot serve cached bytes. There is no
  disk/offline cache or deferred write queue: writes still wait for the NAS.
- The matching server (API service version `0.3.0`) supports HTTP/1.1 persistent
  connections with a 30-second idle timeout and disables Nagle's algorithm.
  Connections close after rejected/ambiguous requests. This permits reuse of
  the proxy-to-container connection for reads and acknowledged writes; whether
  UGREEN's proxy actually reuses it depends on that proxy.
- SMB availability checks share their pending worker and reuse a recent result
  for five seconds, avoiding extra blocked network lookups. Reconnecting an
  already loaded remote page checks its session without reloading the page.

## Measured request reduction and limits

The deterministic client test reads a 1 MiB binary fixture in sixteen consecutive
64 KiB blocks. It checks every returned byte and uses **2 remote requests instead
of 16**: the first 64 KiB read, then a 1 MiB read-ahead request (short at EOF).
The isolated server test transfers eight blocks using **one accepted TCP
connection**, and another test verifies three acknowledged writes on one socket.
These are request/connection counts, not a measurement of real NAS throughput or
end-to-end UGREENlink latency. Parallel/random access, larger Explorer blocks,
external edits, proxy behavior and the one-second expiry affect the gain.

Install the `v0.3.6-preview.1` Windows setup to get client read-ahead and faster
connection checks. For the server transport changes, also update the existing
NAS Docker project: set `UGREEN_DRIVE_REF=v0.3.6-preview.1`, rebuild its image from
that tag and recreate the container. Restarting the old image alone does not
update its code. Preserve the verified `DATA_PATH`, `PUID`, `PGID`, Techbase-only
mount, loopback binding and existing shortcut port. The new Windows client also
works with the previous server, but that old server still closes each connection.
No NAS deployment or real-NAS benchmark has been performed for this release.

## Checking which route is active

The client status/log identifies the mounted backend as `UGREENlink` or
`LAN-SMB`. If it says `UGREENlink` while the computer is at home, verify that
the configured SMB path is the Techbase share root and that the NAS name
resolves and port 445 is reachable from the computer. If that route cannot be
reached, remote-drive performance remains dependent on internet latency and
UGREENlink availability.

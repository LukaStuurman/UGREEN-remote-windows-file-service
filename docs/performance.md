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
  files. Since `v0.3.7-preview.1`, each file can hold a current and a next 1 MiB
  window (up to 16 MiB retained across eight files). Changes made by another NAS client can therefore
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

## Additional Windows improvements in v0.3.7-preview.1

- Binary reads/writes use WebView2 shared buffers rather than base64/JSON text
  payloads. Upload buffers are read-only to the page; response buffers are writable
  and sized to the requested byte limit, never more than 4 MiB. Responses stream
  into the buffer with overflow rejection. Eight concurrent native buffers bound
  host allocation to 32 MiB; JSON messages carry control/status data only. All
  existing exact-origin, service-root, authentication and navigation checks still
  gate buffer sharing and replies. JavaScript releases its view after completion
  or failure; the host disposes its view on completion, timeout or navigation.
  Browser fetches abort after 30 seconds. A current WebView2 Runtime is required.
- Once sequential reads consume half of a full 1 MiB window, the client starts
  fetching the next window in the background. There is one next window per file
  and at most two speculative network workers globally. The next demand read
  waits for/reuses that request instead of starting it afterward. Short/EOF or
  small/random reads do not start speculation. Results expire after one second;
  invalidated/evicted windows or changed/invalid sessions discard pending results.
  Speculative failure does not invent file data or defer a write acknowledgement.
- Concurrent stat requests for the same normalized path share one loader; so do
  concurrent directory listings. Unrelated paths and stat/list operations can run
  independently. The one-second metadata lifetime and child-stat seeding remain;
  invalidation detaches old flights so a post-mutation lookup starts afresh and an
  older completion cannot overwrite current metadata. Failures are not cached.

All three changes are client-only and work with the previous NAS API. Unit tests
check prefetch starting before the current window is consumed, demand reuse,
worker limits, failure, invalidation, metadata coalescing and byte integrity.
Browser-helper tests execute the actual transport script with streamed 4 MiB
fixtures, uploads/acknowledgements, bounds, failures and origin rejection. A
disposable Windows CI test also exercises actual WebView2 native shared buffers
in an invisible window with a fresh temporary browser profile and in-memory
intercepted HTTPS fixtures; it never opens a real NAS. The native browser test is
skipped locally to avoid opening a browser/profile on the user's machine. These
tests do not measure real-NAS latency or prove the live UGREENlink proxy behavior.

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

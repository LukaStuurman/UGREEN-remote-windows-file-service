# Pinned upstream and local extension

Runtime sources copied unchanged from `github.com/hirochachacha/go-smb2 v1.1.0`,
verified using the original Go module checksum. `UPSTREAM-SHA256.json` records
every copied source; the original BSD license and source notices are retained.
Upstream: https://github.com/hirochachacha/go-smb2/tree/v1.1.0

Only `remote_drive_handles.go` is added: access/share-aware opens, per-operation
context, rename with server-side replacement by file ID, delete disposition and
allocation size, attributes/times and delete-sharing metadata inspection. No existing upstream source is modified. These operations let
the caller retain SMB handles across Windows file callbacks. Replacement never
deletes the destination first. Tests exercise these operations against Samba.

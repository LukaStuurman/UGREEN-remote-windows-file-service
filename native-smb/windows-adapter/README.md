# Private Windows adapter identity

Tailscale v1.104.1 requests the same fixed Wintun GUID even when `--tun` has a
different name. Starting the portable daemon alongside an existing Tailscale
adapter can therefore leave engine startup stuck while its pipe already exists.

`prepare.py` verifies the pinned module version and original Windows source hash,
then copies that module into a new ignored build directory. Only
`net/tstun/tun_windows.go` is replaced, and a pure GUID unit test is added.
The temporary build modfile selects that copy with a local `replace`; the shared
Go cache, tracked go.mod/go.sum and other Tailscale builds are never changed.
The copy is removed after building. Review the patch and update the source hash
before changing the pinned Tailscale version.

The replacement derives a stable, per-adapter GUID only for this application's
`--tun=UGREEN...` names. Default Tailscale names retain the upstream GUID.
CLI flags must use the `--tun=NAME` form, as all current launchers do.
The upstream BSD-3-Clause license and modification attribution accompany the
bundled components.

`Test-AdapterCoexistence.ps1` runs only on disposable Windows CI. It starts a
stock-equivalent fixture with the unchanged default GUID, then runs the real
private-adapter startup test alongside it. Neither daemon is enrolled and no NAS
is accessed. The fixture script stops only its own created process. Local builds
run only the pure GUID test and never start an adapter.

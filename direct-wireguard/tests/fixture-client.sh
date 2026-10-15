#!/bin/sh
set -eu
umask 077
wireguard-go -f client >/dev/null 2>&1 &
vpn_process=$!
trap 'kill "$vpn_process" 2>/dev/null || true' EXIT
for attempt in $(seq 1 50); do
  test -S /run/wireguard/client.sock && break
  sleep 0.1
done
wg-quick strip /fixture/Techbase-direct.conf >/tmp/client.conf
wg setconf client /tmp/client.conf
ip address add 10.203.0.2/32 dev client
ip link set client mtu 1380 up
ip route add 10.203.0.1/32 dev client
printf 'username = fixture\npassword = fixture-test-only\n' >/tmp/smb-auth
python3 - <<'PY'
from pathlib import Path
Path('/tmp/upload.bin').write_bytes(bytes(range(256)) * 4096)
PY
smbclient //10.203.0.1/Techbase -A /tmp/smb-auth -m SMB3 \
  -c 'put /tmp/upload.bin roundtrip.bin; get roundtrip.bin /tmp/download.bin; del roundtrip.bin' >/dev/null
cmp /tmp/upload.bin /tmp/download.bin
# The endpoint is a proxy, not a LAN/default-route gateway. Even a modified
# client AllowedIPs/route must not turn it into a general network tunnel.
server_key="$(wg show client peers)"
wg set client peer "$server_key" allowed-ips "10.203.0.1/32,$NAS_ADDRESS/32"
ip route add "$NAS_ADDRESS/32" dev client
python3 - <<'PY'
import os
import socket
for host, port in [('10.203.0.1', 22), (os.environ['NAS_ADDRESS'], 445)]:
    try:
        with socket.create_connection((host, port), timeout=1):
            raise SystemExit('Forbidden network route was reachable')
    except OSError:
        pass
print('Byte-exact SMB upload/download over WireGuard and blocked forwarding verified.')
PY

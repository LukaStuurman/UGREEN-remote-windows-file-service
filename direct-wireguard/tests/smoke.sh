#!/usr/bin/env bash
set -euo pipefail
network=ugreen-wireguard-ci
server=ugreen-wireguard-server-ci
nas=ugreen-wireguard-nas-ci
client=ugreen-wireguard-client-ci
temporary="$(mktemp -d)"
cleanup() {
  docker rm --force "$client" "$server" "$nas" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf -- "$temporary"
}
trap cleanup EXIT
docker network create "$network" >/dev/null
docker build --tag ugreen-direct-wireguard:ci direct-wireguard
docker build --tag ugreen-direct-wireguard-fixture:ci --file direct-wireguard/tests/Dockerfile direct-wireguard
docker run --detach --name "$nas" --network "$network" \
  --entrypoint /app/fixture-nas.sh ugreen-direct-wireguard-fixture:ci >/dev/null
nas_address="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$nas")"
for _ in $(seq 1 30); do
  if docker exec "$nas" python3 -c 'import socket; socket.create_connection(("127.0.0.1",445),timeout=1).close()' >/dev/null 2>&1; then break; fi
  sleep 1
done
docker run --detach --name "$server" --network "$network" \
  --read-only --tmpfs /config:mode=700 --tmpfs /run --tmpfs /tmp \
  --cap-drop ALL --cap-add NET_ADMIN --cap-add NET_BIND_SERVICE \
  --device /dev/net/tun:/dev/net/tun --security-opt no-new-privileges:true \
  --env "NAS_ADDRESS=$nas_address" --env WG_ENDPOINT=192.0.2.1 \
  --env WG_FORCE_USERSPACE=1 ugreen-direct-wireguard:ci >/dev/null
ready=0
for _ in $(seq 1 40); do
  if docker exec "$server" python3 /app/app.py health >/dev/null 2>&1; then ready=1; break; fi
  if ! docker inspect --format '{{.State.Running}}' "$server" | grep -qx true; then
    docker logs "$server"
    exit 1
  fi
  sleep 1
done
test "$ready" = 1
server_address="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$server")"
docker cp "$server:/config/Techbase-direct.conf" "$temporary/Techbase-direct.conf" >/dev/null
# Fixture-only endpoint; never print or upload the generated profile/private key.
python3 - "$temporary/Techbase-direct.conf" "$server_address" <<'PY'
from pathlib import Path
import sys
p = Path(sys.argv[1])
p.write_text(p.read_text().replace('Endpoint = 192.0.2.1:51820', 'Endpoint = ' + sys.argv[2] + ':51820'))
PY
docker run --name "$client" --network "$network" --cap-add NET_ADMIN \
  --device /dev/net/tun:/dev/net/tun --env "NAS_ADDRESS=$nas_address" \
  --volume "$temporary:/fixture:ro" --entrypoint /app/fixture-client.sh ugreen-direct-wireguard-fixture:ci
docker exec "$server" python3 /app/app.py status | python3 -c \
  'import json,sys; s=json.load(sys.stdin); assert s["ready"] and s["recentHandshake"]; print("Recent WireGuard handshake verified.")'

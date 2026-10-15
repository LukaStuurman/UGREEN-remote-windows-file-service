"""Single-peer, direct WireGuard endpoint with an SMB-only TCP proxy.

No NAS folders are mounted. NAS authentication/share permissions remain in UGOS.
No forwarding, NAT, web admin, cloud relay or public SMB listener is provided.
"""
import base64
import ipaddress
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import sys
import tempfile
import time

SERVER = "10.203.0.1"
CLIENT = "10.203.0.2"
NETWORK = ipaddress.ip_network("10.203.0.0/24")
STATE = Path("/config")
PRIVATE_NETWORKS = tuple(map(ipaddress.ip_network, ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")))


def settings(env):
    try:
        nas = ipaddress.IPv4Address(env.get("NAS_ADDRESS", ""))
    except ValueError:
        raise ValueError("NAS_ADDRESS must be the NAS LAN IPv4 address.") from None
    if not any(nas in network for network in PRIVATE_NETWORKS) or nas in NETWORK:
        raise ValueError("NAS_ADDRESS must be a private LAN address outside the WireGuard subnet.")
    endpoint = env.get("WG_ENDPOINT", "").strip()
    if not endpoint or len(endpoint) > 253 or any(c in endpoint for c in "\r\n\t /\\#=;"):
        raise ValueError("WG_ENDPOINT must be your own public IP or DDNS hostname, without a URL scheme or port.")
    try:
        address = ipaddress.ip_address(endpoint)
    except ValueError:
        address = None
    if address is not None:
        if address.version != 4 or address.is_loopback or address.is_unspecified or address.is_multicast or address.is_link_local:
            raise ValueError("WG_ENDPOINT must be a reachable IPv4 address or DDNS hostname.")
        endpoint = str(address)
    else:
        if not re.fullmatch(r"(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", endpoint):
            raise ValueError("WG_ENDPOINT is invalid.") from None
        if any(not label or len(label) > 63 or label.startswith("-") or label.endswith("-") for label in endpoint.split(".")):
            raise ValueError("WG_ENDPOINT hostname is invalid.")
    if any(endpoint.lower() == suffix or endpoint.lower().endswith("." + suffix)
           for suffix in ("ug.link", "ugapp.link", "ugdocker.link")):
        raise ValueError("A UGREENlink address cannot be a direct WireGuard endpoint.")
    try:
        port = int(env.get("WG_PORT", "51820"))
        if not 1024 <= port <= 65535:
            raise ValueError()
    except ValueError:
        raise ValueError("WG_PORT must be between 1024 and 65535.") from None
    return str(nas), endpoint, port


def command(args, text=None):
    result = subprocess.run(args, input=text, text=True, capture_output=True, timeout=15)
    if result.returncode:
        # Never include stdout/stderr, keys, endpoints or configuration in logs.
        raise RuntimeError("A required network setup command failed: " + Path(args[0]).name)
    return result.stdout.strip()


def key(name, generator, state=STATE):
    path = state / name
    if path.is_symlink():
        raise ValueError("A state file must not be a symbolic link.")
    if path.exists():
        value = path.read_text().strip()
    else:
        value = command(["wg", generator])
        secure_write(path, value + "\n")
    try:
        if len(base64.b64decode(value, validate=True)) != 32:
            raise ValueError()
    except (ValueError, TypeError):
        raise ValueError("Invalid stored WireGuard key; restore the private state backup.") from None
    path.chmod(0o600)
    return value


def secure_write(path, contents):
    if path.is_symlink():
        raise ValueError("A state file must not be a symbolic link.")
    fd, name = tempfile.mkstemp(prefix=".wireguard-", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "w") as stream:
            stream.write(contents)
        temporary.replace(path)
        path.chmod(0o600)
    finally:
        temporary.unlink(missing_ok=True)


def client_config(private, public, preshared, endpoint, port):
    return (f"[Interface]\nPrivateKey = {private}\nAddress = {CLIENT}/32\nMTU = 1380\n\n"
            f"[Peer]\nPublicKey = {public}\nPresharedKey = {preshared}\n"
            f"Endpoint = {endpoint}:{port}\nAllowedIPs = {SERVER}/32\nPersistentKeepalive = 25\n")


def firewall_rules(nas):
    # iptables-restore applies a complete table atomically, inside this container's
    # dedicated network namespace. No host firewall, NAT or routing is changed.
    return f"""*filter
:INPUT DROP [0:0]
:FORWARD DROP [0:0]
:OUTPUT DROP [0:0]
-A INPUT -i lo -j ACCEPT
-A OUTPUT -o lo -j ACCEPT
-A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
-A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
-A INPUT -i eth0 -p udp --dport 51820 -j ACCEPT
-A OUTPUT -o eth0 -p udp --sport 51820 -j ACCEPT
-A INPUT -i wg0 -s {CLIENT}/32 -d {SERVER}/32 -p tcp --dport 445 -j ACCEPT
-A OUTPUT -o eth0 -d {nas}/32 -p tcp --dport 445 -j ACCEPT
COMMIT
"""


def status():
    result = subprocess.run(["wg", "show", "wg0", "latest-handshakes"], capture_output=True, text=True, timeout=3)
    if result.returncode:
        return {"ready": False, "handshake": False}
    timestamps = [int(line.split()[1]) for line in result.stdout.splitlines() if len(line.split()) == 2]
    latest = max(timestamps, default=0)
    age = max(0, int(time.time()) - latest) if latest else None
    return {"ready": Path("/run/ready").exists(), "handshake": latest > 0,
            "handshakeAgeSeconds": age, "recentHandshake": age is not None and age < 180}


def run():
    nas, endpoint, port = settings(os.environ)
    STATE.mkdir(mode=0o700, parents=True, exist_ok=True)
    STATE.chmod(0o700)
    os.umask(0o077)
    private = key("server.key", "genkey")
    client = key("windows.key", "genkey")
    preshared = key("windows.psk", "genpsk")
    server_public = command(["wg", "pubkey"], private)
    client_public = command(["wg", "pubkey"], client)
    secure_write(STATE / "Techbase-direct.conf", client_config(client, server_public, preshared, endpoint, port))
    try:
        command(["iptables-restore", "--wait", "5"], firewall_rules(nas))
    except RuntimeError:
        command(["iptables-legacy-restore", "--wait", "5"], firewall_rules(nas))
    workers = []
    stopping = False

    def stop(*_):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        created = subprocess.run(["ip", "link", "add", "wg0", "type", "wireguard"], capture_output=True)
        if created.returncode or os.environ.get("WG_FORCE_USERSPACE") == "1":
            if created.returncode == 0:
                command(["ip", "link", "delete", "wg0"])
            worker = subprocess.Popen(["wireguard-go", "-f", "wg0"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            workers.append(worker)
            for _ in range(50):
                if Path("/run/wireguard/wg0.sock").exists():
                    break
                if worker.poll() is not None:
                    raise RuntimeError("WireGuard userspace failed; verify /dev/net/tun and NET_ADMIN support.")
                time.sleep(0.1)
            else:
                raise RuntimeError("WireGuard interface creation timed out.")
            mode = "userspace"
        else:
            mode = "kernel"
        command(["wg", "setconf", "wg0", "/dev/stdin"],
                f"[Interface]\nPrivateKey = {private}\nListenPort = 51820\n\n"
                f"[Peer]\nPublicKey = {client_public}\nPresharedKey = {preshared}\nAllowedIPs = {CLIENT}/32\n")
        command(["ip", "address", "add", SERVER + "/32", "dev", "wg0"])
        command(["ip", "link", "set", "dev", "wg0", "mtu", "1380", "up"])
        command(["ip", "route", "add", CLIENT + "/32", "dev", "wg0"])
        proxy = subprocess.Popen(["socat", f"TCP4-LISTEN:445,bind={SERVER},reuseaddr,fork,max-children=32",
                                  f"TCP4:{nas}:445,connect-timeout=5"],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        workers.append(proxy)
        # Verify the local listener, without enumerating files or probing NAS data.
        for _ in range(30):
            if proxy.poll() is not None:
                raise RuntimeError("The private SMB proxy could not start.")
            try:
                with socket.create_connection((SERVER, 445), timeout=0.2):
                    break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("The private SMB listener did not become ready.")
        Path("/run/ready").touch(mode=0o600)
        print("Direct WireGuard endpoint ready (" + mode + "). Client profile is in the private state volume.", flush=True)
        while not stopping:
            if any(worker.poll() is not None for worker in workers):
                raise RuntimeError("A required private network process stopped.")
            time.sleep(0.5)
    finally:
        Path("/run/ready").unlink(missing_ok=True)
        for worker in workers:
            worker.terminate()
        for worker in workers:
            try:
                worker.wait(timeout=3)
            except subprocess.TimeoutExpired:
                worker.kill()
                worker.wait()
        subprocess.run(["ip", "link", "delete", "wg0"], capture_output=True)


if __name__ == "__main__":
    try:
        if len(sys.argv) > 1 and sys.argv[1] in ("status", "health"):
            result = status()
            print(json.dumps(result))
            raise SystemExit(0 if result["ready"] else 1)
        run()
    except (ValueError, RuntimeError, OSError, subprocess.SubprocessError) as error:
        # Validation messages are static; never emit config, identifiers or secrets.
        print(str(error) if isinstance(error, (ValueError, RuntimeError)) else "Direct network setup failed.", flush=True)
        raise SystemExit(2)

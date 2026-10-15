import base64
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import app


class ConfigurationTests(unittest.TestCase):
    def test_only_private_nas_ipv4_and_own_endpoint_are_accepted(self):
        valid = {"NAS_ADDRESS": "192.168.50.10", "WG_ENDPOINT": "vpn.example.com"}
        self.assertEqual(app.settings(valid), ("192.168.50.10", "vpn.example.com", 51820))
        for nas in ("", "localhost", "127.0.0.1", "0.0.0.0", "203.0.113.10", "10.203.0.1", "fe80::1", "192.168.1.1\n"):
            with self.subTest(nas=nas), self.assertRaises(ValueError):
                app.settings({**valid, "NAS_ADDRESS": nas})
        for endpoint in ("", "https://vpn.example.com", "vpn.example.com:51820", "x\nPrivateKey=x", "127.0.0.1",
                         "0.0.0.0", "224.0.0.1", "::1", "2001:db8::1", "x;touch", "x..example.com",
                         "ug.link", "app.example.ugdocker.link", "x.ugapp.link"):
            with self.subTest(endpoint=endpoint), self.assertRaises(ValueError):
                app.settings({**valid, "WG_ENDPOINT": endpoint})
        for port in ("0", "-1", "1023", "65536", "UDP", "51820\nPostUp=x"):
            with self.subTest(port=port), self.assertRaises(ValueError):
                app.settings({**valid, "WG_PORT": port})

    def test_client_profile_is_split_tunnel_with_no_credentials_or_default_route(self):
        profile = app.client_config("private-fixture", "public-fixture", "psk-fixture", "vpn.example.com", 51821)
        self.assertIn("AllowedIPs = 10.203.0.1/32", profile)
        self.assertIn("Endpoint = vpn.example.com:51821", profile)
        self.assertIn("PersistentKeepalive = 25", profile)
        for forbidden in ("0.0.0.0/0", "::/0", "DNS =", "PostUp", "password", "UGREENlink"):
            self.assertNotIn(forbidden, profile)

    def test_firewall_allows_only_tunnel_smb_and_one_nas_destination(self):
        rules = app.firewall_rules("192.168.50.10")
        self.assertIn(":INPUT DROP", rules)
        self.assertIn(":FORWARD DROP", rules)
        self.assertIn(":OUTPUT DROP", rules)
        self.assertIn("-i wg0 -s 10.203.0.2/32 -d 10.203.0.1/32 -p tcp --dport 445", rules)
        self.assertIn("-o eth0 -d 192.168.50.10/32 -p tcp --dport 445", rules)
        self.assertNotIn("-A FORWARD", rules)
        self.assertNotIn("MASQUERADE", rules)
        self.assertNotIn("*nat", rules)

    def test_keys_persist_and_invalid_keys_never_appear_in_errors(self):
        fixture = base64.b64encode(bytes(range(32))).decode()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(app, "command", return_value=fixture) as generate:
                self.assertEqual(app.key("fixture.key", "genkey", root), fixture)
                self.assertEqual(app.key("fixture.key", "genkey", root), fixture)
                self.assertEqual(generate.call_count, 1)
            path = root / "invalid.key"
            path.write_text("do-not-log-this-fixture")
            with self.assertRaises(ValueError) as error:
                app.key("invalid.key", "genkey", root)
            self.assertNotIn(path.read_text(), str(error.exception))
            app.secure_write(root / "profile.conf", "fixture-one")
            app.secure_write(root / "profile.conf", "fixture-two")
            self.assertEqual((root / "profile.conf").read_text(), "fixture-two")
            self.assertFalse(list(root.glob(".wireguard-*")))


if __name__ == "__main__":
    unittest.main()

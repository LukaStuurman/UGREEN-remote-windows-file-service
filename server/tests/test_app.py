import json
import http.client
import socket
import tempfile
import threading
import unittest
from http.server import ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

import app


class CountingServer(ThreadingHTTPServer):
    accepted_connections = 0

    def get_request(self):
        request = super().get_request()
        self.accepted_connections += 1
        return request


class FileApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tempdir = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tempdir.name) / "share"
        cls.root.mkdir()
        app.DATA_ROOT = cls.root.resolve()
        cls.server = CountingServer(("127.0.0.1", 0), app.Handler)
        cls.server.daemon_threads = True
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=2)
        cls.tempdir.cleanup()

    def request(self, method, route, *, body=None, drive_client=True):
        headers = {}
        if drive_client:
            headers["X-UGREEN-Remote-Drive"] = "1"
        data = body
        if isinstance(body, dict):
            headers["Content-Type"] = "application/json"
            data = json.dumps(body).encode()
        req = Request(self.base + route, data=data, headers=headers, method=method)
        return urlopen(req, timeout=3)

    def test_file_lifecycle_and_byte_ranges(self):
        self.request("POST", "/api/v1/mkdir?" + urlencode({"path": "docs"})).close()
        self.request("POST", "/api/v1/create?" + urlencode({"path": "docs/hello.txt"})).close()
        self.request("PUT", "/api/v1/write?" + urlencode({"path": "docs/hello.txt", "offset": "0"}), body=b"hello world").close()

        response = self.request("GET", "/api/v1/read?" + urlencode({"path": "docs/hello.txt", "offset": "6", "length": "5"}))
        self.assertEqual(response.read(), b"world")
        response.close()

        listing = json.loads(self.request("GET", "/api/v1/list?" + urlencode({"path": "docs"})).read())
        self.assertEqual([item["name"] for item in listing], ["hello.txt"])
        stat = json.loads(self.request("GET", "/api/v1/stat?" + urlencode({"path": "docs/hello.txt"})).read())
        self.assertEqual(stat["size"], 11)

        self.request("POST", "/api/v1/resize?" + urlencode({"path": "docs/hello.txt", "size": "5"})).close()
        stat = json.loads(self.request("GET", "/api/v1/stat?" + urlencode({"path": "docs/hello.txt"})).read())
        self.assertEqual(stat["size"], 5)
        self.request("POST", "/api/v1/rename", body={"source": "docs/hello.txt", "target": "docs/renamed.txt"}).close()
        self.request("DELETE", "/api/v1/file?" + urlencode({"path": "docs/renamed.txt"})).close()
        self.request("DELETE", "/api/v1/directory?" + urlencode({"path": "docs"})).close()

    def test_file_api_requires_the_windows_client_header(self):
        with self.assertRaises(HTTPError) as denied:
            self.request("GET", "/api/v1/stat?path=", drive_client=False)
        self.assertEqual(denied.exception.code, 403)

    def test_file_mutation_rejects_missing_client_header(self):
        route = "/api/v1/create?" + urlencode({"path": "must-not-exist.txt"})
        with self.assertRaises(HTTPError) as denied:
            self.request("POST", route, drive_client=False)
        self.assertEqual(denied.exception.code, 403)
        self.assertFalse((self.root / "must-not-exist.txt").exists())

    def test_file_api_works_without_a_secondary_access_code(self):
        result = json.loads(self.request("GET", "/api/v1/stat?path=").read())
        self.assertTrue(result["isDirectory"])

    def test_parent_traversal_is_rejected(self):
        with self.assertRaises(HTTPError) as denied:
            self.request("GET", "/api/v1/stat?" + urlencode({"path": "../outside"}))
        self.assertEqual(denied.exception.code, 400)

    def test_append_write_adds_bytes_at_end_and_rejects_other_negative_offsets(self):
        self.request("POST", "/api/v1/create?" + urlencode({"path": "append.txt"})).close()
        self.request("PUT", "/api/v1/write?" + urlencode({"path": "append.txt", "offset": "0"}), body=b"start").close()
        self.request("PUT", "/api/v1/write?" + urlencode({"path": "append.txt", "offset": "-1"}), body=b"-end").close()

        response = self.request("GET", "/api/v1/read?" + urlencode({"path": "append.txt", "offset": "0", "length": "100"}))
        self.assertEqual(response.read(), b"start-end")
        response.close()

        with self.assertRaises(HTTPError) as denied:
            self.request("PUT", "/api/v1/write?" + urlencode({"path": "append.txt", "offset": "-2"}), body=b"bad")
        self.assertEqual(denied.exception.code, 400)

        self.request("DELETE", "/api/v1/file?" + urlencode({"path": "append.txt"})).close()

    def test_symlinks_are_rejected(self):
        target = self.root.parent / "outside-test"
        try:
            target.mkdir(exist_ok=True)
            link = self.root / "escape"
            try:
                link.symlink_to(target, target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest("symlink creation is unavailable in this Windows account")
            with self.assertRaises(HTTPError) as denied:
                self.request("GET", "/api/v1/list?path=escape")
            self.assertEqual(denied.exception.code, 400)
        finally:
            if target.exists():
                try:
                    target.rmdir()
                except OSError:
                    pass

    def test_health_is_public_but_does_not_disclose_root(self):
        result = json.loads(self.request("GET", "/api/v1/health", drive_client=False).read())
        self.assertEqual(result["status"], "ok")
        self.assertNotIn(str(self.root), json.dumps(result))

    def test_techbase_host_path_must_be_absolute_and_named_techbase(self):
        for path in (
            "relative/Techbase",
            "C:/Techbase",
            r"C:\Techbase",
            "/volume1/OtherShare",
            "/volume1/techbase",
            "/volume1/Techbase/../OtherShare",
        ):
            with self.subTest(path=path), self.assertRaises(ValueError):
                app.validate_techbase_host_path(path)

        app.validate_techbase_host_path("/volume1/Techbase")

    def test_repeated_file_reads_reuse_one_tcp_connection(self):
        fixture = self.root / "keepalive-read.bin"
        fixture.write_bytes(bytes(range(256)) * 1024)
        before = self.server.accepted_connections
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        try:
            for offset in range(0, 256 * 1024, 32 * 1024):
                connection.request("GET", "/api/v1/read?" + urlencode({
                    "path": fixture.name, "offset": offset, "length": 32 * 1024
                }), headers={"X-UGREEN-Remote-Drive": "1"})
                response = connection.getresponse()
                self.assertEqual(response.version, 11)
                self.assertEqual(response.status, 200)
                self.assertEqual(len(response.read()), 32 * 1024)
                self.assertFalse(response.will_close)
            self.assertEqual(self.server.accepted_connections - before, 1)
        finally:
            connection.close()
            fixture.unlink()

    def test_writes_are_acknowledged_and_connection_can_be_reused(self):
        fixture = self.root / "keepalive-write.bin"
        before = self.server.accepted_connections
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        try:
            for offset in (0, 3, 6):
                connection.request("PUT", "/api/v1/write?" + urlencode({"path": fixture.name, "offset": offset}),
                                   body=b"abc", headers={"X-UGREEN-Remote-Drive": "1"})
                response = connection.getresponse()
                self.assertEqual(response.status, 200)
                self.assertEqual(json.loads(response.read())["written"], 3)
                self.assertFalse(response.will_close)
            self.assertEqual(fixture.read_bytes(), b"abcabcabc")
            self.assertEqual(self.server.accepted_connections - before, 1)
        finally:
            connection.close()
            fixture.unlink(missing_ok=True)

    def test_binary_write_and_read_preserve_all_bytes(self):
        fixture = self.root / "binary-roundtrip.bin"
        payload = bytes(range(256)) * 1024 + b"\r\n\x1a\n"
        try:
            response = self.request("PUT", "/api/v1/write?" + urlencode({
                "path": fixture.name, "offset": 0}), body=payload)
            self.assertEqual(json.loads(response.read())["written"], len(payload))
            response.close()
            response = self.request("GET", "/api/v1/read?" + urlencode({
                "path": fixture.name, "offset": 0, "length": len(payload)}))
            self.assertEqual(response.read(), payload)
            response.close()
            self.assertEqual(fixture.read_bytes(), payload)
        finally:
            fixture.unlink(missing_ok=True)

    def test_rejected_body_cannot_be_reinterpreted_as_a_second_request(self):
        fixture = self.root / "must-not-be-smuggled.txt"
        payload = (f"POST /api/v1/create?path={fixture.name} HTTP/1.1\r\n"
                   "Host: localhost\r\nX-UGREEN-Remote-Drive: 1\r\nContent-Length: 0\r\n\r\n").encode()
        with socket.create_connection(("127.0.0.1", self.server.server_port), timeout=3) as stream:
            # Missing required client header: reject before consuming the body.
            stream.sendall((f"PUT /api/v1/write?path=ignored.bin HTTP/1.1\r\nHost: localhost\r\n"
                            f"Content-Length: {len(payload)}\r\n\r\n").encode() + payload)
            response = http.client.HTTPResponse(stream)
            response.begin()
            self.assertEqual(response.status, 403)
            self.assertEqual(response.getheader("Connection"), "close")
            response.read()
            try:
                self.assertEqual(stream.recv(1), b"")
            except ConnectionResetError:
                pass  # Windows may reset a socket whose rejected body was unread.
        self.assertFalse(fixture.exists())

    def test_ambiguous_or_unexpected_bodies_close_the_connection(self):
        cases = [
            "Transfer-Encoding: chunked\r\n",
            "Content-Length: 1\r\nContent-Length: 1\r\n",
            "Content-Length: bad\r\n",
            "Content-Length: 1\r\n",
        ]
        for headers in cases:
            with self.subTest(headers=headers), socket.create_connection(
                    ("127.0.0.1", self.server.server_port), timeout=3) as stream:
                stream.sendall(("GET /api/v1/health HTTP/1.1\r\nHost: localhost\r\n" + headers + "\r\nx").encode())
                response = http.client.HTTPResponse(stream)
                response.begin()
                self.assertEqual(response.status, 400)
                self.assertEqual(response.getheader("Connection"), "close")
                response.read()


if __name__ == "__main__":
    unittest.main()

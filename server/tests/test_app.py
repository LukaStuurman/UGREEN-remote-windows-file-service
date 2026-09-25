import json
import tempfile
import threading
import unittest
from http.server import ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

import app


class FileApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tempdir = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tempdir.name) / "share"
        cls.root.mkdir()
        app.DATA_ROOT = cls.root.resolve()
        app.ACCESS_TOKEN = "test-token-0123456789-abcdefghijklmnopqrstuvwxyz"
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), app.Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=2)
        cls.tempdir.cleanup()

    def request(self, method, route, *, body=None, token=True):
        headers = {}
        if token:
            headers["Authorization"] = f"Bearer {app.ACCESS_TOKEN}"
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

    def test_bearer_token_is_required(self):
        with self.assertRaises(HTTPError) as denied:
            self.request("GET", "/api/v1/stat?path=", token=False)
        self.assertEqual(denied.exception.code, 401)

    def test_parent_traversal_is_rejected(self):
        with self.assertRaises(HTTPError) as denied:
            self.request("GET", "/api/v1/stat?" + urlencode({"path": "../outside"}))
        self.assertEqual(denied.exception.code, 400)

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
        result = json.loads(self.request("GET", "/api/v1/health", token=False).read())
        self.assertEqual(result["status"], "ok")
        self.assertNotIn(str(self.root), json.dumps(result))


if __name__ == "__main__":
    unittest.main()

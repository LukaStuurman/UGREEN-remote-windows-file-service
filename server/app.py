#!/usr/bin/env python3
"""Small, token-authenticated file API for the UGREEN remote drive client."""

from __future__ import annotations

import hmac
import json
import os
import re
import shutil
import stat
import sys
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

VERSION = "0.1.0"
PORT = int(os.environ.get("PORT", "8080"))
DATA_ROOT = Path(os.environ.get("DATA_ROOT", "/data")).resolve()
ACCESS_TOKEN = os.environ.get("UGREEN_DRIVE_TOKEN", "")
TECHBASE_HOST_PATH = os.environ.get("TECHBASE_HOST_PATH", "")
MAX_BODY = 4 * 1024 * 1024
MAX_RANGE = 4 * 1024 * 1024


class ApiError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status
        self.message = message


def validate_techbase_host_path(raw: str) -> None:
    """Fail closed unless the configured host bind source is the Techbase share path."""
    if not raw or "\\" in raw or not os.path.isabs(raw):
        raise ValueError("TECHBASE_HOST_PATH must be an absolute NAS host path.")
    parts = raw.split("/")
    if any(part == ".." for part in parts) or Path(raw).name != "Techbase":
        raise ValueError("TECHBASE_HOST_PATH must point directly to the Techbase share.")


def utc_iso(epoch: float) -> str:
    return datetime.fromtimestamp(epoch, tz=timezone.utc).isoformat()


def resolve_user_path(raw: str) -> Path:
    """Resolve a slash-separated path below DATA_ROOT and reject symlinks."""
    if "\x00" in raw:
        raise ApiError(400, "invalid path")
    normalized = raw.replace("\\", "/")
    if normalized.startswith("/") or re.match(r"^[A-Za-z]:", normalized):
        raise ApiError(400, "absolute paths are not allowed")
    parts = [part for part in normalized.split("/") if part not in ("", ".")]
    if any(part == ".." for part in parts):
        raise ApiError(400, "parent paths are not allowed")

    current = DATA_ROOT
    for part in parts:
        current = current / part
        try:
            if stat.S_ISLNK(os.lstat(current).st_mode):
                raise ApiError(400, "symbolic links are not supported")
        except FileNotFoundError:
            pass

    resolved = current.resolve(strict=False)
    try:
        if os.path.commonpath((str(DATA_ROOT), str(resolved))) != str(DATA_ROOT):
            raise ApiError(400, "path escapes the configured share")
    except ValueError:
        raise ApiError(400, "invalid path")
    return current


def stat_json(path: Path) -> dict[str, object]:
    try:
        info = path.stat(follow_symlinks=False)
    except FileNotFoundError:
        raise ApiError(404, "not found")
    if stat.S_ISLNK(info.st_mode):
        raise ApiError(400, "symbolic links are not supported")
    is_dir = stat.S_ISDIR(info.st_mode)
    if not is_dir and not stat.S_ISREG(info.st_mode):
        raise ApiError(400, "unsupported file type")
    return {
        "name": path.name if path != DATA_ROOT else "",
        "isDirectory": is_dir,
        "size": 0 if is_dir else info.st_size,
        "createdUtc": utc_iso(getattr(info, "st_birthtime", info.st_ctime)),
        "modifiedUtc": utc_iso(info.st_mtime),
        "attributes": int(info.st_mode),
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "UGREENRemoteDrive/" + VERSION
    sys_version = ""

    def log_message(self, fmt: str, *args: object) -> None:
        # Do not log query strings (which may contain file names) or credentials.
        request_path = urlsplit(self.path).path
        status = args[1] if len(args) > 1 else "-"
        size = args[2] if len(args) > 2 else "-"
        print(f"{self.log_date_time_string()} {self.client_address[0]} {self.command} {request_path} {status} {size}", flush=True)

    def send_json(self, status: int, value: object) -> None:
        body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def send_error_json(self, error: ApiError) -> None:
        self.send_json(error.status, {"error": error.message})

    def query(self) -> dict[str, str]:
        values = parse_qs(urlsplit(self.path).query, keep_blank_values=True)
        return {key: items[-1] for key, items in values.items()}

    def require_auth(self) -> None:
        authorization = self.headers.get("Authorization", "")
        if not authorization.startswith("Bearer "):
            raise ApiError(401, "bearer token required")
        supplied = authorization[7:].encode("utf-8")
        expected = ACCESS_TOKEN.encode("utf-8")
        if not hmac.compare_digest(supplied, expected):
            raise ApiError(401, "invalid bearer token")

    def read_body(self) -> bytes:
        raw_len = self.headers.get("Content-Length", "")
        if not raw_len.isdigit():
            raise ApiError(411, "Content-Length required")
        length = int(raw_len)
        if length > MAX_BODY:
            raise ApiError(413, "request body too large")
        return self.rfile.read(length)

    def route(self, method: str) -> None:
        path = urlsplit(self.path).path
        args = self.query()
        if method == "GET" and path == "/":
            body = (
                "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\">"
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                "<title>UGREEN Remote Drive</title><body style=\"font:16px system-ui;max-width:52rem;margin:4rem auto;padding:0 1rem\">"
                "<h1>UGREEN Remote Drive</h1><p>The service is running. Use the Windows client to sign in and mount the configured NAS share.</p>"
                "<p>API status: <a href=\"/api/v1/health\">health check</a></p></body></html>"
            ).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            return

        if method == "GET" and path == "/api/v1/health":
            self.send_json(200, {"service": "ugreen-remote-drive", "version": VERSION, "status": "ok"})
            return

        if not path.startswith("/api/v1/"):
            raise ApiError(404, "not found")
        self.require_auth()

        if method == "GET" and path == "/api/v1/stat":
            self.send_json(200, stat_json(resolve_user_path(args.get("path", ""))))
            return

        if method == "GET" and path == "/api/v1/space":
            usage = shutil.disk_usage(DATA_ROOT)
            self.send_json(200, {"available": usage.free, "total": usage.total, "free": usage.free})
            return

        if method == "GET" and path == "/api/v1/list":
            directory = resolve_user_path(args.get("path", ""))
            if not directory.is_dir():
                raise ApiError(404, "directory not found")
            try:
                entries = []
                for item in sorted(directory.iterdir(), key=lambda p: p.name.casefold()):
                    if item.is_symlink():
                        continue
                    entries.append(stat_json(item))
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_json(200, entries)
            return

        if method == "GET" and path == "/api/v1/read":
            file_path = resolve_user_path(args.get("path", ""))
            offset = int(args.get("offset", "0"))
            length = int(args.get("length", "65536"))
            if offset < 0 or length < 0 or length > MAX_RANGE:
                raise ApiError(400, "invalid byte range")
            if file_path.is_dir():
                raise ApiError(400, "path is a directory")
            try:
                fd = os.open(file_path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
                try:
                    if hasattr(os, "pread"):
                        body = os.pread(fd, length, offset)
                    else:
                        os.lseek(fd, offset, os.SEEK_SET)
                        body = os.read(fd, length)
                finally:
                    os.close(fd)
            except FileNotFoundError:
                raise ApiError(404, "not found")
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            return

        if method == "POST" and path == "/api/v1/create":
            file_path = resolve_user_path(args.get("path", ""))
            if file_path == DATA_ROOT:
                raise ApiError(400, "cannot create the share root")
            try:
                fd = os.open(file_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_NOFOLLOW", 0), 0o660)
                os.close(fd)
            except FileExistsError:
                raise ApiError(409, "already exists")
            except FileNotFoundError:
                raise ApiError(404, "parent directory not found")
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_json(201, {"created": True})
            return

        if method == "PUT" and path == "/api/v1/write":
            file_path = resolve_user_path(args.get("path", ""))
            offset = int(args.get("offset", "0"))
            if offset < 0 or file_path == DATA_ROOT or file_path.is_dir():
                raise ApiError(400, "invalid file or offset")
            body = self.read_body()
            try:
                fd = os.open(file_path, os.O_CREAT | os.O_RDWR | getattr(os, "O_NOFOLLOW", 0), 0o660)
                try:
                    if hasattr(os, "pwrite"):
                        written = os.pwrite(fd, body, offset)
                    else:
                        os.lseek(fd, offset, os.SEEK_SET)
                        written = os.write(fd, body)
                finally:
                    os.close(fd)
            except FileNotFoundError:
                raise ApiError(404, "parent directory not found")
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_json(200, {"written": written})
            return

        if method == "POST" and path == "/api/v1/mkdir":
            directory = resolve_user_path(args.get("path", ""))
            if directory == DATA_ROOT:
                raise ApiError(400, "cannot create the share root")
            try:
                directory.mkdir()
            except FileExistsError:
                raise ApiError(409, "already exists")
            except FileNotFoundError:
                raise ApiError(404, "parent directory not found")
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_json(201, {"created": True})
            return

        if method == "POST" and path == "/api/v1/resize":
            file_path = resolve_user_path(args.get("path", ""))
            size = int(args.get("size", "-1"))
            if size < 0 or size > (1 << 63) - 1 or file_path == DATA_ROOT or file_path.is_dir():
                raise ApiError(400, "invalid file size")
            try:
                with file_path.open("r+b") as handle:
                    handle.truncate(size)
            except FileNotFoundError:
                raise ApiError(404, "not found")
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_json(200, {"size": size})
            return

        if method == "POST" and path == "/api/v1/rename":
            body = self.read_body()
            try:
                payload = json.loads(body or b"{}")
                source = resolve_user_path(str(payload["source"]))
                target = resolve_user_path(str(payload["target"]))
            except (KeyError, TypeError, ValueError, json.JSONDecodeError):
                raise ApiError(400, "source and target are required")
            if source == DATA_ROOT or target == DATA_ROOT:
                raise ApiError(400, "cannot rename the share root")
            replace = bool(payload.get("replace", False))
            try:
                if not replace and target.exists():
                    raise ApiError(409, "target already exists")
                if replace:
                    os.replace(source, target)
                else:
                    os.rename(source, target)
            except FileNotFoundError:
                raise ApiError(404, "source or parent directory not found")
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_json(200, {"renamed": True})
            return

        if method == "DELETE" and path in ("/api/v1/file", "/api/v1/directory"):
            item = resolve_user_path(args.get("path", ""))
            if item == DATA_ROOT:
                raise ApiError(400, "cannot remove the share root")
            try:
                if path.endswith("/directory"):
                    item.rmdir()
                else:
                    item.unlink()
            except FileNotFoundError:
                raise ApiError(404, "not found")
            except IsADirectoryError:
                raise ApiError(400, "path is a directory")
            except NotADirectoryError:
                raise ApiError(400, "path is not a directory")
            except OSError as exc:
                if exc.errno in (39, 145):
                    raise ApiError(409, "directory is not empty")
                if exc.errno in (13, 1):
                    raise ApiError(403, "permission denied")
                raise
            self.send_json(200, {"deleted": True})
            return

        if method == "POST" and path == "/api/v1/times":
            body = self.read_body()
            try:
                payload = json.loads(body or b"{}")
                item = resolve_user_path(str(payload["path"]))
                modified = float(payload["modifiedUtcEpoch"])
            except (KeyError, TypeError, ValueError, json.JSONDecodeError):
                raise ApiError(400, "path and modifiedUtcEpoch are required")
            try:
                os.utime(item, (modified, modified), follow_symlinks=False)
            except FileNotFoundError:
                raise ApiError(404, "not found")
            except PermissionError:
                raise ApiError(403, "permission denied")
            self.send_json(200, {"updated": True})
            return

        raise ApiError(404, "not found")

    def do_GET(self) -> None:
        self.handle_route("GET")

    def do_POST(self) -> None:
        self.handle_route("POST")

    def do_PUT(self) -> None:
        self.handle_route("PUT")

    def do_DELETE(self) -> None:
        self.handle_route("DELETE")

    def handle_route(self, method: str) -> None:
        try:
            self.route(method)
        except ApiError as exc:
            self.send_error_json(exc)
        except (ValueError, OverflowError):
            self.send_error_json(ApiError(400, "invalid parameter"))
        except PermissionError:
            self.send_error_json(ApiError(403, "permission denied"))
        except FileNotFoundError:
            self.send_error_json(ApiError(404, "not found"))
        except Exception:
            # Intentionally keep server paths, stack traces and auth material out of responses.
            self.send_error_json(ApiError(500, "internal server error"))


def main() -> None:
    if len(ACCESS_TOKEN) < 32:
        print("UGREEN_DRIVE_TOKEN must contain at least 32 characters.", file=sys.stderr)
        raise SystemExit(2)
    try:
        validate_techbase_host_path(TECHBASE_HOST_PATH)
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(2)
    if not DATA_ROOT.is_dir():
        print("DATA_ROOT must be an existing directory.", file=sys.stderr)
        raise SystemExit(2)
    httpd = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    httpd.daemon_threads = True
    print(f"UGREEN Remote Drive {VERSION} listening on port {PORT}", flush=True)
    try:
        httpd.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()


if __name__ == "__main__":
    main()

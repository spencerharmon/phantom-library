#!/usr/bin/env python3
"""Minimal gostream API mock for Phantom Library integration rig.

Creates deterministic local MKV fixtures and serves /api/library/add so
Materialiser can run end-to-end without real indexers/gostream.

p14-player-stats-overlay: also serves GET /api/library/stats?hash=<40-hex>
with the gostream:phantom-player-stats-endpoint contract (lowercase hex hash,
400 invalid_infohash, 404 torrent_not_found for a hash never added, explicit
nulls, Cache-Control: no-store). A hash is "loaded" once /api/library/add was
called with a magnet carrying that btih (or once seeded via
GOSTREAM_MOCK_LOADED_HASHES). Every stats request is appended to
logs/gostream-mock-stats.jsonl so a rig scenario can prove the overlay stopped
polling after playback stopped.
"""
from __future__ import annotations
import hashlib, json, os, re, subprocess, threading, time
from urllib.parse import parse_qs, urlsplit, unquote
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(os.environ.get("JF_RIG_ROOT", "/tmp/jf-rig"))
PORT = int(os.environ.get("GOSTREAM_MOCK_PORT", "19080"))
RESPONSE_MOVIES_ROOT = os.environ.get("GOSTREAM_MOCK_RESPONSE_MOVIES_ROOT", "")
LOG = ROOT / "logs" / "gostream-mock.log"
MOVIES = ROOT / "gostream" / "movies"
SHOWS = ROOT / "gostream" / "tv"
STUBS = ROOT / "gostream" / "stubs"
STATS_LOG = ROOT / "logs" / "gostream-mock-stats.jsonl"
HEX40 = re.compile(r"^[0-9a-fA-F]{40}$")
LOADED: dict[str, float] = {}
LOADED_LOCK = threading.Lock()


def btih_of(magnet: str) -> str | None:
    """Lowercase 40-hex btih of a magnet URI, or None."""
    for part in (magnet or "").split("?", 1)[-1].split("&"):
        key, _, value = part.partition("=")
        value = unquote(value)
        if key.lower() == "xt" and value.lower().startswith("urn:btih:"):
            h = value[9:]
            return h.lower() if HEX40.match(h) else None
    return None


def mark_loaded(info_hash: str | None) -> None:
    if info_hash:
        with LOADED_LOCK:
            LOADED.setdefault(info_hash.lower(), time.time())


def iso(ts: float) -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts))


def stats_payload(info_hash: str, now: float | None = None) -> tuple[int, dict]:
    """(status, body) for GET /api/library/stats — the gostream contract shape."""
    if not info_hash or not HEX40.match(info_hash):
        return 400, {"error": "invalid_infohash"}
    h = info_hash.lower()
    with LOADED_LOCK:
        loaded_at = LOADED.get(h)
    if loaded_at is None:
        return 404, {"error": "torrent_not_found"}
    now = time.time() if now is None else now
    size = 10485760
    # Deterministic per-hash values so a scenario can assert exact proxying.
    seed = int(h[:6], 16)
    return 200, {
        "hash": h,
        "peers": 12 + seed % 5,
        "seeders": 7 + seed % 3,
        "download_rate_bytes_per_sec": 1048576,
        "bytes_completed": size,
        "size": size,
        "read_offset": 4096,
        "buffered_ahead_bytes": size - 4096,
        "buffered_ahead_seconds": None,
        "stream_state": "streaming",
        "last_read_at": iso(now),
        "torrent_sampled_at": iso(now),
        "stream_sampled_at": iso(now),
    }


def log(line: str) -> None:
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with LOG.open("a") as f:
        f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {line}\n")


def safe_name(title: str, year: int | None, suffix: str) -> str:
    base = "_".join((title or "Unknown").replace("/", " ").split())
    if year:
        base = f"{base}_{year}"
    return f"{base}_{suffix}.mkv"


def ensure_fixture(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and path.stat().st_size > 0:
        return
    cmd = [
        "/usr/lib/jellyfin-ffmpeg/ffmpeg",
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24",
        "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
        "-t", "2",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-shortest", str(path),
    ]
    subprocess.run(cmd, check=True)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _send_json(self, status: int, body: dict) -> None:
        raw = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        url = urlsplit(self.path)
        if url.path != "/api/library/stats":
            self._send_json(404, {"error": "not found"})
            log(f"GET {self.path} -> 404")
            return
        hashes = parse_qs(url.query).get("hash", [])
        status, body = stats_payload(hashes[0] if len(hashes) == 1 else "")
        # Log BEFORE responding so a caller that reads the log right after the
        # response always sees its own request (scenario 52 counts these).
        STATS_LOG.parent.mkdir(parents=True, exist_ok=True)
        with LOADED_LOCK, STATS_LOG.open("a") as f:
            f.write(json.dumps({"ts": time.time(), "hash": hashes[0] if hashes else None, "status": status,
                                "token_header": bool(self.headers.get("X-Gostream-Token"))}) + "\n")
        raw = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)
        log(f"GET {self.path} -> {status}")

    def do_OPTIONS(self):
        self._send_json(405, {"error": "method not allowed"})
        log(f"OPTIONS {self.path} -> 405")

    def _read_body(self) -> bytes:
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while True:
                line = self.rfile.readline().strip()
                if not line:
                    continue
                size = int(line.split(b";", 1)[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            return b"".join(chunks)

        length = int(self.headers.get("Content-Length", "0"))
        return self.rfile.read(length) if length else b""

    def _send_no_content(self) -> None:
        self.send_response(204)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_POST(self):
        if self.path == "/api/library/remove":
            self._handle_remove()
            return
        if self.path == "/api/library/validate":
            body = json.loads(self._read_body() or b"{}")
            lower = {str(k).lower(): v for k, v in body.items()}
            magnet = str(lower.get("magnet") or "")
            digest = hashlib.sha1(magnet.encode("utf-8")).hexdigest()[:8]
            resp = {
                "status": "valid",
                "reason": None,
                "hash": digest,
                "selected_file": {"id": 0, "path": lower.get("title") or "selected.mkv", "size": 10485760},
                "audio_tracks": [{"stream_index": 1, "language": "eng", "title": "English", "codec": "aac", "channels": 2}],
                "selected_audio_index": 1,
                "selected_audio_language": "eng",
                "validation_session_id": lower.get("validationsessionid") or lower.get("validation_session_id"),
            }
            self._send_json(200, resp)
            log(f"POST /api/library/validate body={body!r} -> 200")
            return
        if self.path == "/api/library/validate/release":
            body = json.loads(self._read_body() or b"{}")
            self._send_json(200, {"released": True})
            log(f"POST /api/library/validate/release body={body!r} -> 200")
            return
        if self.path != "/api/library/add":
            self._send_json(404, {"error": "not found"})
            log(f"POST {self.path} -> 404")
            return
        body = json.loads(self._read_body() or b"{}")
        lower = {str(k).lower(): v for k, v in body.items()}
        title = lower.get("title") or "Unknown"
        year = lower.get("year")
        try:
            year = int(year) if year is not None else None
        except (TypeError, ValueError):
            year = None
        tmdb = lower.get("tmdb") or 0
        mark_loaded(btih_of(str(lower.get("magnet") or "")))
        media_type = str(lower.get("type") or "movie").lower()
        season = lower.get("season")
        episode = lower.get("episode")
        digest = hashlib.sha1(json.dumps(body, sort_keys=True).encode("utf-8")).hexdigest()[:8]
        file_name = safe_name(title, year, digest)
        if media_type == "episode":
            try:
                season_i = int(season)
                episode_i = int(episode)
            except (TypeError, ValueError):
                season_i = 1
                episode_i = 1
            fuse = SHOWS / title / f"Season {season_i:02d}" / file_name.replace(".mkv", f"_S{season_i:02d}E{episode_i:02d}.mkv")
        else:
            fuse = MOVIES / file_name
        ensure_fixture(fuse)
        stub = STUBS / file_name
        stub.parent.mkdir(parents=True, exist_ok=True)
        stub.write_text(str(fuse))
        response_fuse = str(Path(RESPONSE_MOVIES_ROOT) / file_name) if RESPONSE_MOVIES_ROOT and media_type != "episode" else str(fuse)
        resp = {
            "stub_path": str(stub),
            "fuse_path": response_fuse,
            "hash": digest,
            "size": fuse.stat().st_size,
        }
        self._send_json(200, resp)
        log(f"POST /api/library/add body={body!r} tmdb={tmdb} title={title!r} -> 200 real={fuse} response_fuse={response_fuse}")

    def _handle_remove(self):
        # Mirrors gostream POST /api/library/remove: deletes the stub and the
        # real backing file it points at, returning 204. A missing stub yields
        # 404 (GostreamClient treats that as "already gone"). This makes the
        # source-safety rig able to assert *actual* removal vs preservation:
        # PhantomSourceManager only calls this when no other materialised item
        # still references the source, so a preserved file proves the
        # shared-hash guard fired.
        body = json.loads(self._read_body() or b"{}")
        lower = {str(k).lower(): v for k, v in body.items()}
        stub_path = lower.get("stub_path")
        if not stub_path:
            self._send_json(400, {"error": "stub_path required"})
            log("POST /api/library/remove -> 400 (missing stub_path)")
            return
        stub = Path(str(stub_path))
        if not stub.exists():
            self._send_json(404, {"error": "not found"})
            log(f"POST /api/library/remove stub={stub_path} -> 404 (absent)")
            return
        fuse_txt = ""
        try:
            fuse_txt = stub.read_text().strip()
        except OSError:
            fuse_txt = ""
        removed = []
        try:
            stub.unlink()
            removed.append(str(stub))
        except FileNotFoundError:
            pass
        if fuse_txt:
            try:
                Path(fuse_txt).unlink()
                removed.append(fuse_txt)
            except FileNotFoundError:
                pass
        self._send_no_content()
        log(f"POST /api/library/remove stub={stub_path} -> 204 removed={removed}")


def main() -> None:
    LOG.parent.mkdir(parents=True, exist_ok=True)
    LOG.write_text(f"# gostream-mock started {time.strftime('%Y-%m-%d %H:%M:%S')} on port {PORT}\n")
    MOVIES.mkdir(parents=True, exist_ok=True)
    SHOWS.mkdir(parents=True, exist_ok=True)
    STATS_LOG.parent.mkdir(parents=True, exist_ok=True)
    STATS_LOG.write_text("")
    for h in os.environ.get("GOSTREAM_MOCK_LOADED_HASHES", "").split(","):
        mark_loaded(h.strip() if HEX40.match(h.strip()) else None)
    ensure_fixture(MOVIES / "Phantom_Rig_Bravo_2024_1080p_deadbeef.mkv")
    ensure_fixture(MOVIES / "Phantom_Rig_Bravo_2024_2160p_HDR_cafebabe.mkv")
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"gostream-mock listening on 127.0.0.1:{PORT}", flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Minimal gostream API mock for Phantom Library integration rig.

Creates deterministic local MKV fixtures and serves /api/library/add so
Materialiser can run end-to-end without real indexers/gostream.
"""
from __future__ import annotations
import hashlib, json, os, subprocess, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(os.environ.get("JF_RIG_ROOT", "/tmp/jf-rig"))
PORT = int(os.environ.get("GOSTREAM_MOCK_PORT", "19080"))
RESPONSE_MOVIES_ROOT = os.environ.get("GOSTREAM_MOCK_RESPONSE_MOVIES_ROOT", "")
LOG = ROOT / "logs" / "gostream-mock.log"
MOVIES = ROOT / "gostream" / "movies"
SHOWS = ROOT / "gostream" / "tv"
STUBS = ROOT / "gostream" / "stubs"


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

    def do_POST(self):
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


def main() -> None:
    LOG.parent.mkdir(parents=True, exist_ok=True)
    LOG.write_text(f"# gostream-mock started {time.strftime('%Y-%m-%d %H:%M:%S')} on port {PORT}\n")
    MOVIES.mkdir(parents=True, exist_ok=True)
    SHOWS.mkdir(parents=True, exist_ok=True)
    ensure_fixture(MOVIES / "Phantom_Rig_Bravo_2024_1080p_deadbeef.mkv")
    ensure_fixture(MOVIES / "Phantom_Rig_Bravo_2024_2160p_HDR_cafebabe.mkv")
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"gostream-mock listening on 127.0.0.1:{PORT}", flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()

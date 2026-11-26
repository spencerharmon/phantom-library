#!/usr/bin/env python3
"""Controlled external Torrentio responses, not a fake worker or DB writer."""
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STREAMS = {
    "/stream/movie/tt99000001.json": ("Phantom.Rig.Alpha.2024.1080p.BluRay.x264", "a" * 40),
    "/stream/series/tt99100001:1:1.json": ("Phantom.Rig.Delta.S01E01.1080p.WEB-DL.x264", "b" * 40),
}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/health":
            status, body = 200, {"ready": True}
        elif self.path in STREAMS:
            title, info_hash = STREAMS[self.path]
            status, body = 200, {"streams": [{"name": "Torrentio 1080p", "infoHash": info_hash,
                "title": title + "\n👤 100 💾 8 GB"}]}
        else:
            status, body = 404, {"error": "unexpected oracle request", "path": self.path}
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)
        print(json.dumps({"path": self.path, "status": status, "body": body}), flush=True)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", int(os.environ["PHANTOM_RIG_ORACLE_PORT"])), Handler).serve_forever()

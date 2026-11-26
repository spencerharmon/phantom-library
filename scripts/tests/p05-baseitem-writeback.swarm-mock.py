#!/usr/bin/env python3
"""Deterministic swarm-health service stand-in for scripts/tests/p05-baseitem-writeback.test.sh.

Speaks the real service's wire contract (tools/swarm-health/swarm_health_service.py):
  POST /measure {"info_hashes": [...]} -> {"results": [{"info_hash","dht_peers","measured_at"}], "dht_nodes": N}
Unanswered infohashes are OMITTED (never reported as 0), exactly like the real service.

Every request and every answer is appended to --log as JSON lines, so the harness can
compare the plugin's persisted BaseItem scores and REST first pages against what the
"service" actually observed — not against a value the test assumed.

Answer function (stable per infohash, no randomness):
  n = int(hash[:6], 16)
  n % 10 == 0          -> unanswered (omitted)
  n % 10 in (1, 2)     -> 0 peers (silent traversal = dead)
  otherwise            -> 1 + n % 150 peers (live; the plugin caps the score at 100)
"""
import argparse
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_BATCH = 256


def answer(info_hash):
    n = int(info_hash[:6], 16)
    if n % 10 == 0:
        return None
    if n % 10 in (1, 2):
        return 0
    return 1 + n % 150


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--log", required=True)
    args = ap.parse_args()
    log = open(args.log, "a", buffering=1)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *a):
            pass

        def _send(self, code, obj):
            body = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.path == "/healthz":
                self._send(200, {"ok": True, "dht_nodes": 500})
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self):
            if self.path != "/measure":
                self._send(404, {"error": "not found"})
                return
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                raw = b""
                while True:
                    size = int(self.rfile.readline().strip(), 16)
                    if size == 0:
                        self.rfile.readline()
                        break
                    raw += self.rfile.read(size)
                    self.rfile.readline()
            else:
                raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            try:
                hashes = json.loads(raw)["info_hashes"]
                if not isinstance(hashes, list) or len(hashes) > MAX_BATCH:
                    raise ValueError("bad batch")
                hashes = [h.lower() for h in hashes]
                if any(len(h) != 40 for h in hashes):
                    raise ValueError("bad hash")
            except Exception as ex:  # noqa: BLE001 - reply 400 like the real service
                self._send(400, {"error": str(ex)})
                return
            now = int(time.time())
            results = []
            for h in hashes:
                peers = answer(h)
                if peers is not None:
                    results.append({"info_hash": h, "dht_peers": peers, "measured_at": now})
            log.write(json.dumps({"requested": hashes, "results": results, "at": now}) + "\n")
            self._send(200, {"results": results, "dht_nodes": 500})

    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"swarm-health mock on 127.0.0.1:{args.port}", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())

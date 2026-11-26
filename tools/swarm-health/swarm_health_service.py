#!/usr/bin/env python3
"""swarm-health: measures BitTorrent swarm size on the DHT for phantom-library.

A small standalone HTTP service (NOT part of gostream or the Jellyfin process). It
keeps ONE long-lived libtorrent session as its DHT node (re-bootstrapped from the
public routers on start; /healthz gates readiness until the routing table fills)
and answers:

  POST /measure   {"info_hashes": ["<40-hex>", ...]}
      -> {"results": [{"info_hash", "dht_peers", "measured_at"}...], "dht_nodes": N}
      Runs one DHT get_peers traversal per infohash (concurrently) and reports the
      number of DISTINCT peers announced for it, unioned over every reply the
      traversal produces. Collection ends SETTLE_S after the last new reply once every
      infohash has answered, or at MEASURE_TIMEOUT_S. libtorrent only replies when a
      traversal finds peers, so an infohash with no reply after the full window on a
      healthy routing table (>= MIN_DHT_NODES) is reported as 0 (dead swarm).
      Recent answers are cached for CACHE_TTL_S.
  GET  /healthz   200 once the routing table has >= MIN_DHT_NODES nodes, else 503.
  GET  /metrics   Prometheus text: dht nodes, measurements, batch durations, peer counts.

Configuration (env): LISTEN_PORT (8095), DHT_PORT (0 = ephemeral), MEASURE_TIMEOUT_S (30), SETTLE_S (5), MAX_BATCH (256), CACHE_TTL_S (3600), MIN_DHT_NODES (20),
DHT_BOOTSTRAP (comma list host:port).
`--selftest` checks request validation and exposition without network.
"""
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HEX = set("0123456789abcdef")
MAX_BODY = 1 << 20


def read_body(headers, rfile):
    """Request body, honouring Content-Length or chunked transfer encoding (.NET's
    PostAsJsonAsync streams JSON chunked, without a Content-Length)."""
    if "chunked" in (headers.get("Transfer-Encoding") or "").lower():
        body = b""
        while True:
            size = int(rfile.readline().split(b";", 1)[0].strip() or b"0", 16)
            if size == 0:
                while rfile.readline() not in (b"\r\n", b"\n", b""):
                    pass
                return body
            body += rfile.read(size)
            rfile.readline()
            if len(body) > MAX_BODY:
                raise ValueError("request body too large")
    length = int(headers.get("Content-Length") or 0)
    if length > MAX_BODY:
        raise ValueError("request body too large")
    return rfile.read(length) if length else b""
PEER_BUCKETS = (0, 1, 2, 5, 10, 20, 50, 100, 200, 500)


def env_int(name, default):
    v = os.environ.get(name, "")
    return int(v) if v else default


def normalize_hashes(raw, max_batch):
    """Validate/normalize a request's infohash list; raises ValueError on bad input."""
    if not isinstance(raw, list):
        raise ValueError("info_hashes must be a list")
    if len(raw) > max_batch:
        raise ValueError(f"at most {max_batch} info_hashes per request")
    out = []
    for h in raw:
        if not isinstance(h, str):
            raise ValueError("info_hash must be a string")
        h = h.strip().lower()
        if len(h) != 40 or not set(h) <= HEX:
            raise ValueError(f"invalid info_hash: {h[:48]}")
        if h not in out:
            out.append(h)
    return out


class Metrics:
    def __init__(self):
        self.lock = threading.Lock()
        self.measured = 0
        self.unanswered = 0
        self.cache_hits = 0
        self.batches = 0
        self.batch_seconds_sum = 0.0
        self.peer_buckets = [0] * (len(PEER_BUCKETS) + 1)
        self.peer_sum = 0

    def observe_peers(self, n):
        self.peer_sum += n
        for i, b in enumerate(PEER_BUCKETS):
            if n <= b:
                self.peer_buckets[i] += 1
        self.peer_buckets[-1] += 1

    def render(self, dht_nodes):
        with self.lock:
            lines = [
                "# HELP swarm_health_dht_nodes DHT routing-table nodes of the service's session.",
                "# TYPE swarm_health_dht_nodes gauge",
                f"swarm_health_dht_nodes {dht_nodes}",
                "# HELP swarm_health_measurements_total Infohashes measured by result.",
                "# TYPE swarm_health_measurements_total counter",
                f'swarm_health_measurements_total{{result="measured"}} {self.measured}',
                f'swarm_health_measurements_total{{result="dead"}} {self.unanswered}',
                f'swarm_health_measurements_total{{result="cached"}} {self.cache_hits}',
                "# HELP swarm_health_batch_seconds Wall time of /measure batches.",
                "# TYPE swarm_health_batch_seconds summary",
                f"swarm_health_batch_seconds_sum {self.batch_seconds_sum}",
                f"swarm_health_batch_seconds_count {self.batches}",
                "# HELP swarm_health_dht_peers Distinct DHT peers per measured infohash.",
                "# TYPE swarm_health_dht_peers histogram",
            ]
            for i, b in enumerate(PEER_BUCKETS):
                lines.append(f'swarm_health_dht_peers_bucket{{le="{b}"}} {self.peer_buckets[i]}')
            lines.append(f'swarm_health_dht_peers_bucket{{le="+Inf"}} {self.peer_buckets[-1]}')
            lines.append(f"swarm_health_dht_peers_sum {self.peer_sum}")
            lines.append(f"swarm_health_dht_peers_count {self.peer_buckets[-1]}")
        return "\n".join(lines) + "\n"


class Dht:
    def __init__(self, lt, port, bootstrap, timeout_s, cache_ttl_s, metrics, settle_s=5):
        self.lt = lt
        self.settle_s = settle_s
        self.timeout_s = timeout_s
        self.cache_ttl_s = cache_ttl_s
        self.metrics = metrics
        self.cache = {}
        self.measure_lock = threading.Lock()
        settings = {
            "listen_interfaces": f"0.0.0.0:{port},[::]:{port}",
            "enable_dht": True, "enable_lsd": False, "enable_upnp": False, "enable_natpmp": False,
            "dht_bootstrap_nodes": bootstrap,
            "alert_mask": lt.alert_category.dht_operation,
        }
        self.session = lt.session(settings)

    def dht_nodes(self):
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", DeprecationWarning)
            return int(self.session.status().dht_nodes)


    def measure(self, hashes):
        now = time.time()
        results, todo = {}, []
        for h in hashes:
            c = self.cache.get(h)
            if c and now - c[1] < self.cache_ttl_s:
                results[h] = c
                with self.metrics.lock:
                    self.metrics.cache_hits += 1
            else:
                todo.append(h)
        if todo:
            with self.measure_lock:
                self.session.pop_alerts()
                start = time.time()
                for h in todo:
                    self.session.dht_get_peers(self.lt.sha1_hash(bytes.fromhex(h)))
                wanted = set(todo)
                seen = {h: set() for h in todo}
                last_new = start
                while time.time() - start < self.timeout_s:
                    answered = all(seen[h] for h in todo)
                    if answered and time.time() - last_new >= self.settle_s:
                        break
                    self.session.wait_for_alert(500)
                    for a in self.session.pop_alerts():
                        if isinstance(a, self.lt.dht_get_peers_reply_alert):
                            h = str(a.info_hash).lower()
                            if h in wanted:
                                before = len(seen[h])
                                seen[h].update(str(p) for p in a.peers())
                                if len(seen[h]) > before:
                                    last_new = time.time()
                finished = time.time()
                silent = 0
                for h in todo:
                    peers = len(seen[h])
                    silent += peers == 0
                    results[h] = (peers, finished)
                    self.cache[h] = results[h]
                with self.metrics.lock:
                    for h in todo:
                        self.metrics.observe_peers(len(seen[h]))
                    self.metrics.measured += len(todo) - silent
                    self.metrics.unanswered += silent
                    self.metrics.batches += 1
                    self.metrics.batch_seconds_sum += time.time() - start
        if len(self.cache) > 200000:
            cutoff = time.time() - self.cache_ttl_s
            self.cache = {k: v for k, v in self.cache.items() if v[1] >= cutoff}
        return [{"info_hash": h, "dht_peers": results[h][0], "measured_at": int(results[h][1])}
                for h in hashes if h in results]


def make_handler(dht, metrics, max_batch, min_nodes):
    class H(BaseHTTPRequestHandler):
        def _send(self, code, body, ctype="application/json"):
            data = body.encode() if isinstance(body, str) else body
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path == "/metrics":
                self._send(200, metrics.render(dht.dht_nodes()), "text/plain; version=0.0.4")
            elif self.path == "/healthz":
                n = dht.dht_nodes()
                self._send(200 if n >= min_nodes else 503, json.dumps({"dht_nodes": n}))
            else:
                self._send(404, '{"error":"not found"}')

        def do_POST(self):
            if self.path != "/measure":
                self._send(404, '{"error":"not found"}')
                return
            try:
                req = json.loads(read_body(self.headers, self.rfile) or b"{}")
                hashes = normalize_hashes(req.get("info_hashes"), max_batch)
            except (ValueError, json.JSONDecodeError) as e:
                self._send(400, json.dumps({"error": str(e)}))
                return
            if dht.dht_nodes() < min_nodes:
                self._send(503, json.dumps({"error": "dht not ready", "dht_nodes": dht.dht_nodes()}))
                return
            results = dht.measure(hashes)
            self._send(200, json.dumps({"results": results, "dht_nodes": dht.dht_nodes()}))

        def log_message(self, *a):
            pass

    return H


def selftest():
    assert normalize_hashes(["A" * 40, "a" * 40, "b" * 40], 10) == ["a" * 40, "b" * 40]
    for bad in (["zz" * 20], ["a" * 39], [1], "x"):
        try:
            normalize_hashes(bad, 10)
            raise AssertionError(f"accepted {bad!r}")
        except ValueError:
            pass
    try:
        normalize_hashes(["a" * 40] * 3, 2)
        raise AssertionError("accepted oversize batch")
    except ValueError:
        pass
    m = Metrics()
    for n in (0, 3, 120):
        m.observe_peers(n)
    m.measured = 3
    body = m.render(42)
    assert "swarm_health_dht_nodes 42" in body
    assert 'swarm_health_dht_peers_bucket{le="0"} 1' in body
    assert 'swarm_health_dht_peers_bucket{le="5"} 2' in body
    assert 'swarm_health_dht_peers_bucket{le="+Inf"} 3' in body and "swarm_health_dht_peers_sum 123" in body
    import io
    chunked = io.BytesIO(b"5\r\n{\"a\":\r\n3\r\n12}\r\n0\r\n\r\n")
    assert read_body({"Transfer-Encoding": "chunked"}, chunked) == b'{"a":12}'
    assert read_body({"Content-Length": "4"}, io.BytesIO(b"abcdXX")) == b"abcd"
    assert read_body({}, io.BytesIO(b"")) == b""
    print("selftest ok")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return 0
    import libtorrent as lt

    metrics = Metrics()
    dht = Dht(lt, env_int("DHT_PORT", 0),
              os.environ.get("DHT_BOOTSTRAP", "router.bittorrent.com:6881,dht.transmissionbt.com:6881,"
                                              "dht.libtorrent.org:25401,router.utorrent.com:6881"),
              env_int("MEASURE_TIMEOUT_S", 30), env_int("CACHE_TTL_S", 3600), metrics,
              settle_s=env_int("SETTLE_S", 5))

    port = env_int("LISTEN_PORT", 8095)
    handler = make_handler(dht, metrics, env_int("MAX_BATCH", 256), env_int("MIN_DHT_NODES", 20))
    print(f"swarm-health: listening on :{port}", file=sys.stderr, flush=True)
    ThreadingHTTPServer(("0.0.0.0", port), handler).serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())

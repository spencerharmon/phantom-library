#!/usr/bin/env python3
"""Black-box HTTP/UDP integration: the service and libtorrent run unmodified.

The only controlled substitutes are remote DHT peers. No fake LT module, engine,
HTTP handler, clock, cache or scheduler is injected into the service.
"""
import concurrent.futures
from contextlib import ExitStack, closing
import hashlib
import heapq
import http.client
import ipaddress
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
SERVICE = Path(os.environ.get("SERVICE_UNDER_TEST", ROOT / "tools/swarm-health/swarm_health_service.py"))
# Optional non-loopback fixture network for testing old services which hardcode
# wildcard DHT binding. No packet is sent to this documentation-only address.
DHT_HOST = None
if os.environ.get("TEST_DHT_NETWORK") == "1":
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
        route.connect(("192.0.2.1", 9))
        DHT_HOST = route.getsockname()[0]


def encode(x):
    if isinstance(x, int):
        return b"i" + str(x).encode() + b"e"
    if isinstance(x, bytes):
        return str(len(x)).encode() + b":" + x
    if isinstance(x, list):
        return b"l" + b"".join(encode(v) for v in x) + b"e"
    return b"d" + b"".join(encode(k) + encode(v) for k, v in sorted(x.items())) + b"e"


def decode(data):
    def item(i):
        kind = data[i:i+1]
        if kind == b"i":
            j = data.index(b"e", i)
            return int(data[i+1:j]), j+1
        if kind in (b"d", b"l"):
            values, i = [], i+1
            while data[i:i+1] != b"e":
                v, i = item(i)
                values.append(v)
            return (dict(zip(values[::2], values[1::2])) if kind == b"d" else values), i+1
        j = data.index(b":", i)
        size = int(data[i:j])
        return data[j+1:j+1+size], j+1+size
    return item(0)[0]


def bloom(addresses):
    result = bytearray(256)
    for addr in addresses:
        digest = hashlib.sha1(ipaddress.ip_address(addr).packed).digest()
        for pos in (0, 2):
            bit = int.from_bytes(digest[pos:pos+2], "little") % 2048
            result[bit // 8] |= 1 << (bit % 8)
    return bytes(result)


def compact(ip, port):
    return ipaddress.ip_address(ip).packed + struct.pack("!H", port)


def info(i):
    return f"{i:040x}"


def eventually(fn, seconds=5):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        result = fn()
        if result:
            return result
        time.sleep(.02)
    raise AssertionError("condition did not converge")


class Peer:
    def __init__(self, cluster, index):
        self.cluster, self.index = cluster, index
        self.node_id = hashlib.sha1(f"fixture-{index}".encode()).digest()
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind((DHT_HOST or f"127.0.0.{index+2}", 0))
        self.address = self.sock.getsockname()
        self.sock.settimeout(.02)
        self.closed = False
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.delayed = []
        self.serial = 0
        self.errors = []

    def run(self):
        while not self.closed:
            try:
                while self.delayed and self.delayed[0][0] <= time.monotonic():
                    _, _, data, addr = heapq.heappop(self.delayed)
                    self.sock.sendto(data, addr)
                data, addr = self.sock.recvfrom(65536)
                msg = decode(data)
                if msg.get(b"y") != b"q":
                    continue
                query = msg[b"q"]
                self.cluster.messages.append((query, msg))
                r = {b"id": self.node_id}
                if query in (b"find_node", b"get_peers"):
                    r[b"nodes"] = b"".join(p.node_id + compact(*p.address) for p in self.cluster.peers[:3])
                delay = 0
                if query == b"get_peers":
                    h = msg[b"a"][b"info_hash"].hex()
                    self.cluster.queries.append((h, self.index, msg, time.monotonic()))
                    opts = self.cluster.data.get(h, {})
                    opts = opts.get(self.index, opts)
                    if opts.get("silent"):
                        continue
                    r[b"token"] = b"fixture"
                    r[b"values"] = [compact(*ep) for ep in opts.get("values", [])]
                    if not opts.get("unsupported"):
                        r[b"BFsd"] = opts.get("seed_filter", bloom(opts.get("seeds", [])))
                        r[b"BFpe"] = opts.get("peer_filter", bloom(opts.get("peers", [])))
                    if opts.get("malformed"):
                        r[b"BFsd"] = b"short"
                    if "nodes" in opts:
                        r[b"nodes"] = opts["nodes"]
                    delay = opts.get("delay", 0)
                packet = encode({b"t": msg[b"t"], b"y": b"r", b"r": r})
                if delay:
                    self.serial += 1
                    heapq.heappush(self.delayed, (time.monotonic()+delay, self.serial, packet, addr))
                else:
                    self.sock.sendto(packet, addr)
            except socket.timeout:
                pass
            except OSError:
                if not self.closed:
                    raise
            except Exception as e:
                self.errors.append(repr(e))

    def close(self):
        self.closed = True
        self.thread.join(timeout=1)
        self.sock.close()


class Cluster:
    def __init__(self):
        self.data, self.queries, self.messages = {}, [], []
        self.peers = [Peer(self, i) for i in range(4)]
        for p in self.peers:
            p.thread.start()

    def count(self, h):
        return sum(q[0] == h for q in self.queries)

    def close(self):
        for p in self.peers:
            p.close()
        errors = [e for p in self.peers for e in p.errors]
        if errors:
            raise AssertionError(errors)


class Process:
    def __init__(self, cluster, directory, **env):
        self.cluster, self.directory = cluster, Path(directory)
        self.env = env
        self.port = self.free_port()
        self.log = (self.directory / f"service-{time.time_ns()}.log").open("w+")
        settings = dict(os.environ, LISTEN_HOST="127.0.0.1", LISTEN_PORT=str(self.port),
                        DHT_LISTEN_HOST=DHT_HOST or "127.0.0.1", DHT_PORT="0", DHT_ALLOW_PRIVATE="1",
                        DHT_BOOTSTRAP=f"{cluster.peers[3].address[0]}:{cluster.peers[3].address[1]}",
                        STATE_DIR=str(self.directory / "state"), MIN_DHT_NODES="1",
                        MAX_QUERIES="6", MAX_INFLIGHT="8", MAX_QUEUE="8", MAX_BATCH="8",
                        MAX_NODES="8", MAX_PEERS="8", CACHE_SIZE="3", CACHE_TTL_S="2",
                        UNKNOWN_TTL_S="0.2", HTTP_READ_S="0.4", MAX_BODY="512", MAX_CHUNKS="4",
                        MEASURE_TIMEOUT_S="2", QUERY_TIMEOUT_S="0.6", REQUEST_TIMEOUT_S="6",
                        STATE_SAVE_S="0.2", PRIORITY_AGING_S="5", SETTLE_S="0")
        for key, value in env.items():
            if value is None:
                settings.pop(key, None)  # exercise the service's production default
            else:
                settings[key] = str(value)
        self.proc = subprocess.Popen([sys.executable, str(SERVICE)], env=settings,
                                     stdout=self.log, stderr=self.log)
        try:
            eventually(self.ready, 10)
        except Exception:
            self.log.seek(0)
            print(self.log.read(), file=sys.stderr)
            print("DHT fixture messages:", cluster.messages[:5], "errors:", [p.errors for p in cluster.peers], file=sys.stderr)
            if self.proc.poll() is None:
                print("health:", self.http("GET", "/healthz"), file=sys.stderr)
            self.close()
            raise

    @staticmethod
    def free_port():
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            return s.getsockname()[1]

    def ready(self):
        if self.proc.poll() is not None:
            self.log.seek(0)
            raise AssertionError(self.log.read())
        try:
            code, body, _ = self.http("GET", "/healthz")
            return body if code == 200 else None
        except OSError:
            return None

    def http(self, method, path, obj=None, headers=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        conn.request(method, path, body=None if obj is None else json.dumps(obj), headers=headers or {})
        resp = conn.getresponse()
        data = resp.read()
        result = data.decode() if path == "/metrics" else json.loads(data) if data else None
        code, hdr = resp.status, dict(resp.getheaders())
        conn.close()
        return code, result, hdr

    def measure(self, hashes, **kw):
        return self.http("POST", "/v1/measure", dict(info_hashes=hashes, **kw))

    def metrics(self):
        code, data, _ = self.http("GET", "/metrics")
        assert code == 200
        return {k.removeprefix("swarm_health_"): float(v) for k, v in
                (line.split() for line in data.splitlines() if not line.startswith("#"))}

    def raw(self, packet):
        with socket.create_connection(("127.0.0.1", self.port), timeout=3) as s:
            s.sendall(packet)
            response = bytearray()
            while True:
                try:
                    data = s.recv(4096)
                except ConnectionResetError:
                    # Closing an overloaded socket with unread request bytes can
                    # reset after the complete 503. Still require that response.
                    if not response:
                        raise
                    break
                if not data:
                    break
                response.extend(data)
            return bytes(response)

    def close(self):
        if self.proc.poll() is None:
            self.proc.terminate()  # only our disposable test child, never a deployed service
            try:
                self.proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait()
                raise AssertionError("service did not shut down within bound")
        self.log.seek(0)
        output = self.log.read()
        self.log.close()
        if self.proc.returncode != 0:
            raise AssertionError(f"service exited {self.proc.returncode}: {output}")


class Contract(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.cluster = Cluster()
        self.services = []

    def tearDown(self):
        for svc in self.services:
            svc.close()
        self.cluster.close()
        self.assertTrue(all(q in (b"get_peers", b"find_node", b"ping") for q, _ in self.cluster.messages),
                        "service must not sample candidates, announce or store items")
        self.tmp.cleanup()

    def start(self, **env):
        svc = Process(self.cluster, self.tmp.name, **env)
        self.services.append(svc)
        return svc

    def row(self, svc, h):
        code, body, _ = svc.measure([h])
        self.assertEqual(code, 200, body)
        return body["results"][0]

    def test_bep33_union_dedup_zero_and_timestamp(self):
        # Official BEP33 vector: 256 v4 + 1000 v6 -> estimate 1224.9308.
        addresses = [f"192.0.2.{i}" for i in range(256)] + [str(ipaddress.IPv6Address(int(ipaddress.IPv6Address("2001:db8::"))+i)) for i in range(1000)]
        h = info(1)
        vals = [("192.0.2.1", 6881), ("192.0.2.2", 6881), ("192.0.2.1", 6881)]
        self.cluster.data[h] = {
            0: {"seeds": addresses[:10], "peers": addresses[:700], "values": vals},
            1: {"seeds": addresses[:10], "peers": addresses[500:], "values": vals},
            2: {"seeds": addresses[:10], "peers": addresses, "values": vals},
        }
        svc = self.start()
        self.assertEqual(len(svc.ready()["node_id"]), 40)
        row = self.row(svc, h)
        self.assertEqual(row["status"], "ok", row)
        self.assertEqual(row["dht_peers"], 1225, row)
        self.assertEqual(row["dht_seeders"], 10, row)
        self.assertEqual(row["observed_peers"], 2, row)
        self.assertEqual(row["queries"], 3)
        self.assertEqual(row["checked_at"], row["measured_at"])
        self.assertLessEqual(abs(row["checked_at"] - time.time()), 2)
        for query_h, _, msg, _ in self.cluster.queries:
            if query_h == h:
                self.assertEqual(msg[b"a"][b"scrape"], 1)
        self.assertEqual(self.row(svc, h), row)
        self.assertEqual(self.cluster.count(h), 3)
        zero = self.row(svc, info(2))
        self.assertEqual((zero["status"], zero["dht_seeders"], zero["dht_peers"]), ("ok", 0, 0))
        time.sleep(2.1)
        later = self.row(svc, h)
        self.assertGreater(later["checked_at"], row["checked_at"])
        self.assertEqual(self.cluster.count(h), 6)
        self.assertGreater(svc.metrics()["cache_expired_total"], 0)

    def test_unknown_is_not_zero_even_with_healthy_routing(self):
        svc = self.start()
        h = info(10)
        self.cluster.data[h] = {"silent": True}
        # Legacy path is also used by the deployed baseline: this assertion
        # detects its exact no-replies=>0 bug, not just an absent new endpoint.
        code, body, _ = svc.http("POST", "/measure", {"info_hashes": [h]})
        self.assertEqual(code, 200, body)
        self.assertEqual(body["results"], [], "silent DHT must not become a dead swarm")
        timeout = body["observations"][0]
        self.assertEqual(timeout["status"], "timeout")
        self.assertIsNone(timeout["dht_peers"])
        self.assertIsNone(timeout["checked_at"])
        self.assertGreater(svc.ready()["dht_nodes"], 0)
        unsupported = info(11)
        self.cluster.data[unsupported] = {"unsupported": True}
        self.assertEqual(self.row(svc, unsupported)["status"], "unsupported")
        partial = info(12)
        self.cluster.data[partial] = {0: {"silent": True}, 1: {}, 2: {}}
        row = self.row(svc, partial)
        self.assertEqual(row["status"], "partial")
        self.assertIsNone(row["dht_peers"], row)
        self.assertIsNotNone(row["checked_at"])
        legacy_peers = info(13)
        self.cluster.data[legacy_peers] = {"unsupported": True, "values": [("192.0.2.5", 1234)] * 3}
        row = self.row(svc, legacy_peers)
        self.assertEqual((row["status"], row["dht_peers"], row["dht_seeders"]), ("partial", 1, None))
        malformed = info(14)
        self.cluster.data[malformed] = {"malformed": True}
        self.assertIsNone(self.row(svc, malformed)["dht_peers"])
        saturated = info(15)
        self.cluster.data[saturated] = {"peer_filter": b"\xff" * 256}
        row = self.row(svc, saturated)
        self.assertEqual(row["status"], "partial")
        self.assertIsNone(row["dht_peers"])
        one_bit = info(16)
        self.cluster.data[one_bit] = {"peer_filter": b"\x01" + b"\x00" * 255}
        self.assertEqual(self.row(svc, one_bit)["dht_peers"], 1)

    def test_query_peer_node_and_cache_budgets(self):
        svc = self.start(MAX_QUERIES=2, MAX_NODES=4, MAX_PEERS=3, UNKNOWN_TTL_S=2)
        h = info(20)
        # A malicious expansion supplies far more contacts/peers than allowed.
        nodes = b"".join(hashlib.sha1(str(i).encode()).digest() + compact("127.0.0.1", 20000+i) for i in range(20))
        self.cluster.data[h] = {"nodes": nodes, "values": [(f"192.0.2.{i}", 6881) for i in range(20)]}
        row = self.row(svc, h)
        self.assertEqual(row["queries"], 2)
        self.assertEqual(self.cluster.count(h), 2)
        self.assertEqual(row["observed_peers"], 3, row)
        self.assertEqual(row["status"], "partial")
        for i in range(21, 28):
            self.row(svc, info(i))
        metrics = svc.metrics()
        self.assertLessEqual(metrics["cache_entries"], 3)
        self.assertGreater(metrics["cache_evictions_total"], 0)
        self.assertLessEqual(metrics["node_contacts"], 4)
        self.assertLessEqual(metrics["native_pending"], 8)
        self.assertGreater(metrics["budget_exhausted_total"], 0)

    def test_oversize_udp_reply_is_unknown_not_zero(self):
        svc = self.start()
        h = info(29)
        self.cluster.data[h] = {"values": [("192.0.2.1", 6881)] * 1000}
        row = self.row(svc, h)
        self.assertEqual(row["status"], "timeout", row)
        self.assertIsNone(row["dht_peers"])
        self.assertIsNone(row["checked_at"])
        self.assertEqual(self.cluster.count(h), 3)
        self.assertEqual(svc.ready()["ready"], True)

    def test_concurrent_duplicates_priority_cancellation_and_overload(self):
        svc = self.start(MAX_QUEUE=3, QUERY_TIMEOUT_S=1, MEASURE_TIMEOUT_S=2)
        blocker, low, high, rejected = map(info, (30, 31, 32, 33))
        self.cluster.data[blocker] = {"delay": .45}
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            a = pool.submit(svc.measure, [blocker], request_id="a")
            eventually(lambda: self.cluster.count(blocker))
            b = pool.submit(svc.measure, [blocker], request_id="b")
            l = pool.submit(svc.measure, [low], request_id="low", priority=2)
            eventually(lambda: svc.metrics()["queue"] == 1)
            hi = pool.submit(svc.measure, [high], request_id="high", priority=2)
            eventually(lambda: svc.metrics()["queue"] == 2)
            promoted = pool.submit(svc.measure, [high], request_id="promoted", priority=0)
            eventually(lambda: svc.metrics()["coalesced_total"] >= 2)
            code, _, hdr = svc.measure([rejected])
            self.assertEqual(code, 429)
            self.assertEqual(hdr["Retry-After"], "2")
            # Cancelling one coalesced request must leave the other alive.
            self.assertEqual(svc.http("DELETE", "/measure/a")[0], 202)
            self.assertEqual(a.result()[0], 409)
            self.assertEqual(b.result()[0], 200)
            self.assertEqual(hi.result()[0], 200)
            self.assertEqual(promoted.result()[0], 200)
            self.assertEqual(l.result()[0], 200)
        self.assertEqual(self.cluster.count(blocker), 3)
        starts = [q[0] for q in self.cluster.queries]
        self.assertLess(starts.index(high), starts.index(low))
        self.assertGreater(svc.metrics()["coalesced_total"], 0)
        # Drop a queued request; it must never issue a query.
        hold, cancelled = info(34), info(35)
        self.cluster.data[hold] = {"delay": .4}
        with concurrent.futures.ThreadPoolExecutor() as pool:
            h = pool.submit(svc.measure, [hold], request_id="hold")
            eventually(lambda: self.cluster.count(hold))
            c = pool.submit(svc.measure, [cancelled], request_id="cancelled")
            eventually(lambda: svc.metrics()["queue"] == 1)
            self.assertEqual(svc.http("DELETE", "/measure/cancelled")[0], 202)
            self.assertEqual(c.result()[0], 409)
            self.assertEqual(h.result()[0], 200)
        self.assertEqual(self.cluster.count(cancelled), 0)
        self.assertEqual(svc.metrics()["requests"], 0)

    def test_delete_reuse_owns_exact_request_generation(self):
        svc = self.start(QUERY_TIMEOUT_S=1)
        admitted = 0
        # Repeated real socket races: DELETE and replacement bytes are sent on
        # preconnected sockets, without waiting for the old POST to unwind.
        # Alternate different-hash successors and successors joining the old
        # coalesced job. No service hooks, fake scheduler or patched clock.
        for i in range(24):
            old_hash, new_hash = info(1000 + 2*i), info(1001 + 2*i)
            if i % 2:
                new_hash = old_hash
            self.cluster.data[old_hash] = {"delay": .15}
            self.cluster.data[new_hash] = {"delay": .15}
            with self.subTest(trial=i), ExitStack() as stack:
                def connect():
                    conn = stack.enter_context(closing(http.client.HTTPConnection("127.0.0.1", svc.port, timeout=5)))
                    conn.connect()
                    return conn

                def receive(conn):
                    response = conn.getresponse()
                    return response.status, json.loads(response.read())

                old, keeper, delete, new = [connect() for _ in range(4)]
                old.request("POST", "/v1/measure", json.dumps({"info_hashes": [old_hash], "request_id": "reuse"}))
                keeper.request("POST", "/v1/measure", json.dumps({"info_hashes": [old_hash], "request_id": "keeper"}))
                eventually(lambda: svc.metrics()["requests"] == 2)
                eventually(lambda: self.cluster.count(old_hash) == 3)
                # Failed admission with the same ID must not release its owner.
                code, body, _ = svc.measure([new_hash], request_id="reuse")
                self.assertEqual((code, body["error"]), (409, "request_id already active"))
                delete.request("DELETE", "/measure/reuse")
                new.request("POST", "/v1/measure", json.dumps({"info_hashes": [new_hash], "request_id": "reuse"}))
                self.assertEqual(receive(delete), (202, {"cancelled": True}))
                self.assertEqual(receive(old), (409, {"error": "request cancelled"}))
                code, body = receive(new)
                kept_code, kept = receive(keeper)
                self.assertEqual(kept_code, 200, kept)
                self.assertEqual(kept["results"][0]["status"], "ok")
                if code == 409 and body.get("error") == "request_id already active":
                    # TCP ordering across connections is not guaranteed. Only
                    # admission before DELETE may legitimately reject reuse.
                    code, body, _ = svc.measure([new_hash], request_id="reuse")
                else:
                    admitted += 1
                self.assertEqual(code, 200, body)
                self.assertEqual(body["results"][0]["info_hash"], new_hash)
                self.assertEqual(body["results"][0]["status"], "ok")
                if new_hash == old_hash:
                    self.assertEqual(body["results"], kept["results"])
                self.assertEqual(self.cluster.count(old_hash), 3)
                self.assertEqual(self.cluster.count(new_hash), 3)
                eventually(lambda: svc.metrics()["requests"] == 0)
        self.assertGreater(admitted, 0, "no replacement exercised concurrent old-handler cleanup")
        self.assertEqual(svc.metrics()["cancelled_total"], 24)
        self.assertEqual(svc.metrics()["engine_errors_total"], 0)

    def test_disconnect_and_deadline_preserve_coalesced_successors(self):
        svc = self.start(REQUEST_TIMEOUT_S=.6, QUERY_TIMEOUT_S=1)
        for i, path in enumerate(("disconnect", "/v1/measure", "/measure")):
            h = info(1100 + i)
            self.cluster.data[h] = {"delay": .75}
            with self.subTest(cleanup=path), ExitStack() as stack:
                def connect():
                    conn = stack.enter_context(closing(http.client.HTTPConnection("127.0.0.1", svc.port, timeout=5)))
                    conn.connect()
                    return conn

                old = connect()
                old.request("POST", "/v1/measure" if path == "disconnect" else path,
                            json.dumps({"info_hashes": [h], "request_id": "reuse"}))
                eventually(lambda: self.cluster.count(h) == 3)
                # The newer coalesced waiter's deadline outlives the response.
                time.sleep(.3)
                keeper = connect()
                keeper.request("POST", "/v1/measure", json.dumps({"info_hashes": [h], "request_id": "keeper"}))
                eventually(lambda: svc.metrics()["requests"] == 2)
                if path == "disconnect":
                    old.close()
                else:
                    response = old.getresponse()
                    body = json.loads(response.read())
                    self.assertEqual(response.status, 200 if path == "/measure" else 504, body)
                    if path == "/measure":
                        self.assertTrue(body["request_timed_out"])
                        self.assertEqual(body["results"], [])
                eventually(lambda: svc.metrics()["requests"] == 1)
                successor = connect()
                successor.request("POST", "/v1/measure", json.dumps({"info_hashes": [h], "request_id": "reuse"}))
                rows = []
                for conn in (keeper, successor):
                    response = conn.getresponse()
                    body = json.loads(response.read())
                    self.assertEqual(response.status, 200, body)
                    self.assertEqual(body["results"][0]["status"], "ok")
                    rows.append(body["results"])
                self.assertEqual(rows[0], rows[1])
                self.assertEqual(self.cluster.count(h), 3, "cleanup must not restart shared traversal")
                eventually(lambda: svc.metrics()["requests"] == 0)
        self.assertEqual(svc.metrics()["cancelled_total"], 3)
        self.assertEqual(svc.metrics()["request_timeouts_total"], 2)

    def test_coalesced_waiter_admission_limit(self):
        svc = self.start(MAX_HTTP=6, MAX_REQUESTS=2, QUERY_TIMEOUT_S=1)
        h = info(350)
        self.cluster.data[h] = {"delay": .4}
        with concurrent.futures.ThreadPoolExecutor() as pool:
            first = pool.submit(svc.measure, [h])
            eventually(lambda: self.cluster.count(h))
            second = pool.submit(svc.measure, [h])
            eventually(lambda: svc.metrics()["requests"] == 2)
            code, _, hdr = svc.measure([h])
            self.assertEqual(code, 429)
            self.assertEqual(hdr["Retry-After"], "2")
            self.assertEqual(first.result()[0], 200)
            self.assertEqual(second.result()[0], 200)
        self.assertEqual(self.cluster.count(h), 3)
        self.assertEqual(svc.metrics()["requests"], 0)

    def test_priority_aging_and_atomic_batch_admission(self):
        svc = self.start(MAX_QUEUE=3, PRIORITY_AGING_S=.15, QUERY_TIMEOUT_S=1)
        hold, aged, urgent = map(info, (36, 37, 38))
        self.cluster.data[hold] = {"delay": .6}
        with concurrent.futures.ThreadPoolExecutor() as pool:
            h = pool.submit(svc.measure, [hold])
            eventually(lambda: self.cluster.count(hold))
            old = pool.submit(svc.measure, [aged], priority=2)
            eventually(lambda: svc.metrics()["queue"] == 1)
            # Two new jobs exceed the one remaining slot: admit neither.
            self.assertEqual(svc.measure([info(360), info(361)])[0], 429)
            self.assertEqual(svc.metrics()["queue"], 1)
            time.sleep(.35)
            new = pool.submit(svc.measure, [urgent], priority=0)
            self.assertEqual(h.result()[0], 200)
            self.assertEqual(old.result()[0], 200)
            self.assertEqual(new.result()[0], 200)
        starts = [q[0] for q in self.cluster.queries]
        self.assertLess(starts.index(aged), starts.index(urgent))
        self.assertEqual(self.cluster.count(info(360)), 0)
        self.assertEqual(self.cluster.count(info(361)), 0)

    def test_active_cancel_disconnect_and_native_budget_survives_timeout(self):
        svc = self.start(MAX_INFLIGHT=2, MAX_QUERIES=2, QUERY_TIMEOUT_S=.15, MEASURE_TIMEOUT_S=.4)
        for i in range(40, 45):
            self.cluster.data[info(i)] = {"silent": True}
        with concurrent.futures.ThreadPoolExecutor() as pool:
            f = pool.submit(svc.measure, [info(40)], request_id="active")
            eventually(lambda: self.cluster.count(info(40)) == 2)
            self.assertEqual(svc.http("DELETE", "/measure/active")[0], 202)
            self.assertEqual(f.result()[0], 409)
        for i in range(41, 44):
            start = time.monotonic()
            row = self.row(svc, info(i))
            self.assertEqual(row["status"], "timeout")
            self.assertLess(time.monotonic() - start, 1)
            self.assertLessEqual(svc.metrics()["native_pending"], 2)
        self.assertEqual(sum(self.cluster.count(info(i)) for i in range(40, 44)), 2)
        s = socket.create_connection(("127.0.0.1", svc.port))
        body = json.dumps({"info_hashes": [info(44)], "request_id": "disconnect"}).encode()
        s.sendall(b"POST /v1/measure HTTP/1.1\r\nHost: test\r\nContent-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body)
        eventually(lambda: svc.metrics()["requests"] == 1)
        s.close()
        eventually(lambda: svc.metrics()["requests"] == 0)
        self.assertGreaterEqual(svc.metrics()["cancelled_total"], 2)

    def test_http_framing_deadlines_auth_and_cardinality(self):
        svc = self.start(MAX_HTTP=6, MAX_REQUESTS=3, API_TOKEN="test-only-token")
        self.assertEqual(svc.measure([info(50)])[0], 401)
        headers = {"Authorization": "Bearer test-only-token"}
        for body in ([], {"info_hashes": "bad"}, {"info_hashes": [123]}, {"info_hashes": [info(50)], "priority": True}):
            self.assertEqual(svc.http("POST", "/measure", body, headers)[0], 400)
        prefix = b"POST /measure HTTP/1.1\r\nHost: test\r\n"
        cases = [
            (b"Content-Length: 999999999\r\n\r\n", 413),
            (b"Content-Length: -1\r\n\r\n", 400),
            (b"Content-Length: 1\r\nContent-Length: 1\r\n\r\n", 400),
            (b"Content-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n", 400),
            (b"Transfer-Encoding: chunked\r\n\r\nffffffffffffffff\r\n", 413),
            (b"Transfer-Encoding: chunked\r\n\r\nnope\r\n", 400),
            (b"Transfer-Encoding: chunked\r\n\r\n1\r\nxXX", 400),
            (b"Transfer-Encoding: chunked\r\n\r\n" + b"1\r\nx\r\n" * 5, 413),
            (b"Content-Length: 4\r\n\r\nx", 408),
        ]
        for packet, expected in cases:
            before = time.monotonic()
            self.assertIn(f" {expected} ".encode(), svc.raw(prefix + packet), packet)
            self.assertLess(time.monotonic() - before, 1.5)
        body = json.dumps({"info_hashes": [info(51)]}).encode()
        packet = (prefix + b"Authorization: Bearer test-only-token\r\nTransfer-Encoding: chunked\r\n\r\n" +
                  f"{len(body):x}\r\n".encode() + body + b"\r\n0\r\n\r\n")
        self.assertIn(b" 200 ", svc.raw(packet))
        self.assertIn(b" 431 ", svc.raw(prefix + b"X: " + b"a" * 9000 + b"\r\n\r\n"))
        # Whole-header timeout, not a timeout restarted per byte (slowloris).
        slow = socket.create_connection(("127.0.0.1", svc.port))
        slow.sendall(b"G")
        time.sleep(.25)
        slow.sendall(b"E")
        self.assertIn(b" 408 ", slow.recv(4096))
        slow.close()
        sockets = [socket.create_connection(("127.0.0.1", svc.port)) for _ in range(6)]
        try:
            time.sleep(.05)
            self.assertIn(b" 503 ", svc.raw(b"GET /healthz HTTP/1.1\r\nHost: test\r\n\r\n"))
        finally:
            for s in sockets:
                s.close()
        time.sleep(.1)
        before = svc.metrics()
        for i in range(52, 62):
            self.assertEqual(svc.http("POST", "/v1/measure", {"info_hashes": [info(i)]}, headers)[0], 200)
        after = svc.metrics()
        self.assertEqual(set(before), set(after))
        text = svc.http("GET", "/metrics")[1]
        self.assertNotIn("{", text)
        self.assertNotIn(info(51), text)
        self.assertGreater(after["http_rejected_total"], 0)
        self.assertLessEqual(after["http_clients"], 6)

    def test_request_deadline_bounds_queue_wait(self):
        svc = self.start(REQUEST_TIMEOUT_S=.2, QUERY_TIMEOUT_S=1)
        self.cluster.data[info(70)] = {"silent": True}
        before = time.monotonic()
        code, body, hdr = svc.measure([info(70)])
        self.assertEqual(code, 504, body)
        self.assertEqual(hdr["Retry-After"], "2")
        self.assertLess(time.monotonic() - before, .8)
        eventually(lambda: svc.metrics()["requests"] == 0)
        self.assertEqual(svc.metrics()["active"], 0)

    def test_legacy_default_128_hash_batch_preserves_progress_at_deadline(self):
        svc = self.start(MAX_BATCH=None, MAX_QUEUE=None, MAX_BODY=None,
                         REQUEST_TIMEOUT_S=.3, QUERY_TIMEOUT_S=1)
        hashes = [info(i) for i in range(100, 228)]
        for h in hashes[1:]:
            self.cluster.data[h] = {"silent": True}
        before = time.monotonic()
        code, body, _ = svc.http("POST", "/measure", {"info_hashes": hashes})
        self.assertEqual(code, 200, body)
        self.assertTrue(body["request_timed_out"])
        self.assertLess(time.monotonic() - before, 1)
        self.assertEqual(len(body["observations"]), 128)
        self.assertEqual(len(body["results"]), 1)
        self.assertEqual(body["results"][0]["info_hash"], hashes[0])
        self.assertEqual(body["results"][0]["status"], "ok")
        self.assertEqual(body["results"][0]["dht_peers"], 0)  # actual empty filters
        for row in body["observations"][1:]:
            self.assertIsNone(row["dht_peers"])
            self.assertIsNone(row["checked_at"])
            self.assertEqual(row["reason"], "request_deadline")
        self.assertEqual(svc.metrics()["requests"], 0)
        self.assertEqual(svc.metrics()["request_timeouts_total"], 1)

    def test_deep_state_corruption_recovers_and_atomically_replaces(self):
        state = Path(self.tmp.name) / "state/dht-state.json"
        state.parent.mkdir()
        # Byte-bounded is not depth-bounded: CPython raises RecursionError,
        # not ValueError, for this 32,001-byte JSON document.
        state.write_text("[" * 16000 + "0" + "]" * 16000)
        self.assertEqual(state.stat().st_size, 32001)
        # Keep the original inode open so it cannot be reused during assertions.
        with state.open("rb") as original:
            svc = self.start()
            self.assertTrue(svc.ready()["ready"], "configured bootstrap must recover")
            node_id = svc.ready()["node_id"]
            self.assertEqual(self.row(svc, info(1200))["status"], "ok")
            self.assertEqual(svc.metrics()["state_recoveries_total"], 1)
            self.assertEqual(svc.metrics()["state_loads_total"], 0)
            eventually(lambda: svc.metrics()["state_saves_total"] > 0)
            self.assertNotEqual(state.stat().st_ino, os.fstat(original.fileno()).st_ino,
                                "valid save must replace, not overwrite, corrupt state")
            self.assertEqual(original.read(), b"[" * 16000 + b"0" + b"]" * 16000)
            svc.close()
            self.services.remove(svc)
        saved = json.loads(state.read_bytes())
        self.assertEqual(saved["version"], 1)
        raw = bytes.fromhex(saved["state"])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), saved["sha256"])
        self.assertLessEqual(state.stat().st_size, 65536)
        self.assertEqual(state.stat().st_mode & 0o777, 0o600)
        self.assertFalse(state.with_suffix(".tmp").exists())
        # The replacement must be genuinely restorable, not just valid JSON.
        restored = self.start(DHT_BOOTSTRAP="")
        self.assertEqual(restored.ready()["node_id"], node_id)
        self.assertEqual(restored.metrics()["state_loads_total"], 1)
        self.assertEqual(restored.metrics()["state_recoveries_total"], 0)
        self.assertEqual(self.row(restored, info(1201))["status"], "ok")

    def test_state_io_error_still_fails_closed(self):
        state = Path(self.tmp.name) / "state/dht-state.json"
        state.mkdir(parents=True)  # deterministic open failure, even under root
        env = dict(os.environ, STATE_DIR=str(state.parent), DHT_LISTEN_HOST="127.0.0.1")
        failed = subprocess.run([sys.executable, str(SERVICE)], env=env, capture_output=True, timeout=5)
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn(b"IsADirectoryError", failed.stderr)
        self.assertTrue(state.is_dir())
        self.assertFalse(state.with_suffix(".tmp").exists())

    def test_restart_routing_identity_and_corruption_recovery(self):
        svc = self.start()
        node_id = svc.ready()["node_id"]
        self.row(svc, info(80))
        state = Path(self.tmp.name) / "state/dht-state.json"
        eventually(lambda: state.exists() and svc.metrics()["state_saves_total"] > 0)
        svc.close()
        self.services.remove(svc)
        saved = json.loads(state.read_text())
        raw = bytes.fromhex(saved["state"])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), saved["sha256"])
        self.assertNotIn(info(80), raw.hex())
        self.assertLess(state.stat().st_size, 65536)
        # No bootstrap configured on restart: saved contacts must be functional.
        second = self.start(DHT_BOOTSTRAP="", DHT_LISTEN_HOST="127.0.0.10")
        self.assertEqual(second.ready()["node_id"], node_id)
        self.assertEqual(second.metrics()["state_loads_total"], 1)
        self.assertEqual(self.row(second, info(81))["status"], "ok")
        second.close()
        self.services.remove(second)
        # A torn temporary file cannot replace the committed atomic state.
        state.with_suffix(".tmp").write_text("interrupted write")
        third = self.start(DHT_BOOTSTRAP="")
        self.assertEqual(third.ready()["node_id"], node_id)
        third.close()
        self.services.remove(third)
        state.write_bytes(b"broken")
        recovered = self.start()
        self.assertEqual(recovered.metrics()["state_recoveries_total"], 1)
        self.assertNotEqual(recovered.ready()["node_id"], node_id)
        self.assertEqual(self.row(recovered, info(82))["status"], "ok")
        # The lifetime lock fails closed instead of creating a second node on
        # the same volume. This is a separate disposable child of this test.
        env = dict(os.environ, STATE_DIR=str(state.parent), DHT_LISTEN_HOST="127.0.0.1")
        locked = subprocess.run([sys.executable, str(SERVICE)], env=env, capture_output=True, timeout=5)
        self.assertNotEqual(locked.returncode, 0)
        self.assertIn(b"BlockingIOError", locked.stderr)
        recovered.close()
        self.services.remove(recovered)
        state.write_bytes(b"x" * 65537)
        oversized = self.start()
        self.assertEqual(oversized.metrics()["state_recoveries_total"], 1)
        oversized.close()
        self.services.remove(oversized)
        # Valid envelope/checksum but invalid native DHT state must recover too.
        raw = encode({b"unexpected": 1})
        state.write_text(json.dumps({"version": 1, "state": raw.hex(), "sha256": hashlib.sha256(raw).hexdigest()}))
        invalid_native = self.start()
        self.assertEqual(invalid_native.metrics()["state_recoveries_total"], 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)

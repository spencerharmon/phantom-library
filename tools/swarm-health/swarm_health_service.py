#!/usr/bin/env python3
"""Independent demand-only swarm health service. See README.md for the wire contract.

The C++ bridge owns ONE maintained libtorrent session (Python's upstream binding
omits dht_direct_request). This process owns bounded scheduling, HTTP and cache.
"""
import asyncio
from collections import Counter, OrderedDict
from dataclasses import dataclass, field
import fcntl
import hashlib
import hmac
import ipaddress
import json
import math
import os
from pathlib import Path
import re
import signal
import struct
import sys
import time
import uuid


# Every externally configurable resource has a finite validated upper bound.
LIMITS = {
    "LISTEN_PORT": (8095, 1, 65535), "DHT_PORT": (6881, 0, 65535),
    "MAX_HTTP": (64, 4, 256), "MAX_REQUESTS": (32, 1, 128),
    "MAX_BATCH": (32, 1, 256), "MAX_QUEUE": (256, 1, 4096),
    "MAX_BODY": (16384, 128, 1048576), "MAX_HEADER": (8192, 256, 16384),
    "MAX_CHUNKS": (256, 1, 1024), "HTTP_READ_S": (5, .1, 30),
    "HTTP_WRITE_S": (3, .1, 10), "REQUEST_TIMEOUT_S": (60, .1, 300),
    "MEASURE_TIMEOUT_S": (8, .1, 30), "QUERY_TIMEOUT_S": (2, .05, 10),
    "MAX_QUERIES": (24, 1, 128), "MAX_INFLIGHT": (8, 1, 16),
    "MAX_NODES": (128, 1, 256), "MAX_PEERS": (512, 1, 4096),
    "CACHE_SIZE": (4096, 1, 100000), "CACHE_TTL_S": (3600, .1, 86400),
    "UNKNOWN_TTL_S": (5, .1, 60), "PRIORITY_AGING_S": (5, .1, 60),
    "RETRY_AFTER_S": (2, 1, 60), "MIN_DHT_NODES": (20, 0, 2560),
    "STATE_SAVE_S": (60, .1, 600), "DHT_UPLOAD_RATE": (8000, 1000, 65536),
}


class Config:
    def __init__(self):
        for key, (default, low, high) in LIMITS.items():
            typ = float if any(isinstance(x, float) for x in (default, low, high)) or key.endswith("_S") else int
            value = typ(os.environ.get(key, default))
            if not low <= value <= high or not math.isfinite(value):
                raise ValueError(f"{key} must be between {low} and {high}")
            setattr(self, key, value)
        if self.MAX_REQUESTS > self.MAX_HTTP - 2:
            raise ValueError("MAX_HTTP must reserve at least two slots beyond MAX_REQUESTS")
        self.host = os.environ.get("LISTEN_HOST", "0.0.0.0")
        self.dht_host = str(ipaddress.IPv4Address(os.environ["DHT_LISTEN_HOST"]))
        if self.dht_host == "0.0.0.0":
            raise ValueError("DHT_LISTEN_HOST must be one concrete IPv4 address, not a wildcard")
        self.bootstrap = os.environ.get("DHT_BOOTSTRAP", "")
        if len(self.bootstrap) > 4096 or len(self.bootstrap.split(",")) > 16:
            raise ValueError("at most 16 bootstrap endpoints / 4096 bytes")
        self.allow_private = os.environ.get("DHT_ALLOW_PRIVATE", "0") == "1"
        self.token = os.environ.get("API_TOKEN", "")
        if not self.token.isascii() or len(self.token) > 4096:
            raise ValueError("API_TOKEN must be at most 4096 ASCII characters")
        self.state_dir = Path(os.environ.get("STATE_DIR", "/var/lib/swarm-health"))
        self.bridge = os.environ.get("DHT_BRIDGE", str(Path(__file__).with_name("dht-bridge")))


class HttpError(Exception):
    def __init__(self, status, message):
        self.status, self.message = status, message


def hashes_from(raw, limit):
    if not isinstance(raw, list) or not 1 <= len(raw) <= limit:
        raise HttpError(400, f"info_hashes must contain 1..{limit} hashes")
    out = []
    for h in raw:
        if not isinstance(h, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", h):
            raise HttpError(400, "invalid info_hash")
        if h.lower() not in out:
            out.append(h.lower())
    return out


def estimate(bits):
    # BEP33, k=2, m=2048. Empty is exactly zero; saturation is UNKNOWN,
    # never infinity/NaN or a misleading finite count.
    zero = 2048 - bits.bit_count()
    if zero == 0:
        return None
    if bits == 0:
        return 0
    # A single set bit can arise when the two hash indices coincide. Rounding
    # 0.5 down must not turn this positive observation into a confirmed zero.
    return max(1, round(math.log(zero / 2048) / (2 * math.log(1 - 1 / 2048))))


class State:
    """One bounded checksummed state file, atomic replace+file/dir fsync.

    An exclusive lifetime flock prevents two processes sharing a node identity.
    Corrupt/oversize state is cold-started, then replaced after bootstrap. Only
    node IDs/routing contacts persist, never submitted hashes or result cache.
    """
    def __init__(self, cfg, metrics):
        self.path = cfg.state_dir / "dht-state.json"
        cfg.state_dir.mkdir(parents=True, exist_ok=True)
        self.lock = (cfg.state_dir / "node.lock").open("a")
        fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        self.metrics = metrics

    def load(self):
        try:
            with self.path.open("rb") as f:
                data = f.read(65537)
            if len(data) > 65536:
                raise ValueError("oversize state")
            obj = json.loads(data)
            raw = bytes.fromhex(obj["state"])
            if obj["version"] != 1 or hashlib.sha256(raw).hexdigest() != obj["sha256"]:
                raise ValueError("state checksum/version")
            self.metrics["state_loads_total"] += 1
            return raw.hex()
        except FileNotFoundError:
            return ""
        except (ValueError, KeyError, TypeError):
            self.metrics["state_recoveries_total"] += 1
            return ""

    def save(self, raw_hex):
        raw = bytes.fromhex(raw_hex)
        data = json.dumps({"version": 1, "state": raw_hex, "sha256": hashlib.sha256(raw).hexdigest()}).encode()
        if len(data) > 65536:
            raise ValueError("state budget exceeded")
        tmp = self.path.with_suffix(".tmp")
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, self.path)
        fd = os.open(self.path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
        self.metrics["state_saves_total"] += 1


class Bridge:
    def __init__(self, cfg, metrics, state):
        self.cfg, self.metrics, self.state = cfg, metrics, state
        self.nodes, self.node_id, self.routing_nodes, self.pending = [], "", 0, 0
        self.events = []

    async def start(self):
        self.proc = await asyncio.create_subprocess_exec(self.cfg.bridge, stdin=asyncio.subprocess.PIPE,
                                                        stdout=asyncio.subprocess.PIPE, limit=262144)
        reply = await self.call({"listen": f"{self.cfg.dht_host}:{self.cfg.DHT_PORT}", "bind_ip": self.cfg.dht_host,
                                 "bootstrap": self.cfg.bootstrap, "allow_private": self.cfg.allow_private,
                                 "max_queries": self.cfg.MAX_INFLIGHT, "max_nodes": self.cfg.MAX_NODES,
                                 "upload_rate": self.cfg.DHT_UPLOAD_RATE, "state": self.state.load()})
        if reply["recovered"]:
            self.metrics["state_recoveries_total"] += 1
        print(f"swarm-health libtorrent={reply['libtorrent']}", file=sys.stderr, flush=True)

    async def call(self, obj):
        data = json.dumps(obj).encode() + b"\n"
        if len(data) >= 262144:
            raise RuntimeError("IPC frame budget")
        async with asyncio.timeout(3):
            self.proc.stdin.write(data)
            await self.proc.stdin.drain()
            line = await self.proc.stdout.readline()
        if not line or len(line) >= 262144:
            raise RuntimeError("DHT bridge unavailable/oversize")
        reply = json.loads(line)
        if "nodes" in reply:
            self.nodes, self.node_id = reply["nodes"], reply["node_id"]
            self.routing_nodes, self.pending = reply["routing_nodes"], reply["pending"]
            self.events.extend(reply["events"])
            # Drained on every driver tick; never more than native inflight slots.
            if len(self.events) > self.cfg.MAX_INFLIGHT:
                raise RuntimeError("native event budget")
        return reply


@dataclass
class Job:
    h: str
    seq: int
    created: float
    waiters: dict = field(default_factory=dict)
    started: float = 0
    candidates: dict = field(default_factory=dict)
    queried: set = field(default_factory=set)
    pending: dict = field(default_factory=dict)
    peers: set = field(default_factory=set)
    seed_bits: int = 0
    peer_bits: int = 0
    replies: int = 0
    scrapes: int = 0
    incomplete: bool = False
    checked_at: object = None


class Service:
    def __init__(self, cfg):
        self.cfg = cfg
        self.metrics = Counter()
        self.state = State(cfg, self.metrics)
        self.dht = Bridge(cfg, self.metrics, self.state)
        self.cache, self.jobs, self.requests = OrderedDict(), {}, {}
        self.seq, self.query_seq, self.clients = 0, 0, 0
        self.active = None  # deliberately one active demand traversal
        self.failed = False
        self.closing = False

    def expire(self):
        now = time.monotonic()
        for h, (_, expiry) in list(self.cache.items()):
            if now >= expiry:
                del self.cache[h]
                self.metrics["cache_expired_total"] += 1

    def submit(self, req, legacy):
        hashes = hashes_from(req.get("info_hashes"), self.cfg.MAX_BATCH)
        priority = req.get("priority", 1)
        if type(priority) is not int or priority not in (0, 1, 2):
            raise HttpError(400, "priority must be 0, 1 or 2")
        rid = req.get("request_id", uuid.uuid4().hex)
        if not isinstance(rid, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", rid):
            raise HttpError(400, "invalid request_id")
        if rid in self.requests:
            raise HttpError(409, "request_id already active")
        if self.failed or self.dht.routing_nodes < self.cfg.MIN_DHT_NODES:
            raise HttpError(503, "dht not ready")
        self.expire()
        new = [h for h in hashes if h not in self.cache and h not in self.jobs]
        if len(self.requests) >= self.cfg.MAX_REQUESTS or len(self.jobs) + len(new) > self.cfg.MAX_QUEUE:
            self.metrics["overload_total"] += 1
            raise HttpError(429, "demand capacity exhausted")
        future = asyncio.get_running_loop().create_future()
        results = {}
        self.requests[rid] = (future, hashes, results, legacy)
        for h in hashes:
            if h in self.cache:
                results[h] = self.cache[h][0]
                self.cache.move_to_end(h)
                self.metrics["cache_hits_total"] += 1
            else:
                if h not in self.jobs:
                    self.seq += 1
                    self.jobs[h] = Job(h, self.seq, time.monotonic())
                else:
                    self.metrics["coalesced_total"] += 1
                self.jobs[h].waiters[rid] = priority
        self.complete_request(rid)
        return rid, future

    def complete_request(self, rid):
        future, hashes, results, legacy = self.requests[rid]
        if len(results) != len(hashes) or future.done():
            return
        rows = [results[h] for h in hashes]
        # Old int-valued consumers cannot deserialize null (or absent int -> 0).
        # Never put unknowns in the legacy result list. Full observations survive.
        future.set_result({"request_id": rid, "results": [r for r in rows if not legacy or r["dht_peers"] is not None],
                           "observations": rows, "dht_nodes": self.dht.routing_nodes})

    def release(self, rid, cancelled=False):
        if rid not in self.requests:
            return False
        future, hashes, _, _ = self.requests.pop(rid)
        if not future.done():
            future.cancel()
        for h in hashes:
            job = self.jobs.get(h)
            if job:
                job.waiters.pop(rid, None)
                if not job.waiters:
                    del self.jobs[h]
                    if job is self.active:
                        self.active = None
                    # Native transactions remain accounted until their real alert;
                    # no reuse of slots/IDs on cancellation or application timeout.
        if cancelled:
            self.metrics["cancelled_total"] += 1
        return True

    def add_nodes(self, job, nodes):
        for nid, ip, port in nodes:
            addr = ipaddress.ip_address(ip)
            if addr.version != 4 or not port or (not self.cfg.allow_private and not addr.is_global):
                continue
            ep = (ip, port)
            if ep not in job.candidates:
                if len(job.candidates) >= self.cfg.MAX_NODES:
                    job.incomplete = True
                    break
                job.candidates[ep] = int(nid, 16) ^ int(job.h, 16)

    def consume(self, job, event):
        if event["id"] not in job.pending:
            return
        del job.pending[event["id"]]
        if not event["valid"]:
            job.incomplete = True
            return
        job.replies += 1
        job.checked_at = int(time.time())
        seeds, peers = event.get("BFsd", ""), event.get("BFpe", "")
        if len(seeds) == 512 and len(peers) == 512:
            job.scrapes += 1
            job.seed_bits |= int(seeds, 16)
            job.peer_bits |= int(peers, 16)
        else:
            job.incomplete = True
        for packed in event["values"]:
            raw = bytes.fromhex(packed)
            if int.from_bytes(raw[-2:], "big") == 0:
                continue
            # Compact IP+port, not reply count. The same endpoint from multiple
            # nodes/packets consumes one slot. v4-mapped IPv6 canonicalized.
            ip = ipaddress.ip_address(raw[:-2])
            ip = getattr(ip, "ipv4_mapped", None) or ip
            endpoint = (str(ip), int.from_bytes(raw[-2:], "big"))
            if endpoint not in job.peers:
                if len(job.peers) == self.cfg.MAX_PEERS:
                    job.incomplete = True
                    break
                job.peers.add(endpoint)
        job.incomplete |= event.get("truncated", False)
        raw = bytes.fromhex(event.get("nodes", ""))
        if len(raw) % 26:
            job.incomplete = True
        nodes = [(raw[i:i+20].hex(), str(ipaddress.ip_address(raw[i+20:i+24])), struct.unpack("!H", raw[i+24:i+26])[0])
                 for i in range(0, len(raw) - 25, 26)]
        self.add_nodes(job, nodes)

    def finish(self, job):
        seeders = estimate(job.seed_bits) if job.scrapes else None
        total = estimate(job.seed_bits | job.peer_bits) if job.scrapes else None
        if total is None and job.scrapes:
            job.incomplete = True
        if job.peers:
            total = max(total or 0, len(job.peers))
        status = ("timeout" if not job.replies else "unsupported" if not job.scrapes and not job.peers
                  else "partial" if job.incomplete else "ok")
        # Zero requires valid empty filters on every contacted node, no dropped
        # responses/budgets. A routing table full of nodes is NOT such evidence.
        if status != "ok":
            if total == 0:
                total = None
            if seeders == 0:
                seeders = None
        result = {"info_hash": job.h, "dht_seeders": seeders, "dht_peers": total,
                  "checked_at": job.checked_at, "measured_at": job.checked_at,
                  "status": status, "observed_peers": len(job.peers),
                  "queries": len(job.queried), "replies": job.replies,
                  "retry_after_s": self.cfg.RETRY_AFTER_S if status != "ok" else 0}
        self.metrics[f"outcome_{status}_total"] += 1
        ttl = self.cfg.CACHE_TTL_S if status == "ok" else min(self.cfg.UNKNOWN_TTL_S, self.cfg.CACHE_TTL_S)
        self.cache[job.h] = (result, time.monotonic() + ttl)
        self.cache.move_to_end(job.h)
        while len(self.cache) > self.cfg.CACHE_SIZE:
            self.cache.popitem(last=False)
            self.metrics["cache_evictions_total"] += 1
        del self.jobs[job.h]
        self.active = None
        for rid in job.waiters:
            self.requests[rid][2][job.h] = result
            self.complete_request(rid)

    async def tick(self):
        await self.dht.call({"op": "poll"})
        events, self.dht.events = self.dht.events, []
        now = time.monotonic()
        job = self.active
        if job:
            for event in events:
                self.consume(job, event)
            for qid, sent in list(job.pending.items()):
                if now - sent >= self.cfg.QUERY_TIMEOUT_S:
                    del job.pending[qid]
                    job.incomplete = True
                    self.metrics["query_timeouts_total"] += 1
        if not job and self.jobs:
            # Scan a bounded map, no growing heap of stale promotion entries.
            job = min(self.jobs.values(), key=lambda j: (
                max(0, min(j.waiters.values()) - int((now - j.created) / self.cfg.PRIORITY_AGING_S)), j.seq))
            self.active = job
            job.started = now
            self.add_nodes(job, self.dht.nodes)
            self.metrics["started_total"] += 1
        if job:
            remaining = sorted((ep for ep in job.candidates if ep not in job.queried), key=job.candidates.get)
            while (remaining and job is self.active and len(job.queried) < self.cfg.MAX_QUERIES
                   and self.dht.pending < self.cfg.MAX_INFLIGHT
                   and time.monotonic() - job.started < self.cfg.MEASURE_TIMEOUT_S):
                ep = remaining.pop(0)
                self.query_seq = (self.query_seq + 1) % 2000000000
                reply = await self.dht.call({"op": "query", "id": self.query_seq, "hash": job.h,
                                             "ip": ep[0], "port": ep[1]})
                if reply["accepted"]:
                    self.metrics["queries_total"] += 1
                # IPC awaits yield to HTTP cancellation/deadlines. Never resume
                # dispatch or finish a job whose last waiter vanished meanwhile.
                if job is not self.active:
                    return
                if not reply["accepted"]:
                    job.incomplete = True
                    break
                job.queried.add(ep)
                job.pending[self.query_seq] = time.monotonic()
                events, self.dht.events = self.dht.events, []
                for event in events:
                    self.consume(job, event)
            # A reply processed inside dispatch may have added new contacts.
            remaining = [ep for ep in job.candidates if ep not in job.queried]
            exhausted = bool(remaining) and len(job.queried) >= self.cfg.MAX_QUERIES
            deadline = time.monotonic() - job.started >= self.cfg.MEASURE_TIMEOUT_S
            if deadline or (not job.pending and (not remaining or exhausted)):
                if deadline or exhausted:
                    job.incomplete = True
                    self.metrics["budget_exhausted_total"] += 1
                self.finish(job)
        self.expire()

    async def driver(self):
        next_save = time.monotonic() + self.cfg.STATE_SAVE_S
        try:
            while not self.closing:
                await self.tick()
                if time.monotonic() >= next_save:
                    reply = await self.dht.call({"op": "state"})
                    if reply.get("state"):
                        self.state.save(reply["state"])
                    next_save = time.monotonic() + self.cfg.STATE_SAVE_S
                await asyncio.sleep(.02)
        except Exception:
            self.failed = True
            self.metrics["engine_errors_total"] += 1
            for future, _, _, _ in self.requests.values():
                if not future.done():
                    future.set_exception(HttpError(503, "dht engine failed"))
            raise

    def exposition(self):
        fixed = ("state_loads_total state_saves_total state_recoveries_total cache_hits_total cache_expired_total "
                 "cache_evictions_total coalesced_total overload_total cancelled_total query_timeouts_total "
                 "started_total queries_total budget_exhausted_total engine_errors_total http_rejected_total http_write_timeouts_total "
                 "outcome_ok_total outcome_partial_total outcome_timeout_total outcome_unsupported_total").split()
        values = {key: self.metrics[key] for key in fixed}
        values.update(dht_nodes=self.dht.routing_nodes, node_contacts=len(self.dht.nodes),
                      native_pending=self.dht.pending, queue=len(self.jobs) - int(self.active is not None),
                      active=int(self.active is not None), cache_entries=len(self.cache),
                      requests=len(self.requests), http_clients=self.clients, ready=int(not self.failed and self.dht.routing_nodes >= self.cfg.MIN_DHT_NODES))
        values.update({"limit_" + name.lower(): getattr(self.cfg, name) for name in
                       ("MAX_QUEUE", "MAX_INFLIGHT", "MAX_NODES", "MAX_PEERS", "MAX_QUERIES", "CACHE_SIZE", "MAX_HTTP", "MAX_REQUESTS")})
        # No user-controlled values in metric names or labels, including IDs,
        # hashes, IPs, errors, paths, request IDs, and node identity.
        return "".join(f"# TYPE swarm_health_{k} {'counter' if k.endswith('_total') else 'gauge'}\nswarm_health_{k} {v}\n" for k, v in values.items())


async def read_request(reader, cfg):
    async with asyncio.timeout(cfg.HTTP_READ_S):
        try:
            head = await reader.readuntil(b"\r\n\r\n")
        except (asyncio.LimitOverrunError, ValueError):
            raise HttpError(431, "headers too large")
        if len(head) > cfg.MAX_HEADER:
            raise HttpError(431, "headers too large")
        lines = head[:-4].decode("ascii").split("\r\n")
        parts = lines[0].split(" ")
        if len(parts) != 3 or parts[2] not in ("HTTP/1.0", "HTTP/1.1"):
            raise HttpError(400, "invalid request line")
        method, path, _ = parts
        headers = {}
        for line in lines[1:]:
            k, sep, v = line.partition(":")
            if not sep or not re.fullmatch(r"[A-Za-z0-9-]+", k) or k.lower() in headers:
                raise HttpError(400, "invalid/duplicate header")
            headers[k.lower()] = v.strip()
        if "transfer-encoding" in headers and "content-length" in headers:
            raise HttpError(400, "ambiguous framing")
        body = bytearray()
        if "transfer-encoding" in headers:
            if headers["transfer-encoding"].lower() != "chunked":
                raise HttpError(400, "unsupported framing")
            for _ in range(cfg.MAX_CHUNKS):
                line = await reader.readuntil(b"\r\n")
                if len(line) > 80 or not re.fullmatch(rb"[0-9a-fA-F]{1,16}\r\n", line):
                    raise HttpError(400, "invalid chunk frame")
                size = int(line, 16)
                if size > cfg.MAX_BODY - len(body):
                    raise HttpError(413, "body too large")
                if size == 0:
                    if await reader.readexactly(2) != b"\r\n":
                        raise HttpError(400, "trailers unsupported")
                    break
                body.extend(await reader.readexactly(size))
                if await reader.readexactly(2) != b"\r\n":
                    raise HttpError(400, "invalid chunk terminator")
            else:
                raise HttpError(413, "too many chunks")
        else:
            length = headers.get("content-length", "0")
            if not re.fullmatch(r"[0-9]{1,10}", length):
                raise HttpError(400, "invalid content-length")
            length = int(length)
            if length > cfg.MAX_BODY:
                raise HttpError(413, "body too large")
            body = await reader.readexactly(length)
        return method, path, headers, body


async def send(writer, code, body, cfg, text=False):
    data = body.encode() if text else json.dumps(body, allow_nan=False).encode()
    ctype = "text/plain; version=0.0.4" if text else "application/json"
    header = (f"HTTP/1.1 {code} Result\r\nConnection: close\r\nContent-Type: {ctype}\r\n"
              f"Content-Length: {len(data)}\r\n")
    if code in (429, 503, 504):
        header += f"Retry-After: {int(cfg.RETRY_AFTER_S)}\r\n"
    writer.write(header.encode() + b"\r\n" + data)
    async with asyncio.timeout(cfg.HTTP_WRITE_S):
        await writer.drain()


async def serve(reader, writer, svc):
    rid, disconnected = None, None
    try:
        try:
            method, path, headers, body = await read_request(reader, svc.cfg)
        except TimeoutError:
            raise HttpError(408, "HTTP read deadline exceeded")
        if path not in ("/healthz", "/metrics") and svc.cfg.token and not hmac.compare_digest(
                headers.get("authorization", ""), "Bearer " + svc.cfg.token):
            raise HttpError(401, "unauthorized")
        if method == "GET" and path == "/healthz":
            ready = not svc.failed and svc.dht.routing_nodes >= svc.cfg.MIN_DHT_NODES
            await send(writer, 200 if ready else 503, {"dht_nodes": svc.dht.routing_nodes,
                       "node_id": svc.dht.node_id, "ready": ready}, svc.cfg)
        elif method == "GET" and path == "/metrics":
            await send(writer, 200, svc.exposition(), svc.cfg, text=True)
        elif method == "DELETE" and path.startswith("/measure/"):
            cancelled = svc.release(path.removeprefix("/measure/"), cancelled=True)
            await send(writer, 202 if cancelled else 404, {"cancelled": cancelled}, svc.cfg)
        elif method == "POST" and path in ("/measure", "/v1/measure"):
            req = json.loads(body)
            if not isinstance(req, dict):
                raise HttpError(400, "object required")
            rid, future = svc.submit(req, legacy=path == "/measure")
            disconnected = asyncio.create_task(reader.read(1))
            done, _ = await asyncio.wait((future, disconnected), timeout=svc.cfg.REQUEST_TIMEOUT_S,
                                         return_when=asyncio.FIRST_COMPLETED)
            if future in done:
                if future.cancelled():
                    raise HttpError(409, "request cancelled")
                await send(writer, 200, future.result(), svc.cfg)
            elif disconnected in done:
                svc.release(rid, cancelled=True)
            else:
                svc.release(rid, cancelled=True)
                raise HttpError(504, "request deadline exceeded")
        else:
            raise HttpError(404, "not found")
    except HttpError as e:
        await send(writer, e.status, {"error": e.message}, svc.cfg)
    except (ValueError, UnicodeError, RecursionError, asyncio.LimitOverrunError, asyncio.IncompleteReadError):
        await send(writer, 400, {"error": "malformed request"}, svc.cfg)
    except TimeoutError:
        # A response write timed out; do not append a second HTTP response.
        svc.metrics["http_write_timeouts_total"] += 1
    finally:
        if disconnected:
            disconnected.cancel()
        if rid:
            svc.release(rid)
        writer.close()
        svc.clients -= 1


async def main():
    cfg = Config()
    svc = Service(cfg)
    await svc.dht.start()
    connections = set()

    def accepted(reader, writer):
        if svc.clients >= cfg.MAX_HTTP:
            svc.metrics["http_rejected_total"] += 1
            writer.write(b"HTTP/1.1 503 Overloaded\r\nConnection: close\r\nRetry-After: 2\r\nContent-Length: 0\r\n\r\n")
            writer.close()
            return
        svc.clients += 1
        task = asyncio.create_task(serve(reader, writer, svc))
        connections.add(task)
        def finished(task):
            connections.discard(task)
            if not task.cancelled() and task.exception():
                if not isinstance(task.exception(), (ConnectionError, TimeoutError)):
                    print(f"HTTP handler failed: {type(task.exception()).__name__}", file=sys.stderr)
        task.add_done_callback(finished)

    server = await asyncio.start_server(accepted, cfg.host, cfg.LISTEN_PORT,
                                       limit=cfg.MAX_HEADER, backlog=cfg.MAX_HTTP)
    stop = asyncio.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        asyncio.get_running_loop().add_signal_handler(sig, stop.set)
    driver = asyncio.create_task(svc.driver())
    stopped = asyncio.create_task(stop.wait())
    print(f"swarm-health listening on {cfg.host}:{cfg.LISTEN_PORT}", file=sys.stderr, flush=True)
    await asyncio.wait((driver, stopped), return_when=asyncio.FIRST_COMPLETED)
    svc.closing = True
    server.close()
    await server.wait_closed()
    for task in list(connections):
        task.cancel()
    await asyncio.gather(*connections, return_exceptions=True)
    try:
        await driver
        reply = await svc.dht.call({"op": "state"})
        if reply.get("state"):
            svc.state.save(reply["state"])
        await svc.dht.call({"op": "close"})
    finally:
        stopped.cancel()
        if svc.dht.proc.returncode is None:
            svc.dht.proc.terminate()
        await asyncio.wait_for(svc.dht.proc.wait(), 5)
        svc.state.lock.close()


if __name__ == "__main__":
    asyncio.run(main())

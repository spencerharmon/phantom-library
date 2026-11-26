#!/usr/bin/env python3
"""Swarm-connectivity A/B probe (integration scenario 51).

Measures how well a BitTorrent client reaches the swarms behind phantom-library's
real sources, and how much an inbound-reachable listen port helps, by running TWO
libtorrent sessions side by side on the SAME sample of infohashes:

  arm=reachable  listens on PROBE_REACHABLE_PORT (the deployment forwards it from
                 the internet to this pod; the probe does not assume it — inbound
                 peer counts are the proof)
  arm=control    listens on an ephemeral, unforwarded port (outbound-only, the
                 situation gostream was in before its port was published)

Infohashes are sampled from gostream's own library (POST /torrents {"action":
"list"}), skipping torrents gostream currently has active so the probe never
competes with a user's stream. Magnets are bare infohashes, so peers are found via
DHT/PEX only — no tracker or indexer involved. Torrents are added in upload mode:
metadata is fetched, no payload is downloaded.

Per arm and run it records: metadata success ratio, time to first peer / metadata
(p50, p90), peak connected peers and peak inbound peers per torrent, the number of
swarm peers each arm discovered, and the session's DHT node count. (Upload-mode
torrents drop their peers once metadata is in, so peaks, not end-of-window counts,
are the meaningful connection measure.) Results are pushed to a Prometheus Pushgateway
(metrics prefixed phantom_swarm_probe_) and printed as JSON.

Configuration (env): GOSTREAM_API (required, e.g. http://gostream:8090),
PROBE_SAMPLE (12), PROBE_WINDOW_S (60), PROBE_DHT_WARMUP_S (60),
PROBE_REACHABLE_PORT (required), PHANTOM_PUSHGATEWAY_URL (optional; omitted = no
push), PROBE_PUSH_JOB (phantom-swarm-probe), PROBE_SEED (random sample seed).

Exit code: 0 on a completed measurement (even if swarms are dead — that is data),
1 if the probe could not measure (gostream unreachable, empty sample, no DHT).
Run `--selftest` to check the aggregation/exposition code without network.
"""
import json
import os
import warnings
import random
import shutil
import statistics
import sys
import tempfile
import time
import urllib.request

ARMS = ("reachable", "control")


def env_int(name, default):
    v = os.environ.get(name, "")
    return int(v) if v else default


def log(msg):
    print(f"[swarm-probe] {msg}", file=sys.stderr, flush=True)


def gostream_sample(api, n, rng):
    req = urllib.request.Request(api.rstrip("/") + "/torrents", data=b'{"action":"list"}',
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=60) as r:
        items = json.load(r)
    idle = sorted({t["hash"].lower() for t in items
                   if t.get("hash") and len(t["hash"]) == 40 and t.get("stat_string") == "Torrent in db"})
    log(f"gostream library: {len(items)} torrents, {len(idle)} idle candidates")
    return rng.sample(idle, min(n, len(idle)))


def quantile(values, q):
    if not values:
        return None
    s = sorted(values)
    idx = min(len(s) - 1, max(0, round(q * (len(s) - 1))))
    return s[idx]


def summarize(records, window_s):
    """records: per-torrent dicts for one arm -> aggregate metrics dict."""
    n = len(records)
    meta = [r["metadata_s"] for r in records if r["metadata_s"] is not None]
    first = [r["first_peer_s"] for r in records if r["first_peer_s"] is not None]
    peers = [r["max_peers"] for r in records]
    known = [r["known_peers"] for r in records]
    out = {
        "torrents": n,
        "metadata_success_ratio": (len(meta) / n) if n else 0.0,
        "peer_found_ratio": (len(first) / n) if n else 0.0,
        "peers_sum": sum(peers),
        "seeds_sum": sum(r["max_seeds"] for r in records),
        "inbound_peers_sum": sum(r["max_inbound"] for r in records),
        "known_peers_sum": sum(known),
        "window_seconds": window_s,
    }
    for q in (0.5, 0.9):
        tag = f"p{int(q * 100)}"
        out[f"metadata_seconds_{tag}"] = quantile(meta, q)
        out[f"first_peer_seconds_{tag}"] = quantile(first, q)
        out[f"peers_{tag}"] = quantile(peers, q)
        out[f"known_peers_{tag}"] = quantile(known, q)
    return out


def exposition(summary, dht_nodes, run_ts, duration_s):
    """Prometheus text exposition for a Pushgateway PUT."""
    lines = []

    def g(name, help_, samples):
        lines.append(f"# HELP phantom_swarm_probe_{name} {help_}")
        lines.append(f"# TYPE phantom_swarm_probe_{name} gauge")
        for labels, v in samples:
            if v is None:
                continue
            lab = ",".join(f'{k}="{val}"' for k, val in labels.items())
            lines.append(f"phantom_swarm_probe_{name}{{{lab}}} {float(v)}")

    def per_arm(key):
        return [({"arm": a}, summary[a][key]) for a in ARMS]

    def per_arm_q(key):
        return [({"arm": a, "quantile": q}, summary[a][f"{key}_p{int(float(q) * 100)}"])
                for a in ARMS for q in ("0.5", "0.9")]

    g("torrents", "Infohashes probed in the run, per arm.", per_arm("torrents"))
    g("metadata_success_ratio", "Fraction of probed torrents whose metadata was fetched within the window.",
      per_arm("metadata_success_ratio"))
    g("peer_found_ratio", "Fraction of probed torrents with at least one connected peer within the window.",
      per_arm("peer_found_ratio"))
    g("metadata_seconds", "Time to metadata over torrents that succeeded.", per_arm_q("metadata_seconds"))
    g("first_peer_seconds", "Time to first connected peer over torrents that found one.",
      per_arm_q("first_peer_seconds"))
    g("peers", "Peak connected peers per torrent during the window.", per_arm_q("peers"))
    g("peers_sum", "Peak connected peers per torrent, summed over probed torrents.", per_arm("peers_sum"))
    g("seeds_sum", "Peak connected seeds per torrent, summed over probed torrents.", per_arm("seeds_sum"))
    g("inbound_peers_sum", "Peak peers that connected IN to the arm per torrent, summed over probed torrents.",
      per_arm("inbound_peers_sum"))
    g("known_peers", "Swarm peers the arm discovered per torrent (DHT/PEX candidates).", per_arm_q("known_peers"))
    g("known_peers_sum", "Swarm peers discovered, summed over probed torrents.", per_arm("known_peers_sum"))
    g("dht_nodes", "DHT routing-table nodes of the arm's session at the end of the run.",
      [({"arm": a}, dht_nodes.get(a)) for a in ARMS])
    g("window_seconds", "Per-torrent observation window.", [({}, summary[ARMS[0]]["window_seconds"])])
    g("run_duration_seconds", "Wall time of the probe run.", [({}, duration_s)])
    g("last_run_timestamp_seconds", "Unix time the run finished.", [({}, run_ts)])
    return "\n".join(lines) + "\n"


def push(url, job, body):
    target = url.rstrip("/") + f"/metrics/job/{job}"
    req = urllib.request.Request(target, data=body.encode(), method="PUT",
                                 headers={"Content-Type": "text/plain; version=0.0.4"})
    with urllib.request.urlopen(req, timeout=30) as r:
        if r.status not in (200, 202):
            raise RuntimeError(f"pushgateway {target}: HTTP {r.status}")
    log(f"pushed to {target}")


def make_session(lt, port):
    s = lt.session({
        "listen_interfaces": f"0.0.0.0:{port},[::]:{port}",
        "enable_dht": True, "enable_lsd": False, "enable_upnp": False, "enable_natpmp": False,
        "dht_bootstrap_nodes": "router.bittorrent.com:6881,dht.transmissionbt.com:6881,"
                               "dht.libtorrent.org:25401,router.utorrent.com:6881",
        "alert_mask": 0, "connections_limit": 400,
    })
    return s


def dht_nodes(sess):
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", DeprecationWarning)
            return int(sess.status().dht_nodes)
    except Exception:
        return 0


def probe(lt, hashes, window_s, warmup_s, reachable_port, workdir):
    sessions = {"reachable": make_session(lt, reachable_port), "control": make_session(lt, 0)}
    for a, s in sessions.items():
        log(f"arm={a} listening on port {s.listen_port()}")
    t0 = time.time()
    while time.time() - t0 < warmup_s and min(dht_nodes(s) for s in sessions.values()) < 100:
        time.sleep(2)
    log("dht warmup: " + ", ".join(f"{a}={dht_nodes(s)}" for a, s in sessions.items()))
    if max(dht_nodes(s) for s in sessions.values()) == 0:
        raise RuntimeError("no DHT nodes after warmup; cannot measure")

    handles = {a: {} for a in ARMS}
    recs = {a: {} for a in ARMS}
    start = time.time()
    for h in hashes:
        for a, s in sessions.items():
            p = lt.parse_magnet_uri(f"magnet:?xt=urn:btih:{h}")
            p.save_path = os.path.join(workdir, a)
            p.flags |= lt.torrent_flags.upload_mode
            handles[a][h] = s.add_torrent(p)
            recs[a][h] = {"metadata_s": None, "first_peer_s": None, "max_peers": 0, "max_seeds": 0,
                          "max_inbound": 0, "known_peers": 0}
    while time.time() - start < window_s:
        now = time.time() - start
        for a in ARMS:
            for h, th in handles[a].items():
                st = th.status()
                r = recs[a][h]
                if st.num_peers > 0 and r["first_peer_s"] is None:
                    r["first_peer_s"] = now
                if st.has_metadata and r["metadata_s"] is None:
                    r["metadata_s"] = now
                r["max_peers"] = max(r["max_peers"], st.num_peers)
                r["max_seeds"] = max(r["max_seeds"], st.num_seeds)
                r["known_peers"] = max(r["known_peers"], st.list_peers)
                if st.num_peers:
                    inbound = sum(1 for p in th.get_peer_info()
                                  if not (p.flags & lt.peer_info.local_connection))
                    r["max_inbound"] = max(r["max_inbound"], inbound)
        time.sleep(1)
    nodes = {a: dht_nodes(s) for a, s in sessions.items()}
    for a, s in sessions.items():
        for th in handles[a].values():
            s.remove_torrent(th)
    return recs, nodes


def selftest():
    recs = [{"metadata_s": 3.0, "first_peer_s": 1.0, "max_peers": 5, "max_seeds": 2, "max_inbound": 1,
             "known_peers": 40},
            {"metadata_s": None, "first_peer_s": None, "max_peers": 0, "max_seeds": 0, "max_inbound": 0,
             "known_peers": 0},
            {"metadata_s": 9.0, "first_peer_s": 4.0, "max_peers": 3, "max_seeds": 3, "max_inbound": 0,
             "known_peers": 10}]
    s = summarize(recs, 60)
    assert s["torrents"] == 3 and abs(s["metadata_success_ratio"] - 2 / 3) < 1e-9, s
    assert s["peer_found_ratio"] == 2 / 3 and s["inbound_peers_sum"] == 1 and s["peers_sum"] == 8, s
    assert s["known_peers_sum"] == 50 and s["known_peers_p90"] == 40, s
    assert s["metadata_seconds_p50"] in (3.0, 9.0) and s["first_peer_seconds_p90"] == 4.0, s
    empty = summarize([], 60)
    assert empty["metadata_success_ratio"] == 0.0 and empty["metadata_seconds_p50"] is None
    body = exposition({"reachable": s, "control": empty}, {"reachable": 300, "control": 10}, 1700000000, 75.5)
    assert 'phantom_swarm_probe_inbound_peers_sum{arm="reachable"} 1.0' in body, body
    assert 'phantom_swarm_probe_metadata_seconds{arm="control",quantile="0.5"}' not in body, body
    assert 'phantom_swarm_probe_peers{arm="reachable",quantile="0.9"}' in body, body
    assert quantile([5, 1, 3], 0.5) == 3 and quantile([], 0.5) is None
    print("selftest ok")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return 0
    api = os.environ.get("GOSTREAM_API", "")
    port = env_int("PROBE_REACHABLE_PORT", 0)
    if not api or not port:
        log("GOSTREAM_API and PROBE_REACHABLE_PORT are required")
        return 1
    n = env_int("PROBE_SAMPLE", 12)
    window_s = env_int("PROBE_WINDOW_S", 60)
    warmup_s = env_int("PROBE_DHT_WARMUP_S", 60)
    rng = random.Random(os.environ.get("PROBE_SEED") or None)
    import libtorrent as lt

    run_start = time.time()
    hashes = gostream_sample(api, n, rng)
    if not hashes:
        log("no idle torrents to sample")
        return 1
    workdir = tempfile.mkdtemp(prefix="swarm-probe-")
    try:
        recs, nodes = probe(lt, hashes, window_s, warmup_s, port, workdir)
    finally:
        shutil.rmtree(workdir, ignore_errors=True)
    summary = {a: summarize(list(recs[a].values()), window_s) for a in ARMS}
    duration = time.time() - run_start
    print(json.dumps({"summary": summary, "dht_nodes": nodes, "hashes": hashes,
                      "per_torrent": recs}, indent=2, default=str))
    body = exposition(summary, nodes, time.time(), duration)
    url = os.environ.get("PHANTOM_PUSHGATEWAY_URL", "")
    if url:
        push(url, os.environ.get("PROBE_PUSH_JOB", "phantom-swarm-probe"), body)
    return 0


if __name__ == "__main__":
    sys.exit(main())

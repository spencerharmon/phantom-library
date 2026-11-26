#!/usr/bin/env python3
"""Controlled UDP fixture + HTTP client, reused from service-contract tests.

Runs in an offline fixture container. The service is a separate, unmodified
container using its production ENTRYPOINT and a persistent named volume.
"""
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import time
from swarm_health_integration import Cluster, Process, eventually, info


class Client(Process):
    def __init__(self):
        self.port = 8095

    def ready(self):
        try:
            code, body, _ = self.http("GET", "/healthz")
            return body if code == 200 else None
        except OSError:
            return None


if sys.argv[1] == "peers":
    cluster = Cluster()
    cluster.data[info(1)] = {"seeds": ["192.0.2.1"], "peers": ["192.0.2.2"],
                             "values": [("192.0.2.1", 6881), ("192.0.2.2", 6881)]}
    cluster.data[info(2)] = {"silent": True}
    Path("/tmp/bootstrap").write_text("%s:%s" % cluster.peers[3].address)
    stop = False
    def shutdown(*_):
        global stop
        stop = True
    signal.signal(signal.SIGTERM, shutdown)
    while not stop:
        time.sleep(.05)
    cluster.close()
elif sys.argv[1] == "probe":
    client = Client()
    health = eventually(client.ready, 15)
    assert health["dht_nodes"] >= 1 and len(health["node_id"]) == 40, health
    old = Path("/tmp/node-id")
    if sys.argv[2] == "restart":
        assert old.read_text() == health["node_id"], health
        assert client.metrics()["state_loads_total"] == 1
        assert client.metrics()["cache_entries"] == 0
    else:
        old.write_text(health["node_id"])
    code, body, _ = client.measure([info(1)])
    assert code == 200, (code, body)
    row = body["results"][0]
    assert (row["status"], row["dht_seeders"], row["dht_peers"]) == ("ok", 1, 2), row
    assert row["queries"] == row["replies"] == 3, row
    assert abs(row["checked_at"] - time.time()) < 5, row
    code, body, _ = client.http("POST", "/measure", {"info_hashes": [info(2)]})
    assert code == 200 and body["results"] == [], body
    assert body["observations"][0]["dht_peers"] is None, body
    assert body["observations"][0]["status"] == "timeout", body
    assert client.metrics()["engine_errors_total"] == 0
    print("PASS: production ENTRYPOINT HTTP + controlled UDP, sampled positive and unknown; " + sys.argv[2])
elif sys.argv[1] == "inventory":
    assert os.getuid() == os.getgid() == 10001
    provenance = Path("/usr/share/swarm-health")
    subprocess.run(["sha256sum", "-c", str(provenance / "files.sha256")], check=True, stdout=sys.stderr)
    expected = json.loads(sys.argv[2])
    for path, digest in expected.items():
        assert hashlib.sha256(Path(path).read_bytes()).hexdigest() == digest, "wrong source: " + path
    assert (provenance / "source-revision").read_text().strip() == sys.argv[3], "wrong source commit"
    assert subprocess.check_output(["dpkg-query", "-W", "-f=${Version}", "libtorrent-rasterbar2.0t64"], text=True) == "2.0.11-1"
    for path in ("/usr/bin/git", "/usr/bin/apt-get", "/usr/bin/pip", "/usr/bin/c++"):
        assert not Path(path).exists(), "unexpected runtime tool: " + path
    print(json.dumps({"source": sys.argv[3], "uid": os.getuid(), "python": sys.version,
                      "files": (provenance / "files.sha256").read_text(),
                      "runtime_packages": (provenance / "runtime-packages.tsv").read_text(),
                      "build_packages": (provenance / "build-packages.tsv").read_text()}))
elif sys.argv[1] == "state":
    state = Path("/var/lib/swarm-health/dht-state.json")
    assert state.stat().st_uid == 10001 and state.stat().st_mode & 0o777 == 0o600
    saved = json.loads(state.read_text())
    assert saved["version"] == 1
    assert hashlib.sha256(bytes.fromhex(saved["state"])).hexdigest() == saved["sha256"]
    assert not state.with_suffix(".tmp").exists()
    print("PASS: mounted state is UID 10001 mode 0600, atomic and checksummed")
else:
    raise ValueError(sys.argv[1])

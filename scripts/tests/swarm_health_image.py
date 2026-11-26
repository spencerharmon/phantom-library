#!/usr/bin/env python3
"""Build/pull the real locked image; test offline, unmodified ENTRYPOINT + restart.

Only the remote UDP peers are controlled. No host service/dependency mounts.
Works with a local Podman or a remote Docker daemon (copy, not host bind mounts).
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
os.chdir(ROOT)
ENGINE = os.environ.get("CONTAINER_ENGINE", "podman")
GIT = ["beehive", "submodule", "git"] if os.environ.get("SUBMODULE_WORKTREE") else ["git"]


def output(args):
    return subprocess.check_output(args, text=True).strip()


def engine(*args):
    return output([ENGINE, *args])


def run(*args):
    subprocess.run([ENGINE, *args], check=True)


revision = os.environ.get("SWARM_HEALTH_SOURCE_REVISION") or output(GIT + ["rev-parse", "HEAD"])
assert re.fullmatch("[0-9a-f]{40}", revision), "a full source commit is required"
# Don't label dirty or unrelated source with an arbitrary clean commit.
paths = [p for p in (ROOT / "tools/swarm-health").iterdir() if p.is_file() and p.name != "README.md" and p.name != "Dockerfile.test"]
for path in paths:
    committed = subprocess.check_output(GIT + ["show", revision + ":" + str(path.relative_to(ROOT))])
    assert committed == path.read_bytes(), "source differs from revision: " + str(path)
image = os.environ.get("SWARM_HEALTH_IMAGE")
if image:
    assert re.fullmatch(r"[^\s@]+@sha256:[0-9a-f]{64}", image), "pull test requires immutable digest"
    run("pull", "--platform", "linux/amd64", image)
else:
    image = os.environ.get("SWARM_HEALTH_TAG", "localhost/swarm-health:" + revision)
    epoch = output(GIT + ["show", "-s", "--format=%ct", revision])
    run("build", "--platform", "linux/amd64", "--build-arg", "SOURCE_REVISION=" + revision,
        "--build-arg", "SOURCE_DATE_EPOCH=" + epoch, "-t", image, "tools/swarm-health")
inspection = json.loads(engine("image", "inspect", image))[0]
config = inspection["Config"]
assert config["User"] == "10001:10001", "root/wrong runtime UID"
assert config["Entrypoint"] == ["python3", "/app/swarm_health_service.py"], "runtime downloader/wrong entrypoint"
assert not config.get("Cmd"), "unexpected runtime command"
assert config["Labels"]["org.opencontainers.image.revision"] == revision, "wrong source label"
assert config.get("StopSignal") in ("SIGTERM", "15"), "missing clean termination signal"
assert {"8095/tcp", "6881/udp"} <= config["ExposedPorts"].keys()
assert "/var/lib/swarm-health" in config["Volumes"]
assert inspection["Architecture"] == "amd64" and inspection["Os"] == "linux"
# Pin all subsequent runs to the actual local image ID, never a movable tag.
image_id = inspection["Id"]
expected = {"/app/swarm_health_service.py": hashlib.sha256((ROOT / "tools/swarm-health/swarm_health_service.py").read_bytes()).hexdigest()}
for name in ("Dockerfile", "build-bridge.sh", "dht_bridge.cpp", "lock-packages.sh", "build-packages.sha256", "runtime-packages.sha256"):
    expected["/usr/share/swarm-health/" + name] = hashlib.sha256((ROOT / "tools/swarm-health" / name).read_bytes()).hexdigest()
prefix = "swarm-image-" + uuid.uuid4().hex[:12]
fixture, service, volume = prefix + "-fixture", prefix + "-service", prefix + "-state"
containers = []
volume_created = False
try:
    run("volume", "create", volume)
    volume_created = True
    run("create", "--name", fixture, "--network", "none",
        "--mount", "type=volume,source=" + volume + ",target=/var/lib/swarm-health,readonly",
        "--entrypoint", "python3", image_id, "/fixture/swarm_health_image_fixture.py", "peers")
    containers.append(fixture)
    with tempfile.TemporaryDirectory() as directory:
        for name in ("swarm_health_image_fixture.py", "swarm_health_integration.py"):
            shutil.copy(ROOT / "scripts/tests" / name, directory)
        run("cp", directory + "/.", fixture + ":/fixture")
    run("start", fixture)
    for _ in range(100):
        probe = subprocess.run([ENGINE, "exec", fixture, "cat", "/tmp/bootstrap"], capture_output=True, text=True)
        if probe.returncode == 0:
            bootstrap = probe.stdout.strip()
            break
        time.sleep(.1)
    else:
        raise AssertionError("controlled UDP fixture failed to start")
    inventory = json.loads(engine("exec", fixture, "python3", "/fixture/swarm_health_image_fixture.py", "inventory", json.dumps(expected), revision))
    for phase in ("cold-start", "restart"):
        args = ["run", "-d", "--name", service, "--network", "container:" + fixture,
                "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                "--mount", "type=volume,source=" + volume + ",target=/var/lib/swarm-health"]
        settings = dict(DHT_LISTEN_HOST="127.0.0.1" if phase == "cold-start" else "127.0.0.8",
                        DHT_PORT="6881", DHT_ALLOW_PRIVATE="1", DHT_BOOTSTRAP=bootstrap if phase == "cold-start" else "",
                        MIN_DHT_NODES="1", MAX_NODES="8", MAX_QUERIES="6", QUERY_TIMEOUT_S="0.5",
                        MEASURE_TIMEOUT_S="2", STATE_SAVE_S="600")
        for key, value in settings.items():
            args += ["-e", key + "=" + value]
        run(*args, image_id)
        containers.append(service)
        run("exec", fixture, "python3", "/fixture/swarm_health_image_fixture.py", "probe", phase)
        # A long save interval ensures this checks the actual SIGTERM final save.
        run("stop", "--time", "8", service)
        result = json.loads(engine("inspect", service))[0]
        assert result["State"]["ExitCode"] == 0, result["State"]
        logs = subprocess.check_output([ENGINE, "logs", service], text=True, stderr=subprocess.STDOUT)
        assert "libtorrent=2.0.11" in logs, logs
        print("PASS: SIGTERM exit=0 within 8s, native libtorrent=2.0.11; " + phase)
        # Fixture has the same volume read-only; inspection is also UID 10001.
        run("exec", fixture, "python3", "/fixture/swarm_health_image_fixture.py", "state")
        run("rm", service)
        containers.remove(service)
    # Read-only state must fail closed instead of running root or falling back to ephemeral state.
    denied = subprocess.run([ENGINE, "run", "--rm", "--network", "none", "--read-only",
                             "--mount", "type=volume,source=" + volume + ",target=/var/lib/swarm-health,readonly",
                             "-e", "DHT_LISTEN_HOST=127.0.0.1", image_id], capture_output=True, text=True, timeout=15)
    assert denied.returncode != 0 and ("Read-only file system" in denied.stderr or "Permission denied" in denied.stderr), denied
    print("PASS: unwritable mounted state fails closed")
    evidence = dict(image=image, image_id=image_id, platform="linux/amd64", source_revision=revision,
                    base=config["Labels"]["org.opencontainers.image.base.digest"], inventory=inventory)
    if os.environ.get("SWARM_HEALTH_EVIDENCE"):
        Path(os.environ["SWARM_HEALTH_EVIDENCE"]).write_text(json.dumps(evidence, indent=2) + "\n")
    print(json.dumps(evidence, sort_keys=True))
    print("ALL PASS: locked swarm-health image, offline HTTP/UDP, persistent restart, source and dependency provenance")
finally:
    for name in reversed(containers):
        # Disposable test containers only. Log diagnostics before cleanup on failure.
        subprocess.run([ENGINE, "logs", name], check=False)
        subprocess.run([ENGINE, "rm", "-f", "-v", name], check=True)
    if volume_created:
        run("volume", "rm", volume)

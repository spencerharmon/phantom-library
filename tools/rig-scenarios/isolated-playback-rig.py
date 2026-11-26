#!/usr/bin/env python3
"""One-shot synthetic playback rig. Owns every process, DB and directory it creates."""
import argparse
import hashlib
import json
import os
import platform
import re
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[2]
TOOLS = REPO / "tools/rig-scenarios"
PLUGIN = "Jellyfin.Plugin.PhantomLibrary"
PLUGIN_ID = "9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b"
# REBASE.md's patch base. Build from the public source, never a production image.
SERVER_TAG = "v10.11.9"


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def run(cmd, **kwargs):
    return subprocess.run([str(x) for x in cmd], check=True, **kwargs)


def stop(process):
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=10)


def start(cmd, log, env):
    with log.open("w") as stream:
        return subprocess.Popen([str(x) for x in cmd], stdout=stream, stderr=subprocess.STDOUT,
                                env=env, start_new_session=True)


def request(base, path, body=None, token=""):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, headers={"Content-Type": "application/json", "X-Emby-Token": token,
        "X-Emby-Authorization": 'MediaBrowser Client="phantom-rig", Device="phantom-rig", DeviceId="synthetic-seed", Version="1"'})
    with urllib.request.urlopen(req, timeout=5) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


def wait_http(process, base, path, token=""):
    for _ in range(180):
        if process.poll() is not None:
            raise RuntimeError(f"owned process exited ({process.returncode}); see logs")
        try:
            return request(base, path, token=token)
        except (OSError, urllib.error.URLError):
            time.sleep(1)
    raise TimeoutError(f"service never ready: {base}{path}")


def ffmpeg(root):
    supplied = os.environ.get("PHANTOM_PARITY_FFMPEG")
    receipt = {}
    if supplied:
        binary = Path(supplied).resolve()
    else:
        arch = {"x86_64": "linux64", "aarch64": "linuxarm64"}[platform.machine()]
        archive = root / "ffmpeg.tar.xz"
        url = f"https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v7.1.3-1/jellyfin-ffmpeg_7.1.3-1_portable_{arch}-gpl.tar.xz"
        urllib.request.urlretrieve(url, archive)
        dest = root / "ffmpeg"
        dest.mkdir()
        with tarfile.open(archive) as tar:
            tar.extractall(dest, filter="data")
        binary = next(dest.rglob("ffmpeg"))
        receipt.update(url=url, archive_sha256=sha(archive))
        archive.unlink()
    receipt.update(binary_sha256=sha(binary), ffprobe_sha256=sha(binary.with_name("ffprobe")),
                   version=run([binary, "-version"], capture_output=True, text=True).stdout)
    (root / "ffmpeg-identity.json").write_text(json.dumps(receipt, indent=2))
    return str(binary)


def build(root, env):
    server = os.environ.get("PHANTOM_PARITY_SERVER")
    candidate = os.environ.get("PHANTOM_PARITY_PLUGIN")
    if server and candidate:
        return Path(server).resolve(), Path(candidate).resolve()
    tree = root / "build"
    tree.mkdir()
    for name in ("src", "Directory.Build.props", "NuGet.config"):
        src, dest = REPO / name, tree / name
        if src.is_dir():
            shutil.copytree(src, dest, ignore=shutil.ignore_patterns("bin", "obj"))
        else:
            shutil.copy2(src, dest)
    archive = root / "jellyfin-source.tar.gz"
    urllib.request.urlretrieve(f"https://codeload.github.com/jellyfin/jellyfin/tar.gz/refs/tags/{SERVER_TAG}", archive)
    with tarfile.open(archive) as tar:
        tar.extractall(tree, filter="data")
    (tree / "jellyfin-10.11.9").rename(tree / "jellyfin")
    patches = sorted((REPO / "scripts/jellyfin-patches").glob("*.patch"))
    with (root / "build.log").open("w") as log:
        for patch in patches:
            run(["patch", "--batch", "--forward", "-p1", "-i", patch], cwd=tree / "jellyfin", stdout=log, stderr=subprocess.STDOUT)
        flags = ["-c", "Release", "-m:2", "-p:UseSharedCompilation=false", "-nodeReuse:false"]
        for project in ("jellyfin/Jellyfin.Server/Jellyfin.Server.csproj", f"src/{PLUGIN}/{PLUGIN}.csproj"):
            proc = start(["dotnet", "build", tree / project, *flags, "-p:CopyLocalLockFileAssemblies=true"], root / (Path(project).stem + "-build.log"), env)
            try:
                if proc.wait(timeout=1200) != 0:
                    raise RuntimeError(f"build failed: {project}")
            finally:
                stop(proc)
    (root / "build-identity.json").write_text(json.dumps({"server_tag": SERVER_TAG,
        "source_sha256": sha(archive), "patches": {p.name: sha(p) for p in patches}}, indent=2))
    return (Path(server).resolve() if server else tree / "jellyfin/Jellyfin.Server/bin/Release/net9.0"), (Path(candidate).resolve() if candidate else tree / f"src/{PLUGIN}/bin/Release/net9.0")


class Postgres:
    def __init__(self, root, env):
        self.root, self.env = root, env
        self.container = None
        self.runtime = os.environ.get("PHANTOM_PARITY_CONTAINER_RUNTIME", "podman")
        if self.runtime not in ("podman", "docker"):
            raise ValueError("container runtime must be podman or docker")
        self.database = "parity_" + uuid.uuid4().hex
        self.created = False
        self.command = []

    def sql(self, text, database="postgres"):
        return run(self.command + ["-d", database, "-X", "-qAt", "-v", "ON_ERROR_STOP=1"],
                   input=text, text=True, capture_output=True, env=self.env).stdout.strip()

    def open(self):
        host = os.environ.get("PHANTOM_PARITY_PGHOST")
        if host:
            # Explicit test-only service (Gitea service container). No defaults
            # from ambient PG*/PHANTOM_POSTGRES_* or any production DSN.
            self.env.update(PGHOST=host, PGPORT=os.environ.get("PHANTOM_PARITY_PGPORT", "5432"),
                            PGUSER=os.environ.get("PHANTOM_PARITY_PGUSER", "postgres"),
                            PGPASSWORD=os.environ["PHANTOM_PARITY_PGPASSWORD"])
            self.command = ["psql"]
        else:
            image = os.environ.get("PHANTOM_PARITY_POSTGRES_IMAGE", "docker.io/library/postgres:16-alpine")
            password = uuid.uuid4().hex
            self.container = run([self.runtime, "run", "-d", "--name", self.database,
                "-e", "POSTGRES_PASSWORD", "-p", "127.0.0.1::5432", image],
                env={**self.env, "POSTGRES_PASSWORD": password}, capture_output=True, text=True).stdout.strip()
            mapping = run([self.runtime, "port", self.container, "5432/tcp"], capture_output=True, text=True).stdout.strip()
            self.env.update(PGHOST="127.0.0.1", PGPORT=mapping.rsplit(":", 1)[1], PGUSER="postgres", PGPASSWORD=password)
            self.command = [self.runtime, "exec", "-i", self.container, "psql", "-U", "postgres"]
            identity = run([self.runtime, "inspect", self.container, "--format", "{{.Image}}"], capture_output=True, text=True).stdout.strip()
            (self.root / "postgres-image.txt").write_text(image + "\n" + identity + "\n")
        self.env["PGCONNECT_TIMEOUT"] = "5"
        last_error = ""
        for _ in range(60):
            try:
                self.sql("SELECT 1;")
                break
            except subprocess.CalledProcessError as error:
                last_error = error.stderr
                time.sleep(1)
        else:
            (self.root / "postgres-connect-error.txt").write_text(last_error)
            raise RuntimeError("PostgreSQL test service not ready: " + last_error)
        self.sql(f"CREATE DATABASE {self.database};")
        self.created = True
        self.env.update(PHANTOM_POSTGRES_HOST=self.env["PGHOST"], PHANTOM_POSTGRES_PORT=self.env["PGPORT"],
                        PHANTOM_POSTGRES_USER=self.env["PGUSER"], PHANTOM_POSTGRES_PASSWORD=self.env["PGPASSWORD"],
                        PHANTOM_POSTGRES_DB=self.database,
                        PHANTOM_RIG_PSQL=json.dumps(self.command + ["-d", self.database]))
        receipt = {"host": self.env["PGHOST"], "port": self.env["PGPORT"], "user": self.env["PGUSER"],
                   "database": self.sql("SELECT current_database();", self.database),
                   "version": self.sql("SELECT version();")}
        (self.root / "postgres-identity.json").write_text(json.dumps(receipt, indent=2) + "\n")
        print("POSTGRES", receipt, flush=True)

    def close(self):
        if self.container:
            try:
                with (self.root / "postgres.log").open("w") as log:
                    run([self.runtime, "logs", self.container], stdout=log, stderr=subprocess.STDOUT)
            finally:
                run([self.runtime, "rm", "-f", self.container], capture_output=True)
        elif self.created:
            self.sql(f"DROP DATABASE {self.database} WITH (FORCE);")


def assert_worker_configuration(log, observed):
    """Fail closed on startup defaults, not just the later API configuration.

    Only warmup logs its startup setting on the tracked plugin. Eviction and
    autopilot read the same Plugin.Configuration dynamically and have no startup
    log; require their post-restart readback too (without a second API write).
    """
    flags = ("ChannelWarmupEnabled", "EvictionEnabled", "SeriesAutopilotEnabled")
    for key in flags:
        assert observed[key] is False, (key, observed[key])
    lines = [line for line in log.splitlines() if "Channel BaseItem warmup worker started" in line]
    assert len(lines) == 1 and re.search(r"\benabled=False\b", lines[0]), f"warmup boot configuration not disabled: {lines}"
    return {"warmup_startup_log": lines[0], "loaded_flags": {key: observed[key] for key in flags}}


def restart_configured_server(command, processes, root, env, config):
    # Bootstrap may deserialize defaults before the plugin/API is fully ready.
    # Persist via Jellyfin's serializer, then restart OUR server so no worker can
    # retain the bootstrap configuration. The boot log is separate evidence.
    stop(processes[-1])
    log = root / "logs/jellyfin-console.log"
    processes.append(start(command, log, env))
    # /System/Info/Public is also served by the temporary setup host, BEFORE
    # plugin workers start. An authenticated plugin response is the real gate.
    observed = wait_http(processes[-1], env["PHANTOM_RIG_API"],
                         f"/Plugins/{PLUGIN_ID}/Configuration", token=env["PHANTOM_RIG_TOKEN"])
    for key, expected in config.items():
        assert observed[key] == expected, (key, observed[key], expected)
    receipt = assert_worker_configuration(log.read_text(), observed)
    (root / "worker-configuration.json").write_text(json.dumps(receipt, indent=2))
    (root / "loaded-fixture-config.json").write_text(json.dumps({key: observed[key] for key in config}, indent=2))
    print("WORKER_CONFIG", receipt, flush=True)


def build_availability_control(root, server, candidate, env):
    tree = root / "availability-control"
    shutil.copytree(TOOLS / "AvailabilityRigControl", tree,
                    ignore=shutil.ignore_patterns("bin", "obj"))
    proc = start(["dotnet", "build", tree / "AvailabilityRigControl.csproj", "-c", "Release",
                  "-p:UseSharedCompilation=false", "-nodeReuse:false",
                  f"-p:CandidateDir={candidate}", f"-p:ServerDir={server}"],
                 root / "availability-control-build.log", env)
    try:
        assert proc.wait(timeout=180) == 0, "test control build failed; see availability-control-build.log"
    finally:
        stop(proc)
    return tree / "bin/Release/net9.0/AvailabilityRigControl.dll"


def scenario(kind, root, server, candidate, backend, base_env, suite="playback", control=None):
    root.mkdir()
    env = base_env.copy()
    # Intentionally discard ambient production/storage/telemetry configuration.
    for key in list(env):
        if key.startswith(("PHANTOM_POSTGRES_", "POSTGRES_", "JELLYFIN_", "OTEL_", "PG")):
            del env[key]
    env["PHANTOM_RIG_BACKEND"] = backend
    pg = Postgres(root, env)
    processes = []
    try:
        if backend == "postgres":
            pg.open()
        for name in ("data/plugins/configurations/PhantomLibrary", "config", "cache", "logs", "responses", "tmp"):
            (root / name).mkdir(parents=True)
        jf_port, tmdb_port, go_port = port(), port(), port()
        assert len({jf_port, tmdb_port, go_port}) == 3
        env.update(PHANTOM_REPO_ROOT=str(REPO), JF_RIG_ROOT=str(root), TMDB_MOCK_PORT=str(tmdb_port), GOSTREAM_MOCK_PORT=str(go_port),
            GOSTREAM_MOCK_RESPONSE_MOVIES_ROOT="/mnt/gostream-mkv-virtual/movies",
            PHANTOM_RIG_API=f"http://127.0.0.1:{jf_port}", PHANTOM_RIG_OUTPUT=str(root / "responses"),
            PHANTOM_RIG_PHDB=str(root / "data/plugins/configurations/PhantomLibrary/phantom.db"),
            PHANTOM_RIG_JDB=str(root / "data/data/jellyfin.db"), TMPDIR=str(root / "tmp"))
        plugin_dir = root / "data/plugins" / PLUGIN
        plugin_dir.mkdir()
        assert (candidate / (PLUGIN + ".dll")).is_file(), f"candidate directory missing {PLUGIN}.dll: {candidate}"
        # Copy the selected artifact and its private dependencies; the host's
        # own assemblies must come only from the selected patched server.
        for dll in candidate.glob("*.dll"):
            if not (server / dll.name).exists():
                shutil.copy2(dll, plugin_dir / dll.name)
        assert sha(candidate / (PLUGIN + ".dll")) == sha(plugin_dir / (PLUGIN + ".dll"))
        if control:
            # Same load context as the candidate (no duplicate plugin instance).
            shutil.copy2(control, plugin_dir / control.name)
            env["PHANTOM_RIG_AVAILABILITY_CONTROL"] = "1"
        conf = ET.Element("PluginConfiguration")
        values = {"TmdbApiKey": "rig-test-key", "TmdbApiBaseUrl": f"http://127.0.0.1:{tmdb_port}/3",
            "GostreamBaseUrl": f"http://127.0.0.1:{go_port}", "GostreamDiagnosticsBaseUrl": f"http://127.0.0.1:{go_port}",
            "TorrentioBaseUrl": f"http://127.0.0.1:{tmdb_port}/torrentio", "ProwlarrBaseUrl": "", "ProwlarrApiKey": "",
            "EvictionEnabled": "false", "EagerResolveEnabled": "false", "SeriesAutopilotEnabled": "false",
            "ChannelWarmupEnabled": "false",
            "PhantomBadgeVisibility": "AlwaysShow", "SourcePickerPreset": "gostream-default",
            "PhantomStubRoot": str(root / "phantom"), "GostreamMoviesRoot": str(root / "gostream/movies"),
            "GostreamShowsRoot": str(root / "gostream/tv"), "SuggestionsCatalogueMaxItems": "10"}
        if suite == "stale-candidate":
            oracle_port = port()
            env["PHANTOM_RIG_ORACLE_PORT"] = str(oracle_port)
            values.update(AvailabilityProbeEnabled="false", AvailabilityProbeMinIntervalSeconds="3600",
                          AvailabilityProbeMaxIntervalSeconds="3600", AvailabilityMaxBatchSize="1",
                          AvailabilityYieldToUserSeconds="0", SwarmHealthServiceUrl="",
                          TorrentioBaseUrl=f"http://127.0.0.1:{oracle_port}")
        for key, value in values.items():
            ET.SubElement(conf, key).text = value
        ET.ElementTree(conf).write(root / f"data/plugins/configurations/{PLUGIN}.xml", encoding="utf-8", xml_declaration=True)
        (root / "config/network.xml").write_text(f"<NetworkConfiguration><InternalHttpPort>{jf_port}</InternalHttpPort><PublicHttpPort>{jf_port}</PublicHttpPort><AutoDiscovery>false</AutoDiscovery><EnableIPv6>false</EnableIPv6><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>")
        for script in ("tmdb-mock.py", "gostream-mock.py"):
            processes.append(start([sys.executable, TOOLS / script], root / "logs" / (script + ".log"), env))
        wait_http(processes[0], f"http://127.0.0.1:{tmdb_port}", "/3/configuration")
        # gostream generates its real media before serving. The Jellyfin browse
        # assertions below require both variants; readiness includes those files.
        for _ in range(60):
            if processes[1].poll() is not None:
                raise RuntimeError("gostream mock exited")
            if (root / "gostream/movies/Phantom_Rig_Bravo_2024_2160p_HDR_cafebabe.mkv").exists():
                break
            time.sleep(1)
        else:
            raise TimeoutError("gostream did not generate its real media fixtures")
        if suite == "stale-candidate":
            processes.append(start([sys.executable, TOOLS / "stale-candidate-oracle.py"], root / "logs/oracle.log", env))
            wait_http(processes[-1], f"http://127.0.0.1:{oracle_port}", "/health")
        server_command = ["dotnet", server / "jellyfin.dll", "--datadir", root / "data", "--configdir", root / "config",
            "--cachedir", root / "cache", "--logdir", root / "logs", "--nowebclient", "--ffmpeg", env["PHANTOM_RIG_FFMPEG"]]
        processes.append(start(server_command, root / "logs/jellyfin-bootstrap.log", env))
        wait_http(processes[-1], env["PHANTOM_RIG_API"], "/Startup/Configuration")
        info = request(env["PHANTOM_RIG_API"], "/System/Info/Public")
        (root / "initial-system-info.json").write_text(json.dumps(info, indent=2))
        assert info["StartupWizardCompleted"] is False, "synthetic seed was not empty"
        request(env["PHANTOM_RIG_API"], "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        request(env["PHANTOM_RIG_API"], "/Startup/User")  # initializes the first synthetic user
        request(env["PHANTOM_RIG_API"], "/Startup/User", {"Name": "a", "Password": "a"})
        request(env["PHANTOM_RIG_API"], "/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False})
        request(env["PHANTOM_RIG_API"], "/Startup/Complete", {})
        auth = request(env["PHANTOM_RIG_API"], "/Users/AuthenticateByName", {"Username": "a", "Pw": "a"})
        env["PHANTOM_RIG_TOKEN"] = auth["AccessToken"]
        req = urllib.request.Request(env["PHANTOM_RIG_API"] + "/Plugins", headers={"X-Emby-Token": auth["AccessToken"]})
        with urllib.request.urlopen(req, timeout=10) as response:
            plugins = json.load(response)
        loaded = next(p for p in plugins if p["Id"].replace("-", "").lower() == PLUGIN_ID.replace("-", ""))
        assert loaded["Status"] == "Active", loaded
        # Bootstrap/plugin discovery can replace a preseeded XML configuration.
        # Apply through the live API, retaining defaults/types, then read back the
        # exact loaded configuration rather than assuming deployed == loaded.
        config_path = f"/Plugins/{PLUGIN_ID}/Configuration"
        config = request(env["PHANTOM_RIG_API"], config_path, token=auth["AccessToken"])
        for key, value in values.items():
            if isinstance(config.get(key), bool):
                config[key] = value == "true"
            elif isinstance(config.get(key), int):
                config[key] = int(value)
            else:
                config[key] = value
        request(env["PHANTOM_RIG_API"], config_path, config, auth["AccessToken"])
        observed = request(env["PHANTOM_RIG_API"], config_path, token=auth["AccessToken"])
        for key in values:
            assert observed[key] == config[key], (key, observed[key], config[key])
        restart_configured_server(server_command, processes, root, env, {key: config[key] for key in values})
        receipt = {"server": info["Version"], "plugin": loaded, "candidate_sha256": sha(candidate / (PLUGIN + ".dll")),
                   "deployed_sha256": sha(plugin_dir / (PLUGIN + ".dll")),
                   "server_assemblies": {p.name: sha(p) for p in server.glob("*.dll")}, "backend": backend}
        (root / "versions.json").write_text(json.dumps(receipt, indent=2) + "\n")
        print("RUN", kind, "server=" + info["Version"], "plugin=" + str(loaded["Version"]), receipt["candidate_sha256"], flush=True)
        # Runtime plugin schema creation can trail HTTP readiness. It must be
        # created by the plugin, never by our fixture adapter.
        for _ in range(60):
            if backend == "postgres":
                assert not Path(env["PHANTOM_RIG_PHDB"]).exists(), "SQLite selected instead of PostgreSQL"
                ready = pg.sql("SELECT to_regclass('public.catalogue_items') IS NOT NULL;", pg.database) == "t"
            else:
                ready = Path(env["PHANTOM_RIG_PHDB"]).exists()
            if ready:
                break
            time.sleep(1)
        env["RIG_NO_RESET"] = "1"
        script = "35-channel-e2e-playback.sh" if kind == "movie" else "36-channel-episode-e2e-playback.sh"
        command = ([sys.executable, TOOLS / "stale-candidate-scenario.py", kind] if suite == "stale-candidate"
                   else ["bash", TOOLS / script])
        with (root / "scenario.log").open("w"):
            proc = start(command, root / "scenario.log", env)
            try:
                result = proc.wait(timeout=600)
                print((root / "scenario.log").read_text(), end="", flush=True)
                assert result == 0, f"{kind} scenario failed ({result})"
            finally:
                stop(proc)
        result = json.loads((root / "responses/result.json").read_text())
        assert result["backend"] == backend and result["result"] == "passed", result
        if backend == "postgres":
            assert not Path(env["PHANTOM_RIG_PHDB"]).exists(), "SQLite selected"
            state = pg.sql("SELECT tmdb_id,type,season,episode,stub_path FROM materialised_state ORDER BY tmdb_id;", pg.database)
            (root / "postgres-materialised.txt").write_text(state + "\n")
        return result
    finally:
        try:
            for process in reversed(processes):
                stop(process)
        finally:
            try:
                pg.close()
            finally:
                # Keep evidence, not credentials, DBs or large caches.
                for name in ("data", "config", "cache", "tmp", "phantom", "gostream"):
                    if (root / name).exists():
                        shutil.rmtree(root / name)
        (root / "cleanup.json").write_text(json.dumps({"owned_pids": [p.pid for p in processes],
            "exit_codes": [p.returncode for p in processes], "database": pg.database,
            "container_removed": pg.container is not None, "data_removed": not (root / "data").exists()}, indent=2))


def main():
    if sys.flags.optimize:
        raise RuntimeError("Assertions must be enabled; unset PYTHONOPTIMIZE")
    parser = argparse.ArgumentParser()
    parser.add_argument("--backend", choices=("postgres", "sqlite"), default="postgres")
    parser.add_argument("--scenario", choices=("movie", "episode", "both"), default="both")
    parser.add_argument("--suite", choices=("playback", "stale-candidate"), default="playback")
    args = parser.parse_args()
    artifacts = REPO / "artifacts"
    artifacts.mkdir(exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="playback-parity-", dir=artifacts))
    print("EVIDENCE", root, flush=True)
    env = os.environ.copy()
    env.update(MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", PYTHONDONTWRITEBYTECODE="1")
    def interrupted(signum, frame):
        raise KeyboardInterrupt(f"signal {signum}")
    signal.signal(signal.SIGTERM, interrupted)
    results = []
    try:
        env["PHANTOM_RIG_FFMPEG"] = ffmpeg(root)
        server, candidate = build(root, env)
        control = build_availability_control(root, server, candidate, env) if args.suite == "stale-candidate" else None
        failures = []
        for kind in (("movie", "episode") if args.scenario == "both" else (args.scenario,)):
            results.append({"scenario": kind, "backend": args.backend, "result": "failed"})
            try:
                results[-1] = scenario(kind, root / kind, server, candidate, args.backend, env, args.suite, control)
            except Exception as error:
                if args.suite != "stale-candidate":
                    raise
                # Preserve both parity observations, but NEVER turn a failed
                # scenario/cleanup into success. Exit nonzero after both run.
                results[-1]["error"] = str(error)
                failures.append(f"{kind}: {error}")
                print(f"SCENARIO_FAILED {kind}: {error}", file=sys.stderr, flush=True)
        if failures:
            raise AssertionError("; ".join(failures))
        marker = "STALE_CANDIDATE_REPROBE_OK" if args.suite == "stale-candidate" else "PLAYBACK_PARITY_OK"
        print(f"{marker} backend={args.backend} " + " ".join(r["scenario"] + "=passed" for r in results), flush=True)
    finally:
        (root / "results.json").write_text(json.dumps(results, indent=2) + "\n")
        for directory in ("build", "ffmpeg"):
            if (root / directory).exists() and os.environ.get("PHANTOM_PARITY_KEEP_BUILD") != "1":
                shutil.rmtree(root / directory)
        for archive in ("jellyfin-source.tar.gz", "ffmpeg.tar.xz"):
            if (root / archive).exists():
                (root / archive).unlink()


if __name__ == "__main__":
    main()

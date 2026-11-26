#!/usr/bin/env python3
"""Required 4M-row claim/worker check followed by isolated 35/36 parity."""
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import subprocess
import tempfile
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("parity", REPO / "tools/rig-scenarios/isolated-playback-rig.py")
parity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(parity)
root = Path(os.environ.get("PHANTOM_TEST_RESULTS") or tempfile.mkdtemp(prefix="availability-claims-"))
root.mkdir(parents=True, exist_ok=True)
env = os.environ.copy()
# Always build THIS checkout; an ambient prebuilt plugin is not evidence.
env.pop("PHANTOM_PARITY_PLUGIN", None)
env.pop("PHANTOM_PARITY_SERVER", None)
os.environ.pop("PHANTOM_PARITY_PLUGIN", None)
os.environ.pop("PHANTOM_PARITY_SERVER", None)
env.update(MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", PHANTOM_TEST_RESULTS=str(root))
(root / "hardware.json").write_text(json.dumps({"platform": platform.platform(), "cpu_count": os.cpu_count(),
    "cpu_affinity": sorted(os.sched_getaffinity(0)),
    "host_physical_memory_bytes": os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES"),
    "postgres_container_shared_memory_bytes": 256 * 1024 * 1024,
    "cache": "normal planner; no drop_caches, forced scan setting, prewarm or fastest-of retries"}, indent=2))
pg = parity.Postgres(root, env.copy())
def terminate(signum, frame):
    raise KeyboardInterrupt(f"terminated by signal {signum}")
signal.signal(signal.SIGTERM, terminate)
try:
    server, built_plugin = parity.build(root, env)
    # dotnet test may rebuild project references without publish dependencies.
    # Preserve the complete candidate bundle before it can mutate that output.
    plugin = root / "candidate"
    shutil.copytree(built_plugin, plugin)
    (root / "candidate-identity.json").write_text(json.dumps({p.name: parity.sha(p) for p in plugin.glob("*.dll")}, indent=2))
    shutil.copytree(REPO / "tests", root / "build/tests", ignore=shutil.ignore_patterns("bin", "obj"))
    pg.open()
    env["PHANTOM_TEST_POSTGRES_DSN"] = (f"Host={pg.env['PGHOST']};Port={pg.env['PGPORT']};"
        f"Username={pg.env['PGUSER']};Password={pg.env['PGPASSWORD']};Database={pg.database}")
    with (root / "dotnet-test.log").open("w") as log:
        parity.run(["dotnet", "test", root / "build/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj",
            "-c", "Release", "-m:2", "-p:UseSharedCompilation=false", "-p:CopyLocalLockFileAssemblies=true", "-nodeReuse:false",
            "--filter", "FullyQualifiedName~AvailabilityClaimsPostgresTests|FullyQualifiedName~AvailabilityProbeWorkerTests",
            "--logger", "trx;LogFileName=claims.trx", "--logger", "console;verbosity=normal", "--results-directory", root],
            env=env, stdout=log, stderr=subprocess.STDOUT)
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = ET.parse(root / "claims.trx").findall(".//t:UnitTestResult", ns)
    expected = {"AvailabilityClaimsPostgresTests": 2, "AvailabilityProbeWorkerTests": 22}
    for cls, count in expected.items():
        selected = [r for r in results if cls + "." in r.attrib["testName"]]
        assert len(selected) == count, (cls, count, len(selected))
    assert len(results) == sum(expected.values()), len(results)
    assert all(r.attrib["outcome"] == "Passed" for r in results), [(r.attrib["testName"], r.attrib["outcome"]) for r in results]
    assert parity.sha(built_plugin / (parity.PLUGIN + ".dll")) == parity.sha(plugin / (parity.PLUGIN + ".dll")), "tests rebuilt a different candidate"
    print(f"PASS PostgreSQL claim/worker and legacy worker regressions: {len(results)} passed; 0 skipped", flush=True)
    for node in ET.parse(root / "claims.trx").findall(".//t:StdOut", ns):
        print(node.text or "", flush=True)
    # The existing parity runner owns separate databases and invokes both real
    # server scenarios. These paths are the artifacts built above, not installed DLLs.
    env.update(PHANTOM_PARITY_SERVER=str(server), PHANTOM_PARITY_PLUGIN=str(plugin))
    parity.run(["python3", REPO / "tools/rig-scenarios/isolated-playback-rig.py", "--backend", "postgres"], env=env)
finally:
    pg.close()
    subprocess.run(["dotnet", "build-server", "shutdown"], check=True, env=env)
    print("Evidence retained:", root, flush=True)

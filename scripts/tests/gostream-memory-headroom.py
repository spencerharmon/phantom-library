"""Offline contract test: local Helm tuning + linked Flux standalone workload."""
import copy
from decimal import Decimal, ROUND_CEILING
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[2]
HIVE = Path(os.environ.get("BEEHIVE_WORKTREE", ROOT.parents[2]))
FLUX = Path(os.environ.get("FLUX_REPO", HIVE / "submodules/flux/repo"))
CHART = ROOT / "deploy/helm/phantom-library"


def render(command, values=None):
    result = subprocess.run(command, input=values, capture_output=True,
                            text=True, timeout=60)
    if result.returncode:
        raise AssertionError(result.stderr)
    return [o for o in yaml.safe_load_all(result.stdout) if o]


def mib(quantity):
    match = re.fullmatch(r"(\d+(?:\.\d+)?)(Ki|Mi|Gi|Ti|K|M|G|T)?", str(quantity))
    assert match, f"unsupported memory quantity: {quantity}"
    factors = {None: 1, "Ki": 2**10, "Mi": 2**20, "Gi": 2**30, "Ti": 2**40,
               "K": 10**3, "M": 10**6, "G": 10**9, "T": 10**12}
    return int((Decimal(match[1]) * factors[match[2]] / 2**20)
               .to_integral_value(rounding=ROUND_CEILING))


def budget(container):
    env = {e["name"]: e for e in container["env"]}
    assert len(env) == len(container["env"]), "duplicate environment variable"
    assert "GOMEMLIMIT" not in env, "static GOMEMLIMIT bypasses actual container limit"
    assert env["GOSTREAM_MEMORY_LIMIT_MIB"]["valueFrom"]["resourceFieldRef"] == {
        "containerName": container["name"], "resource": "limits.memory", "divisor": "1Mi"}
    limit = mib(container["resources"]["limits"]["memory"])
    assert mib(container["resources"]["requests"]["memory"]) <= limit
    assert container["command"][:2] == ["/bin/sh", "-c"]
    # Execute only the rendered budget preamble, never FUSE recovery or the service.
    preamble, boundary, _ = container["command"][2].partition('MP="${GOSTREAM_MOUNT_PATH')
    assert boundary, "missing safe startup boundary"
    result = subprocess.run(["/bin/sh", "-c", preamble + "\n/bin/sh -c 'printf \"%s\" \"$GOMEMLIMIT\"'"],
                            env={"PATH": os.defpath, "GOSTREAM_MEMORY_LIMIT_MIB": str(limit)},
                            capture_output=True, text=True, timeout=5)
    assert result.returncode == 0, result.stderr
    assert re.fullmatch(r"\d+MiB", result.stdout), result.stdout
    go = int(result.stdout[:-3])
    assert go >= 2200, "runtime budget below dependency contract"
    assert limit - go >= max(872, (limit + 3) // 4), "insufficient container reserve"
    return limit, go


def tuning(config, container):
    env = {e["name"]: e.get("value") for e in container["env"]}
    assert config["master_concurrency_limit"] == 6, "chart must cap native pumps at six"
    assert config["read_ahead_budget_mb"] == 64, "chart read-ahead must be 64 MiB"
    assert int(env["MKV_PROXY_CONCURRENCY"]) == config["master_concurrency_limit"]
    assert int(env["MKV_PROXY_READ_AHEAD_BUDGET"]) == config["read_ahead_budget_mb"] * 2**20


def mounted_config(pod, container, objects):
    volume = next(v for v in pod["volumes"] if v["name"] == "config-tuning")
    mounts = [m for m in container["volumeMounts"] if m["name"] == volume["name"]]
    assert len(mounts) == 1, "tuning volume not mounted by gostream"
    mount = mounts[0]
    assert "subPath" not in mount and "subPathExpr" not in mount
    items = volume["configMap"].get("items", [{"key": "config.tuning.json", "path": "config.tuning.json"}])
    paths = [i["path"] for i in items if i["key"] == "config.tuning.json"]
    assert len(paths) == 1, "tuning file not projected"
    env = {e["name"]: e.get("value") for e in container["env"]}
    assert env.get("MKV_PROXY_CONFIG_TUNING_PATH") == str(Path(mount["mountPath"]) / paths[0]), "tuning path disconnected"
    configs = [o for o in objects if o["kind"] == "ConfigMap"
               and o["metadata"]["name"] == volume["configMap"]["name"]]
    assert len(configs) == 1, "Helm config must be the one mounted by gostream"
    return json.loads(configs[0]["data"]["config.tuning.json"])


class MemoryHeadroomTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = tempfile.TemporaryDirectory(prefix="gostream-headroom-")
        cls.addClassCleanup(cls.scratch.cleanup)
        cls.helm = shutil.which("helm")
        if not cls.helm:
            # Same vendored release as helm-lint.test.sh; no host install required.
            subprocess.run(["tar", "-xzf", str(ROOT / "scripts/tests/vendor/helm-v3.16.3-linux-amd64.tar.gz"),
                            "-C", cls.scratch.name], check=True)
            cls.helm = str(Path(cls.scratch.name) / "linux-amd64/helm")
        cls.objects = render(["kubectl", "kustomize", str(
            FLUX / "infrastructure/phantom-library-gostream-memory-budget")])
        deployments = [o for o in cls.objects if o["kind"] == "Deployment"
                       and o["metadata"]["name"] == "phantom-library-gostream"]
        assert len(deployments) == 1, "standalone Deployment missing/duplicated"
        cls.pod = deployments[0]["spec"]["template"]["spec"]
        containers = [c for c in cls.pod["containers"] if c["name"] == "gostream"]
        assert len(containers) == 1
        cls.container = containers[0]
        hr = next(o for o in cls.objects if o["kind"] == "HelmRelease"
                  and o["metadata"]["name"] == "phantom-library-shared")
        cls.chart_objects = render([
            cls.helm, "template", hr["spec"]["releaseName"], str(CHART),
            "--namespace", hr["spec"]["targetNamespace"], "-f", "-",
        ], yaml.safe_dump(hr["spec"]["values"]))
        cls.config = mounted_config(cls.pod, cls.container, cls.chart_objects)

    def test_actual_container_and_mounted_helm_tuning(self):
        self.assertEqual(budget(self.container), (3072, 2200))
        tuning(self.config, self.container)
        self.assertFalse(any(o["kind"] == "StatefulSet" for o in self.chart_objects))
        self.assertFalse(any(o["kind"] == "Deployment" and
                             any(c["name"] == "gostream" for c in
                                 o["spec"]["template"]["spec"]["containers"])
                             for o in self.chart_objects))

    def test_derivation_tracks_same_container_limit(self):
        for memory, expected in [("4096Mi", 3072), ("3.5Gi", 2688)]:
            with self.subTest(memory=memory):
                changed = copy.deepcopy(self.container)
                changed["resources"]["limits"]["memory"] = memory
                self.assertEqual(budget(changed), (mib(memory), expected))
        changed["resources"]["limits"]["memory"] = "2Gi"
        with self.assertRaisesRegex(AssertionError, "requires at least"):
            budget(changed)

    def test_reject_static_budget_and_wrong_container(self):
        changed = copy.deepcopy(self.container)
        changed["env"].append({"name": "GOMEMLIMIT", "value": "2200MiB"})
        with self.assertRaisesRegex(AssertionError, "static GOMEMLIMIT"):
            budget(changed)
        changed = copy.deepcopy(self.container)
        next(e for e in changed["env"] if e["name"] == "GOSTREAM_MEMORY_LIMIT_MIB")[
            "valueFrom"]["resourceFieldRef"]["containerName"] = "jellyfin"
        with self.assertRaises(AssertionError):
            budget(changed)

    def test_reject_unmodified_deployment(self):
        objects = render(["kubectl", "kustomize", str(FLUX / "infrastructure/phantom-library-helm")])
        deployment = next(o for o in objects if o["kind"] == "Deployment"
                          and o["metadata"]["name"] == "phantom-library-gostream")
        old = next(c for c in deployment["spec"]["template"]["spec"]["containers"]
                   if c["name"] == "gostream")
        with self.assertRaisesRegex(AssertionError, "static GOMEMLIMIT"):
            budget(old)

    def test_reject_incident_tuning(self):
        for key, old in [("master_concurrency_limit", 25), ("read_ahead_budget_mb", 256)]:
            with self.subTest(key=key), self.assertRaises(AssertionError):
                tuning(dict(self.config, **{key: old}), self.container)

    def test_reject_unexported_budget(self):
        changed = copy.deepcopy(self.container)
        changed["command"][2] = changed["command"][2].replace("export GOMEMLIMIT=", "GOMEMLIMIT=")
        self.assertNotEqual(changed["command"], self.container["command"])
        with self.assertRaises(AssertionError):
            budget(changed)

    def test_reject_disconnected_config(self):
        changed = copy.deepcopy(self.container)
        changed["volumeMounts"] = [m for m in changed["volumeMounts"] if m["name"] != "config-tuning"]
        with self.assertRaisesRegex(AssertionError, "not mounted"):
            mounted_config(self.pod, changed, self.chart_objects)
        changed = copy.deepcopy(self.container)
        changed["env"] = [e for e in changed["env"] if e["name"] != "MKV_PROXY_CONFIG_TUNING_PATH"]
        with self.assertRaisesRegex(AssertionError, "disconnected"):
            mounted_config(self.pod, changed, self.chart_objects)

    def test_jellyfin_is_not_standalone_budget_source(self):
        objects = render([self.helm, "template", "combined", str(CHART),
                          "--set", "jellyfin.resources.limits.memory=5Gi",
                          "--set", "gostream.resources.limits.memory=128Mi"])
        pod = next(o for o in objects if o["kind"] == "StatefulSet")["spec"]["template"]["spec"]
        combined = next(c for c in pod["containers"] if c["name"] == "jellyfin")
        self.assertEqual(mib(combined["resources"]["limits"]["memory"]), 5120)
        self.assertEqual(next(e["value"] for e in combined["env"] if e["name"] == "GOMEMLIMIT"), "2200MiB")
        # Neither this combined-container limit nor the unused gostream.resources
        # value is allowed to substitute for the actual standalone container.
        self.assertEqual(budget(self.container), (3072, 2200))


if __name__ == "__main__":
    unittest.main(verbosity=2)

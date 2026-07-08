#!/usr/bin/env python3
"""Regression check for the Phantom Library Zuul (non-rig gate) config.

Toolchain-agnostic: needs only python3 + PyYAML — no .NET, no live Jellyfin,
no Zuul executor. It is the in-repo guard that keeps zuul.d/ + playbooks/ +
tools/ci/ from silently rotting, and doubles as the local "reproduce now"
lint + dry run.

What it enforces (see docs/ci-zuul.md for rationale):
  * every zuul.d/*.yaml and playbooks/*.yaml parses as YAML;
  * the project attaches jobs to `check` and `gate` ONLY, declares no
    `pipeline:` and no `name:` (untrusted-project rules);
  * every job attached to a pipeline is defined, and its run/post-run
    playbooks exist on disk;
  * the build/test contract is present: dotnet build -c Release + dotnet test
    with MSBUILDDISABLENODEREUSE=1 and -p:UseSharedCompilation=false, a cleanup
    trap, and a leftover dotnet/testhost verification;
  * the referenced scripts pass `bash -n`;
  * the build/test script runs clean end-to-end in dry-run mode (exercising
    the control flow + cleanup trap without a toolchain).

Exit 0 = all checks pass; 1 = a check failed; 2 = unable to run (missing dep).
"""
from __future__ import annotations

import os
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ZUUL_D = os.path.join(REPO_ROOT, "zuul.d")
PLAYBOOKS = os.path.join(REPO_ROOT, "playbooks")

try:
    import yaml
except ImportError:  # pragma: no cover
    sys.stderr.write(
        "verify-zuul-config: PyYAML is required "
        "(pip install pyyaml / apt install python3-yaml)\n"
    )
    sys.exit(2)

_failures: list[str] = []
_checks = 0


def check(cond: bool, msg: str) -> bool:
    global _checks
    _checks += 1
    if not cond:
        _failures.append(msg)
        print(f"  FAIL  {msg}")
    else:
        print(f"  ok    {msg}")
    return cond


def load_yaml(path: str):
    with open(path, encoding="utf-8") as fh:
        return yaml.safe_load(fh)


def read(path: str) -> str:
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def main() -> int:
    print("== files present ==")
    jobs_yaml = os.path.join(ZUUL_D, "jobs.yaml")
    project_yaml = os.path.join(ZUUL_D, "project.yaml")
    for p in (jobs_yaml, project_yaml):
        check(os.path.isfile(p), f"exists: {os.path.relpath(p, REPO_ROOT)}")

    print("\n== yaml parses ==")
    parsed: dict[str, object] = {}
    yaml_files = []
    for d in (ZUUL_D, PLAYBOOKS):
        if os.path.isdir(d):
            for name in sorted(os.listdir(d)):
                if name.endswith((".yaml", ".yml")):
                    yaml_files.append(os.path.join(d, name))
    for p in yaml_files:
        rel = os.path.relpath(p, REPO_ROOT)
        try:
            parsed[p] = load_yaml(p)
            check(True, f"parses: {rel}")
        except Exception as exc:  # noqa: BLE001
            check(False, f"parses: {rel} ({exc})")

    # --- job definitions -----------------------------------------------------
    print("\n== job definitions ==")
    jobs: dict[str, dict] = {}
    if os.path.isfile(jobs_yaml):
        for item in load_yaml(jobs_yaml) or []:
            if isinstance(item, dict) and "job" in item:
                job = item["job"]
                jobs[job["name"]] = job
    check(bool(jobs), f"at least one job defined ({len(jobs)} found)")

    for name, job in jobs.items():
        check("run" in job, f"job '{name}' has a run: playbook")
        for key in ("run", "post-run"):
            ref = job.get(key)
            if not ref:
                continue
            refs = ref if isinstance(ref, list) else [ref]
            for r in refs:
                pb = os.path.join(REPO_ROOT, r)
                check(os.path.isfile(pb), f"job '{name}' {key} playbook exists: {r}")
        # No Nodepool yet: a hardcoded nodeset would fake a node contract.
        check(
            "nodeset" not in job,
            f"job '{name}' declares no nodeset (no Nodepool yet — honest red)",
        )

    # --- project / pipeline attachment --------------------------------------
    print("\n== project attachment (untrusted-project rules) ==")
    project = None
    if os.path.isfile(project_yaml):
        for item in load_yaml(project_yaml) or []:
            if isinstance(item, dict) and "project" in item:
                project = item["project"]
    check(project is not None, "project.yaml defines a project stanza")

    if project is not None:
        check("name" not in project, "project omits name: (defaults to this repo)")
        attached: set[str] = set()
        allowed = {"check", "gate"}
        pipelines = {k for k in project.keys() if k not in ("templates", "vars", "queue")}
        for pl in pipelines:
            check(
                pl in allowed,
                f"project attaches only to check/gate (found pipeline key '{pl}')",
            )
            spec = project.get(pl) or {}
            for j in (spec.get("jobs") or []):
                jn = j if isinstance(j, str) else next(iter(j))
                attached.add(jn)
        for pl in ("check", "gate"):
            spec = project.get(pl) or {}
            check(bool(spec.get("jobs")), f"project attaches at least one job to {pl}")
        for jn in attached:
            check(jn in jobs, f"attached job '{jn}' is defined in zuul.d/jobs.yaml")

    # No project may define a pipeline of its own (untrusted).
    print("\n== no pipeline definitions (untrusted-project rule) ==")
    for p in yaml_files:
        if os.path.dirname(p) != ZUUL_D:
            continue
        for item in load_yaml(p) or []:
            if isinstance(item, dict):
                check(
                    "pipeline" not in item,
                    f"{os.path.relpath(p, REPO_ROOT)} declares no pipeline:",
                )

    # --- build/test contract -------------------------------------------------
    print("\n== build/test cleanup contract ==")
    build_sh = os.path.join(REPO_ROOT, "tools", "ci", "nonrig-build-test.sh")
    lib_sh = os.path.join(REPO_ROOT, "tools", "ci", "lib-cleanup.sh")
    cleanup_sh = os.path.join(REPO_ROOT, "tools", "ci", "nonrig-cleanup.sh")
    scripts = [build_sh, lib_sh, cleanup_sh]
    for s in scripts:
        check(os.path.isfile(s), f"exists: {os.path.relpath(s, REPO_ROOT)}")
        check(os.access(s, os.X_OK), f"executable: {os.path.relpath(s, REPO_ROOT)}")

    if os.path.isfile(build_sh):
        b = read(build_sh)
        check("MSBUILDDISABLENODEREUSE=1" in b, "build sets MSBUILDDISABLENODEREUSE=1")
        check(
            "UseSharedCompilation=false" in b,
            "build passes -p:UseSharedCompilation=false",
        )
        check("-c Release" in b or "-c" in b and "Release" in b, "build uses -c Release")
        check("dotnet build" in b, "build runs `dotnet build`")
        check("dotnet test" in b, "build runs `dotnet test`")
        check("trap cleanup EXIT" in b, "build installs a cleanup EXIT trap")
        check(
            "scripts/jellyfin-patches" in b,
            "build restores the patched Jellyfin source tree",
        )
    all_sh = "".join(read(s) for s in scripts if os.path.isfile(s))
    check("build-server shutdown" in all_sh, "cleanup runs `dotnet build-server shutdown`")
    check("testhost" in all_sh, "cleanup verifies no leftover testhost")
    check("VBCSCompiler" in all_sh, "cleanup verifies no leftover VBCSCompiler")

    # Referenced by the playbooks?
    print("\n== playbooks reference the scripts ==")
    run_pb = os.path.join(PLAYBOOKS, "phantom-library-nonrig-build-test.yaml")
    post_pb = os.path.join(PLAYBOOKS, "phantom-library-nonrig-cleanup.yaml")
    if os.path.isfile(run_pb):
        check(
            "tools/ci/nonrig-build-test.sh" in read(run_pb),
            "run playbook invokes tools/ci/nonrig-build-test.sh",
        )
        check(
            "groups['all'] | length == 0" in read(run_pb),
            "run playbook has the honest empty-inventory guard",
        )
    if os.path.isfile(post_pb):
        check(
            "tools/ci/nonrig-cleanup.sh" in read(post_pb),
            "post-run playbook invokes tools/ci/nonrig-cleanup.sh",
        )

    # --- bash -n syntax ------------------------------------------------------
    print("\n== bash -n syntax ==")
    have_bash = subprocess.run(
        ["bash", "-c", "true"], capture_output=True
    ).returncode == 0
    if have_bash:
        for s in scripts:
            if not os.path.isfile(s):
                continue
            r = subprocess.run(["bash", "-n", s], capture_output=True, text=True)
            check(
                r.returncode == 0,
                f"bash -n {os.path.relpath(s, REPO_ROOT)}"
                + (f" :: {r.stderr.strip()}" if r.returncode else ""),
            )
    else:
        print("  skip  bash not available")

    # --- toolchain-agnostic dry run -----------------------------------------
    print("\n== toolchain-agnostic dry run ==")
    if have_bash and os.path.isfile(build_sh):
        env = dict(os.environ)
        env.update(
            PHANTOM_CI_DRYRUN="1",
            PHANTOM_CI_PKILL="0",
            PHANTOM_CI_STRICT_LEFTOVERS="0",
        )
        r = subprocess.run(
            ["bash", build_sh], capture_output=True, text=True, env=env
        )
        ok = check(r.returncode == 0, "nonrig-build-test.sh dry run exits 0")
        out = r.stdout + r.stderr
        check("non-rig gate PASSED" in out, "dry run reaches PASSED")
        check("cleanup:" in out, "dry run exercises the cleanup trap")
        if not ok:
            print(out)
    else:
        print("  skip  bash/build script unavailable")

    print(f"\n{'=' * 48}")
    if _failures:
        print(f"FAILED: {len(_failures)}/{_checks} checks failed")
        for f in _failures:
            print(f"  - {f}")
        return 1
    print(f"OK: all {_checks} checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())

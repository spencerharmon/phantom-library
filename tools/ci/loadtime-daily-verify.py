#!/usr/bin/env python3
"""Offline post-merge receipt for a completed daily measurement and fresh push.

Capture guidance (read-only; supply deployment values outside this repository):
  kubectl -n "$NAMESPACE" get job "$JOB" -o json |
    jq '{apiVersion,kind,metadata:{name:.metadata.name,
         namespace:.metadata.namespace,uid:.metadata.uid},status}' > job.json
  kubectl -n "$NAMESPACE" exec "$READER_POD" -- \
    cat "$EVIDENCE_DIR/run-$ATTEMPT_ID.json" > manifest.json
  curl --fail --silent --show-error "$PUSHGATEWAY_URL/metrics" > metrics.prom
  date -u +%Y-%m-%dT%H:%M:%SZ

Use an existing authorized reader with the retained volume mounted read-only.
Capture after Job completion, before either Pushgateway group is overwritten;
never launch a Job or grant RBAC for this verifier. Keep captures private and
exclude pod specs, credentials, headers, response bodies and logs. The last
command supplies --captured-at. Run with --namespace, --job, --job-json,
--manifest, --metrics, --captured-at and --expected-sha (a full commit ID).
Select the exact merged checkout commit independently known to contain the fix;
this file-only check does not infer Git ancestry or prove deployment provenance.

The receipt checks trusted captures, not their authenticity or current live
state. Existing evidence has no Job UID and flow metrics have no attempt ID:
binding is exact stage run_start plus Job/stage time windows, not cryptographic
proof against concurrent writers. Capture from the same deployment/gateway and
exclude overlapping writers. Freshness defaults to 36h from run_start AND capture
to verification time. No network, subprocesses or writes occur here. A receipt
proves measurement/publication, not zero flow errors or no threshold breaches
(guard exit 3 is valid). Only hashes, validated times/SHA and counts are printed.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import re
import sys


FLOWS = ("list_load", "sort_change", "info_open", "get_sources",
         "materialise", "play_materialised")
STAGE_JOB = "phantom-loadtime-daily-run"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def timestamp(value):
    require(isinstance(value, str) and re.fullmatch(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})", value),
        "invalid timestamp")
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate JSON key")
        result[key] = value
    return result


def verify(args):
    raw = {name: Path(getattr(args, name)).read_bytes()
           for name in ("job_json", "manifest", "metrics")}
    job = json.loads(raw["job_json"], object_pairs_hook=unique_object)
    manifest = json.loads(raw["manifest"], object_pairs_hook=unique_object)
    require(re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", args.expected_sha),
            "expected SHA must be a full lowercase commit ID")
    require(manifest["checkout_sha"] == args.expected_sha, "checkout SHA mismatch")
    require(type(manifest["version"]) is int and manifest["version"] == 1,
            "unsupported manifest version")
    require(manifest["status"] == "complete" and
            type(manifest["exit_code"]) is int and manifest["exit_code"] == 0,
            "manifest is not successful and complete")
    require(re.fullmatch(r"[0-9a-f]{32}", manifest["attempt_id"]), "invalid attempt ID")
    for kind in ("Movie", "Episode"):
        require(re.fullmatch(r"[A-Za-z0-9-]{1,256}", manifest["targets"][kind]),
                "Movie and Episode targets must be nonempty sanitized IDs")
    require(manifest["color"] in ("blue", "green"), "invalid manifest color")
    require(job["apiVersion"] == "batch/v1" and job["kind"] == "Job" and
            job["metadata"]["namespace"] == args.namespace and
            job["metadata"]["name"] == args.job and
            isinstance(job["metadata"]["uid"], str) and job["metadata"]["uid"],
            "Job identity mismatch")
    status = job["status"]
    require(type(status["succeeded"]) is int and status["succeeded"] > 0 and
            status.get("active", 0) == 0 and
            any(c["type"] == "Complete" and c["status"] == "True"
                for c in status["conditions"]) and
            not any(c["type"] in ("Failed", "FailureTarget") and c["status"] == "True"
                    for c in status["conditions"]), "Job is not completed successfully")
    start, end = timestamp(status["startTime"]), timestamp(status["completionTime"])
    captured = timestamp(args.captured_at)
    now = datetime.now(timezone.utc).timestamp()
    run = manifest["run_start"]
    require(type(run) is int and run > 0 and
            math.isfinite(args.max_age_seconds) and args.max_age_seconds > 0,
            "invalid run_start or freshness limit")
    require(start <= run <= end <= captured <= now and
            now - run <= args.max_age_seconds and now - captured <= args.max_age_seconds,
            "stale or inconsistent Job/run/capture timestamps")
    previous_end = run
    for stage in ("measure", "push", "guard"):
        record = manifest["stages"][stage]
        require(all(type(record[k]) is int for k in ("exit_code", "start", "seconds")),
                "invalid stage values")
        require(record["exit_code"] in ((0, 3) if stage == "guard" else (0,)),
                "unsuccessful measurement/publication stage")
        require(record["seconds"] >= 0 and previous_end <= record["start"] and
                record["start"] + record["seconds"] <= end,
                "stage times outside ordered Job window")
        previous_end = record["start"] + record["seconds"]

    names = {"push_time_seconds", "phantom_loadtime_daily_run_start_timestamp_seconds",
             "phantom_loadtime_daily_stage_exit_code", "phantom_loadtime_daily_stage_seconds",
             "phantom_loadtime_seconds", "phantom_loadtime_runs_total",
             "phantom_loadtime_errors_total"}
    sample = re.compile(r'([a-zA-Z_:][a-zA-Z0-9_:]*)\{(.*)\}\s+(\S+)')
    label = re.compile(r'\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*"((?:[^"\\]|\\[\\"n])*)"\s*(,|$)')
    records = {}
    for line in raw["metrics"].decode("utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name = re.split(r"[\s{]", line, maxsplit=1)[0]
        if name not in names:
            continue
        match = sample.fullmatch(line)
        require(match is not None, "malformed relevant metric sample")
        name, rest, value = match.groups()
        labels = {}
        while rest.strip():
            part = label.match(rest)
            require(part is not None, "malformed metric labels")
            key, text, _ = part.groups()
            require(key not in labels, "duplicate metric label")
            labels[key] = re.sub(r'\\([\\"n])',
                                 lambda m: {"n": "\n", "\\": "\\", '"': '"'}[m[1]], text)
            rest = rest[part.end():]
        if labels.get("job") not in (args.flow_job, STAGE_JOB):
            continue
        value = float(value)
        require(math.isfinite(value) and value >= 0, "invalid metric value")
        key = (name, tuple(sorted(labels.items())))
        require(key not in records, "duplicate metric sample")
        records[key] = value

    def metric(name, group, **labels):
        # The emitter uses only /job/<name>; Pushgateway adds instance="".
        labels.update(job=group, instance="")
        key = (name, tuple(sorted(labels.items())))
        require(key in records, "missing expected metric or unexpected grouping labels")
        return records[key]

    require(metric("phantom_loadtime_daily_run_start_timestamp_seconds", STAGE_JOB) == run,
            "Pushgateway run_start mismatch")
    for stage in ("measure", "push", "guard"):
        for field, suffix in (("exit_code", "exit_code"), ("seconds", "seconds")):
            require(metric("phantom_loadtime_daily_stage_" + suffix, STAGE_JOB, stage=stage)
                    == manifest["stages"][stage][field], "Pushgateway stage mismatch")
    flow_push = metric("push_time_seconds", args.flow_job)
    stage_push = metric("push_time_seconds", STAGE_JOB)
    push = manifest["stages"]["push"]
    guard = manifest["stages"]["guard"]
    # Shell and Kubernetes timestamps are second-resolution; gateway pushes
    # are fractional. Accept only the completion second, not a later push.
    require(push["start"] <= flow_push < push["start"] + push["seconds"] + 1 and
            guard["start"] + guard["seconds"] <= stage_push and
            run <= flow_push <= stage_push < end + 1 and stage_push <= captured,
            "stale or out-of-window push")
    errors = 0
    for flow in FLOWS:
        for item in ("movie", "episode"):
            labels = dict(flow=flow, item_type=item, color=manifest["color"])
            metric("phantom_loadtime_seconds", args.flow_job, **labels)
            runs = metric("phantom_loadtime_runs_total", args.flow_job, **labels)
            failed = metric("phantom_loadtime_errors_total", args.flow_job, **labels)
            require(runs.is_integer() and runs > 0 and failed.is_integer() and failed <= runs,
                    "invalid flow sample counts")
            errors += int(failed)
    return dict(result="verified-captured-evidence", checkout_sha=args.expected_sha,
                run_start=run, job_start=start, job_completion=end, captured_at=captured,
                flow_push=flow_push, stage_push=stage_push, flow_pairs=12,
                error_samples=errors, guard_exit_code=guard["exit_code"],
                sha256={name: hashlib.sha256(data).hexdigest() for name, data in raw.items()})


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    for name in ("namespace", "job", "job-json", "manifest", "metrics", "expected-sha", "captured-at"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--flow-job", default="phantom-loadtime")
    parser.add_argument("--max-age-seconds", type=float, default=36 * 3600)
    args = parser.parse_args()
    try:
        require(bool(args.namespace and args.job and args.flow_job) and args.flow_job != STAGE_JOB,
                "invalid Job arguments")
        receipt = verify(args)
    except (OSError, UnicodeError, ValueError, KeyError, TypeError, AttributeError, OverflowError):
        # Input content (including parser exceptions) may contain private data.
        print("FAIL: invalid, incomplete, mismatched or stale captured evidence", file=sys.stderr)
        return 1
    print(json.dumps(receipt, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Standard-library CLI tests; synthetic captures are not live acceptance evidence."""

from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest


VERIFIER = Path(__file__).resolve().parents[2] / "tools/ci/loadtime-daily-verify.py"
SHA = "a" * 40
FLOWS = ("list_load", "sort_change", "info_open", "get_sources", "materialise", "play_materialised")


def iso(epoch):
    return datetime.fromtimestamp(epoch, timezone.utc).isoformat().replace("+00:00", "Z")


class VerifyTests(unittest.TestCase):
    def setUp(self):
        self.run = int(time.time()) - 120
        self.job = dict(apiVersion="batch/v1", kind="Job",
                        metadata=dict(namespace="test", name="daily-example", uid="example-uid"),
                        status=dict(succeeded=1, startTime=iso(self.run - 10),
                                    completionTime=iso(self.run + 60),
                                    conditions=[dict(type="Complete", status="True")]))
        self.manifest = dict(version=1, attempt_id="b" * 32, checkout_sha=SHA,
                             run_start=self.run, color="green", status="complete", exit_code=0,
                             targets=dict(Movie="movie-1", Episode="episode-1"), failures=[],
                             stages={stage: dict(exit_code=0, start=self.run + offset, seconds=5)
                                     for stage, offset in (("measure", 1), ("push", 10), ("guard", 20))})
        self.metrics = [
            f'phantom_loadtime_daily_run_start_timestamp_seconds{{instance="",job="phantom-loadtime-daily-run"}} {self.run}',
            f'push_time_seconds{{job="phantom-loadtime",instance=""}} {self.run + 12.5}',
            f'push_time_seconds{{job="phantom-loadtime-daily-run",instance=""}} {self.run + 25.5}',
        ]
        for stage in ("measure", "push", "guard"):
            for suffix, value in (("exit_code", 0), ("seconds", 5)):
                self.metrics.append(f'phantom_loadtime_daily_stage_{suffix}'
                                    f'{{stage="{stage}",job="phantom-loadtime-daily-run",instance=""}} {value}')
        for flow in FLOWS:
            for item in ("movie", "episode"):
                for family, value in (("seconds", 0.1), ("runs_total", 3), ("errors_total", 0)):
                    self.metrics.append(f'phantom_loadtime_{family}{{flow="{flow}",item_type="{item}",'
                                        f'color="green",job="phantom-loadtime",instance=""}} {value}')

    def invoke(self, expected=1, overrides=None, extra=()):
        payloads = {"job-json": json.dumps(self.job), "manifest": json.dumps(self.manifest),
                    "metrics": "\n".join(self.metrics) + "\n"}
        payloads.update(overrides or {})
        with tempfile.TemporaryDirectory() as directory:
            args = [sys.executable, str(VERIFIER), "--namespace", "test", "--job", "daily-example",
                    "--expected-sha", SHA, "--captured-at", iso(self.run + 70)]
            for name, data in payloads.items():
                path = Path(directory) / name
                path.write_text(data)
                args.extend(("--" + name, str(path)))
            result = subprocess.run(args + list(extra), capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, expected, result.stderr + result.stdout)
        if expected:
            self.assertEqual(result.stdout, "")
            self.assertNotIn("Traceback", result.stderr)
            self.assertNotIn("SECRET-MARKER", result.stderr)
        else:
            receipt = json.loads(result.stdout)
            self.assertEqual(receipt["result"], "verified-captured-evidence")
            self.assertEqual(receipt["flow_pairs"], 12)
            for name, data in payloads.items():
                self.assertEqual(receipt["sha256"][name.replace("-", "_")],
                                 hashlib.sha256(data.encode()).hexdigest())
            return receipt

    def test_fresh_complete_capture(self):
        receipt = self.invoke(0)
        self.assertEqual(receipt["run_start"], self.run)
        self.assertEqual(receipt["checkout_sha"], SHA)
        self.assertEqual(receipt["error_samples"], 0)

    def test_breach_and_flow_errors_are_reported_not_hidden(self):
        self.manifest["stages"]["guard"]["exit_code"] = 3
        self.metrics = [line.replace('stage="guard",job="phantom-loadtime-daily-run",instance=""} 0',
                                     'stage="guard",job="phantom-loadtime-daily-run",instance=""} 3')
                        if line.startswith("phantom_loadtime_daily_stage_exit_code") else line
                        for line in self.metrics]
        self.metrics[-1] = self.metrics[-1].rsplit(" ", 1)[0] + " 1"
        receipt = self.invoke(0)
        self.assertEqual(receipt["error_samples"], 1)
        self.assertEqual(receipt["guard_exit_code"], 3)

    def test_fractional_push_in_completion_second(self):
        self.job["status"]["completionTime"] = iso(self.run + 25)
        self.invoke(0)
        self.metrics[2] = self.metrics[2].rsplit(" ", 1)[0] + f" {self.run + 26}"
        self.invoke()

    def test_malformed_and_empty_files(self):
        for name in ("job-json", "manifest", "metrics"):
            for content in ("", "SECRET-MARKER", "null", "[]"):
                with self.subTest(name=name, content=content):
                    self.invoke(overrides={name: content})
        self.invoke(overrides={"manifest": '{"status":"SECRET-MARKER","status":"complete"}'})

    def test_missing_empty_or_invalid_targets(self):
        for kind in ("Movie", "Episode"):
            for value in ("", " ", None, [], "SECRET-MARKER/path"):
                with self.subTest(kind=kind, value=value):
                    self.manifest["targets"] = dict(Movie="movie-1", Episode="episode-1")
                    self.manifest["targets"][kind] = value
                    self.invoke()
            del self.manifest["targets"][kind]
            self.invoke()

    def test_wrong_sha(self):
        for sha in ("b" * 40, "unavailable", SHA[:8]):
            self.manifest["checkout_sha"] = sha
            self.invoke()

    def test_wrong_job_or_namespace(self):
        self.invoke(extra=("--job", "other"))
        self.invoke(extra=("--namespace", "other"))

    def test_incomplete_or_failed_job(self):
        for field, value in (("succeeded", 0), ("active", 1), ("conditions", []),
                             ("completionTime", iso(self.run - 1))):
            old = self.job["status"].copy()
            self.job["status"][field] = value
            self.invoke()
            self.job["status"] = old
        self.job["status"]["conditions"].append(dict(type="Failed", status="True"))
        self.invoke()

    def test_stale_capture_and_future_or_early_capture(self):
        self.invoke(extra=("--max-age-seconds", "30"))
        for capture in (iso(self.run + 50), iso(time.time() + 3600), "not-a-date"):
            self.invoke(extra=("--captured-at", capture))

    def test_failed_manifest_or_stage(self):
        self.manifest["status"] = "running"
        self.invoke()
        self.manifest["status"] = "complete"
        for stage in ("measure", "push", "guard"):
            self.manifest["stages"][stage]["exit_code"] = 2
            self.invoke()
            self.manifest["stages"][stage]["exit_code"] = 0

    def test_run_start_must_match_exactly_and_fit_job(self):
        self.manifest["run_start"] += 1
        self.invoke()
        self.manifest["run_start"] = self.run - 1000
        self.invoke()

    def test_stale_or_later_flow_and_stage_push(self):
        for index in (1, 2):
            old = self.metrics[index]
            for value in (self.run - 1, self.run + 61, self.run + 10000):
                self.metrics[index] = old.rsplit(" ", 1)[0] + f" {value}"
                self.invoke()
            self.metrics[index] = old
        # Even a push within the Job must fall in the retained push stage.
        self.metrics[1] = self.metrics[1].rsplit(" ", 1)[0] + f" {self.run + 19}"
        self.invoke()

    def test_missing_duplicate_malformed_or_wrong_color_metrics(self):
        original = self.metrics.copy()
        variants = [original[:-1], original + [original[-1]],
                    [line.replace('color="green"', 'color="blue"') for line in original]]
        for value in ("NaN", "+Inf", "-1", "4", "1.5", "SECRET-MARKER"):
            variants.append(original[:-1] + [original[-1].rsplit(" ", 1)[0] + " " + value])
        variants.append(original[:-1] + [original[-1].replace('instance=""', 'instance="",instance=""')])
        for variant in variants:
            with self.subTest(last=variant[-1]):
                self.metrics = variant
                self.invoke()

    def test_stage_metric_mismatch(self):
        self.metrics[3] = self.metrics[3].rsplit(" ", 1)[0] + " 1"
        self.invoke()


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Regress the shared validator's exact operator-controlled scheduling exceptions."""
import copy
from pathlib import Path
import sys
import unittest

import yaml

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools" / "ci"))
from gitea_runner_policy import supported_runner


class RunnerPolicyTests(unittest.TestCase):
    def setUp(self):
        # Parse YAML, including PyYAML's boolean-key interpretation of `on`.
        self.workflow = yaml.safe_load("""
on:
  push:
  pull_request:
  workflow_dispatch:
    inputs:
      runner_label:
        type: string
        required: false
        default: k8s
""")
        self.manual = "${{ inputs.runner_label || 'k8s' }}"
        self.swarm = "${{ vars.SWARM_HEALTH_RUNNER_LABEL || 'k8s' }}"

    def accepts(self, selector, workflow=None, filename="postgres-playback-parity.yaml", job="playback"):
        return supported_runner(self.workflow if workflow is None else workflow, filename, job, selector)

    def test_static_labels_and_shapes(self):
        for selector in ("k8s", "ubuntu-latest", ["k8s"], ["ubuntu-latest"], ["k8s", "ubuntu-latest"]):
            with self.subTest(selector=selector):
                self.assertTrue(self.accepts(selector, workflow={}, filename="other.yml", job="other"))
        for selector in (None, [], {}, True, 1, [None], [["k8s"]], "arbitrary-runner", ["k8s", "arbitrary-runner"]):
            with self.subTest(selector=selector):
                self.assertFalse(self.accepts(selector))

    def test_optional_manual_override_with_k8s_default(self):
        # A string input intentionally permits owned disposable labels on manual
        # dispatch. On push/PR, missing or empty input falls back to shared k8s.
        for selector in (self.manual, [self.manual]):
            self.assertTrue(self.accepts(selector))
        quoted = copy.deepcopy(self.workflow)
        quoted["on"] = quoted.pop(True)
        self.assertTrue(self.accepts(self.manual, workflow=quoted))

    def test_manual_requires_exact_dispatch_input_contract(self):
        for workflow in ({}, {True: None}, {True: ["push"]},
                         {True: {"workflow_dispatch": None}},
                         {True: {"workflow_dispatch": {}}},
                         {True: {"workflow_dispatch": {"inputs": {}}}}):
            self.assertFalse(self.accepts(self.manual, workflow=workflow))
        for field, value in (("default", "unknown"), ("default", "ubuntu-latest"),
                             ("required", True), ("required", "false"), ("type", "choice")):
            with self.subTest(field=field, value=value):
                workflow = copy.deepcopy(self.workflow)
                workflow[True]["workflow_dispatch"]["inputs"]["runner_label"][field] = value
                self.assertFalse(self.accepts(self.manual, workflow=workflow))
        for field in ("default", "required", "type"):
            workflow = copy.deepcopy(self.workflow)
            del workflow[True]["workflow_dispatch"]["inputs"]["runner_label"][field]
            self.assertFalse(self.accepts(self.manual, workflow=workflow))

    def test_manual_is_scoped_to_one_workflow_and_job(self):
        self.assertFalse(self.accepts(self.manual, filename="unrelated.yaml"))
        self.assertFalse(self.accepts(self.manual, job="unrelated"))
        self.assertFalse(self.accepts([self.manual, "k8s"]))

    def test_no_arbitrary_expressions_or_fallbacks(self):
        for selector in ("${{ inputs.runner_label }}", "${{ inputs.other || 'k8s' }}",
                         "${{ inputs.runner_label || 'unknown' }}", "${{ matrix.runner }}",
                         "${{ github.event.pull_request.title || 'k8s' }}",
                         "${{ inputs.runner_label || vars.RUNNER || 'k8s' }}", self.swarm):
            with self.subTest(selector=selector):
                self.assertFalse(self.accepts(selector))

    def test_swarm_repo_override_is_also_an_exact_scoped_exception(self):
        self.assertTrue(self.accepts([self.swarm], workflow={}, filename="swarm-health-image.yml", job="build-test-publish"))
        for filename, job, selector in (("unrelated.yml", "build-test-publish", self.swarm),
                                        ("swarm-health-image.yml", "other", self.swarm),
                                        ("swarm-health-image.yml", "build-test-publish", self.manual),
                                        ("swarm-health-image.yml", "build-test-publish", "${{ vars.OTHER || 'k8s' }}"),
                                        ("swarm-health-image.yml", "build-test-publish", "${{ vars.SWARM_HEALTH_RUNNER_LABEL }}")):
            with self.subTest(filename=filename, job=job, selector=selector):
                self.assertFalse(self.accepts(selector, filename=filename, job=job))


if __name__ == "__main__":
    unittest.main(verbosity=2)

#!/usr/bin/env python3
"""Guard regressions, NEVER a substitute for the sibling mandatory live test."""
import copy
import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[2] / "tools/rig-scenarios"
sys.path.insert(0, str(TOOLS))


class HarnessTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        env = patch.dict(os.environ, {"JF_RIG_ROOT": tmp.name, "PHANTOM_RIG_OUTPUT": tmp.name,
            "PHANTOM_RIG_API": "http://example.invalid", "PHANTOM_RIG_TOKEN": "synthetic",
            "PHANTOM_RIG_JDB": tmp.name + "/jellyfin.db", "PHANTOM_RIG_PHDB": tmp.name + "/phantom.db",
            "PHANTOM_RIG_BACKEND": "sqlite"})
        env.start()
        self.addCleanup(env.stop)
        spec = importlib.util.spec_from_file_location("stale", TOOLS / "stale-candidate-scenario.py")
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.before = dict(status="available", candidate_magnet="magnet:stale", priority=0, next_check_at=200,
                           probe_policy_hash="current", probe_generation=0)
        self.after = dict(status="available", candidate_magnet="magnet:fresh", priority=50, next_check_at=300,
                          probe_policy_hash="current", probe_generation=1, checked_at=100,
                          candidate_info_hash="a" * 40, candidate_indexer="Torrentio", lease_owner=None, last_error_kind=None)
        self.events = [{**self.before, "priority": 50, "next_check_at": 0}, self.after]
        self.candidates = [dict(info_hash="a" * 40, source="availability_probe", fetched_at=100,
                               expires_at=300, validation_status="unknown")]

    def check(self):
        self.module.assert_reprobe(self.before, self.after, self.events, self.candidates, "a" * 40, 100)

    def test_real_promotion_and_success_evidence_is_accepted(self):
        self.check()

    def test_no_promotion_or_priority_only_is_not_success(self):
        for events in ([], [self.after], [{**self.before, "priority": 50}]):
            with self.subTest(events=events):
                self.events = events
                with self.assertRaises(AssertionError):
                    self.check()

    def test_ordinary_due_or_policy_changed_cannot_masquerade_as_eager(self):
        self.before["next_check_at"] = 100
        with self.assertRaises(AssertionError):
            self.check()
        self.before["next_check_at"] = 200
        self.after["probe_policy_hash"] = "changed"
        with self.assertRaises(AssertionError):
            self.check()

    def test_no_probe_multiple_probes_and_failed_probe_are_rejected(self):
        original = copy.deepcopy(self.after)
        for field, value in (("probe_generation", 0), ("probe_generation", 2), ("last_error_kind", "transient"),
                             ("candidate_indexer", "fixture"), ("candidate_info_hash", "stale"), ("checked_at", 99)):
            with self.subTest(field=field, value=value):
                self.after = {**original, field: value}
                with self.assertRaises(AssertionError):
                    self.check()

    def test_manually_seeded_or_expired_cache_does_not_prove_reprobe(self):
        original = copy.deepcopy(self.candidates[0])
        for field, value in (("source", "rig"), ("expires_at", 99), ("fetched_at", 99), ("validation_status", "invalid")):
            with self.subTest(field=field):
                self.candidates = [{**original, field: value}]
                with self.assertRaises(AssertionError):
                    self.check()
        self.candidates = []
        with self.assertRaises(AssertionError):
            self.check()

    def test_exclusion_requires_repository_and_rest_positive_controls_for_both_types(self):
        for kind, key in (("movie", "Movies"), ("episode", "Series")):
            rows = [{"ProviderIds": {"Tmdb": "2"}}]
            self.module.assert_excluded({key: [2]}, rows, kind, 1, 2)
            for visible, rest in (({key: []}, rows), ({key: [2]}, []),
                                  ({key: [1, 2]}, rows), ({key: [2]}, rows + [{"ProviderIds": {"Tmdb": "1"}}])):
                with self.subTest(kind=kind, visible=visible, rest=rest), self.assertRaises(AssertionError):
                    self.module.assert_excluded(visible, rest, kind, 1, 2)

    def test_stale_rest_cache_fails_without_repairing_or_reprobing(self):
        with patch.object(self.module.rig, "browse", return_value=[]) as browse, patch.object(self.module.time, "sleep"):
            with self.assertRaisesRegex(AssertionError, "REST visibility not restored"):
                self.module.wait_restored("channel", 1)
            self.assertEqual(11, browse.call_count)
        restored = {"ProviderIds": {"Tmdb": "1"}}
        with patch.object(self.module.rig, "browse", side_effect=[[], [restored]]), patch.object(self.module.time, "sleep"):
            self.assertEqual(restored, self.module.wait_restored("channel", 1))


if __name__ == "__main__":
    unittest.main(verbosity=2)

#!/usr/bin/env python3
"""Fast adapter regressions; the sibling shell check ALSO runs PostgreSQL playback."""
import importlib.util
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import subprocess
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools/rig-scenarios"))
from playback_db import PhantomDatabase

spec = importlib.util.spec_from_file_location("isolated_rig", Path(__file__).resolve().parents[2] / "tools/rig-scenarios/isolated-playback-rig.py")
rig = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rig)


class AdapterTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "phantom.db"
        self.env = patch.dict(os.environ, {"PHANTOM_RIG_PHDB": str(self.path), "PHANTOM_RIG_BACKEND": "sqlite"}, clear=True)
        self.env.start()
        self.addCleanup(self.env.stop)

    def test_player_stats_runtime_refuses_host_network_before_starting_services(self):
        result = subprocess.run([sys.executable, rig.TOOLS / "player-stats-runtime.py",
            self.tmp.name, self.tmp.name, self.tmp.name, os.readlink("/proc/self/ns/net")],
            capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("must run in its own network namespace", result.stderr)
        self.assertFalse((Path(self.tmp.name) / "movie").exists())

    def test_sqlite_fixtures_and_upsert_preserve_values(self):
        db = PhantomDatabase()
        db.execute("CREATE TABLE plugin_meta(key TEXT PRIMARY KEY,value TEXT); PRAGMA user_version=21;")
        db.upsert("plugin_meta", ("key",), dict(key="scope", value="it's a movie"))
        self.assertEqual("it's a movie", db.query("SELECT value FROM plugin_meta"))
        db.upsert("plugin_meta", ("key",), dict(key="scope", value="episode"))
        self.assertEqual("1|episode", db.query("SELECT COUNT(*),max(value) FROM plugin_meta"))
        self.assertEqual({"backend": "sqlite", "schema": 21}, db.assert_backend())
        with self.assertRaises(sqlite3.OperationalError):
            db.query("SELECT missing FROM plugin_meta")

    def test_postgres_rejects_parallel_sqlite_before_any_psql(self):
        self.path.touch()
        os.environ.update(PHANTOM_RIG_BACKEND="postgres", PHANTOM_RIG_PSQL='["must-not-be-invoked"]')
        db = PhantomDatabase()
        with self.assertRaisesRegex(AssertionError, "SQLite selected"):
            db.assert_backend()
        with self.assertRaisesRegex(AssertionError, "SQLite selected"):
            db.query("SELECT 1")

    def test_postgres_never_falls_back_when_psql_missing(self):
        os.environ["PHANTOM_RIG_BACKEND"] = "postgres"
        with self.assertRaisesRegex(ValueError, "explicit owned psql"):
            PhantomDatabase()

    def test_sqlite_rejects_inherited_postgres(self):
        os.environ["PHANTOM_POSTGRES_HOST"] = "example.invalid"
        with self.assertRaisesRegex(AssertionError, "inherited PostgreSQL"):
            PhantomDatabase().assert_backend()

    def test_unknown_backend_is_not_sqlite(self):
        os.environ["PHANTOM_RIG_BACKEND"] = "postgrez"
        with self.assertRaises(ValueError):
            PhantomDatabase()

    def test_wrong_postgres_identity_is_fatal(self):
        os.environ.update(PHANTOM_RIG_BACKEND="postgres", PHANTOM_RIG_PSQL='["psql"]', PHANTOM_POSTGRES_DB="owned_db")
        db = PhantomDatabase()
        with patch.object(db, "query", return_value="different_db"):
            with self.assertRaises(AssertionError):
                db.assert_backend()

    def test_cleanup_only_signals_the_owned_process_group(self):
        command = [sys.executable, "-c", "import time; time.sleep(120)"]
        owned = rig.start(command, Path(self.tmp.name) / "owned.log", os.environ.copy())
        other = rig.start(command, Path(self.tmp.name) / "other.log", os.environ.copy())
        self.addCleanup(rig.stop, other)
        self.addCleanup(rig.stop, owned)
        rig.stop(owned)
        self.assertIsNotNone(owned.poll())
        self.assertIsNone(other.poll(), "cleanup signalled an unrelated process")

    def test_failed_build_removes_owned_tree_and_keeps_failure_evidence(self):
        def failed_build(root, env):
            (root / "build").mkdir()
            (root / "build.log").write_text("real build would report its failure here")
            raise RuntimeError("test build failure")
        with patch.object(rig, "REPO", Path(self.tmp.name)), patch.object(rig, "ffmpeg", return_value="unused"), patch.object(rig, "build", side_effect=failed_build), patch.object(sys, "argv", ["rig"]):
            with self.assertRaisesRegex(RuntimeError, "test build failure"):
                rig.main()
        root = next((Path(self.tmp.name) / "artifacts").iterdir())
        self.assertFalse((root / "build").exists())
        self.assertTrue((root / "build.log").exists())
        self.assertEqual("[]", (root / "results.json").read_text().strip())

    def load_scenarios(self):
        os.environ.update(JF_RIG_ROOT=self.tmp.name, PHANTOM_RIG_OUTPUT=self.tmp.name,
            PHANTOM_RIG_API="http://example.invalid", PHANTOM_RIG_TOKEN="synthetic", PHANTOM_RIG_JDB=self.tmp.name + "/jellyfin.db")
        spec = importlib.util.spec_from_file_location("scenarios", rig.TOOLS / "playback-scenarios.py")
        scenarios = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(scenarios)
        return scenarios

    def test_metadata_barrier_waits_for_initial_probe_and_final_persisted_tags(self):
        scenarios = self.load_scenarios()
        item = {"Id": "00000000000000000000000000000001"}
        initial, final = "2026-01-01 01:00:01", "2026-01-01 01:00:02"
        with patch.object(scenarios, "jellyfin_sql", side_effect=[
            ("0001-01-01 00:00:00", None, "phantom"), (initial, None, "phantom")]) as query, patch.object(scenarios.time, "sleep"):
            self.assertEqual(initial, scenarios.wait_metadata(item))
            self.assertEqual(2, query.call_count)
        with patch.object(scenarios, "jellyfin_sql", side_effect=[
            (initial, "/real.mkv", ""), (final, "/wrong.mkv", ""),
            (final, "/real.mkv", "phantom"), (final, "/real.mkv", "")]) as query, patch.object(scenarios.time, "sleep"):
            self.assertEqual(final, scenarios.wait_metadata(item, after=initial, path="/real.mkv"))
            self.assertEqual(4, query.call_count, "a real path alone is not a completed refresh")

    def test_metadata_barrier_never_accepts_stuck_phantom_tags_or_missing_item(self):
        scenarios = self.load_scenarios()
        item = {"Id": "00000000000000000000000000000001"}
        with patch.object(scenarios, "jellyfin_sql", return_value=("2026-01-01", "/real.mkv", "phantom")), patch.object(scenarios.time, "sleep"):
            with self.assertRaisesRegex(AssertionError, "did not converge"):
                scenarios.wait_metadata(item, path="/real.mkv")
        with patch.object(scenarios, "jellyfin_sql", return_value=None):
            with self.assertRaisesRegex(AssertionError, "disappeared"):
                scenarios.wait_metadata(item)

    def test_worker_config_rejects_boot_defaults_even_with_disabled_api_readback(self):
        config = dict(ChannelWarmupEnabled=False, EvictionEnabled=False, SeriesAutopilotEnabled=False)
        good = "Channel BaseItem warmup worker started startupDelay=30s interval=30min enabled=False"
        self.assertEqual(config, rig.assert_worker_configuration(good, config)["loaded_flags"])
        for bad in ("", good.replace("False", "True"), good + "\n" + good.replace("False", "True")):
            with self.subTest(log=bad), self.assertRaises(AssertionError):
                rig.assert_worker_configuration(bad, config)
        for key in config:
            with self.subTest(flag=key), self.assertRaises(AssertionError):
                rig.assert_worker_configuration(good, {**config, key: True})

    def test_configured_restart_stops_only_owned_server_before_start_and_readback(self):
        root = Path(self.tmp.name)
        (root / "logs").mkdir()
        (root / "logs/jellyfin-console.log").write_text("Channel BaseItem warmup worker started enabled=False\n")
        config = dict(ChannelWarmupEnabled=False, EvictionEnabled=False, SeriesAutopilotEnabled=False)
        other, bootstrap, restarted = Mock(), Mock(), Mock()
        restarted.poll.return_value = None
        processes = [other, bootstrap]
        events = []
        with patch.object(rig, "stop", side_effect=lambda p: events.append(("stop", p))), \
             patch.object(rig, "start", side_effect=lambda *a: events.append(("start", a[0])) or restarted), \
             patch.object(rig, "request", side_effect=lambda *a, **k: events.append(("read", k)) or config) as api:
            rig.restart_configured_server(["server"], processes, root,
                {"PHANTOM_RIG_API": "http://example.invalid", "PHANTOM_RIG_TOKEN": "synthetic"}, config)
        self.assertEqual(["stop", "start", "read"], [x[0] for x in events])
        self.assertTrue(api.call_args.args[1].endswith("/Configuration"), "public setup host is not readiness")
        self.assertIs(bootstrap, events[0][1])
        self.assertEqual([other, bootstrap, restarted], processes)
        self.assertEqual(2, len(api.call_args.args), "post-restart API must be read-only")
        self.assertTrue((root / "worker-configuration.json").exists())

    def test_container_removal_attempted_even_if_log_capture_fails(self):
        pg = rig.Postgres(Path(self.tmp.name), {})
        pg.container = "owned-container-id"
        with patch.object(rig, "run", side_effect=[subprocess.CalledProcessError(1, ["podman", "logs"]), None]) as invoke:
            with self.assertRaises(subprocess.CalledProcessError):
                pg.close()
        self.assertEqual(["podman", "rm", "-f", "owned-container-id"], invoke.call_args.args[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)

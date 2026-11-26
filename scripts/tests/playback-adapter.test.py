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
from unittest.mock import patch

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

    def test_container_removal_attempted_even_if_log_capture_fails(self):
        pg = rig.Postgres(Path(self.tmp.name), {})
        pg.container = "owned-container-id"
        with patch.object(rig, "run", side_effect=[subprocess.CalledProcessError(1, ["podman", "logs"]), None]) as invoke:
            with self.assertRaises(subprocess.CalledProcessError):
                pg.close()
        self.assertEqual(["podman", "rm", "-f", "owned-container-id"], invoke.call_args.args[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)

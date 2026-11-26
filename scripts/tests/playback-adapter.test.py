#!/usr/bin/env python3
"""Fast adapter regressions; the sibling shell check ALSO runs PostgreSQL playback."""
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools/rig-scenarios"))
from playback_db import PhantomDatabase


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


if __name__ == "__main__":
    unittest.main(verbosity=2)

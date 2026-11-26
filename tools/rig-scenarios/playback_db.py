"""Backend-aware parity fixtures. Never infer PostgreSQL from an existing SQLite file."""
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys

if sys.flags.optimize:
    raise RuntimeError("Assertions must be enabled; unset PYTHONOPTIMIZE")


class PhantomDatabase:
    def __init__(self):
        self.backend = os.environ.get("PHANTOM_RIG_BACKEND", "sqlite")
        if self.backend not in ("postgres", "sqlite"):
            raise ValueError(f"unknown backend {self.backend}")
        self.path = Path(os.environ["PHANTOM_RIG_PHDB"])
        self.psql = json.loads(os.environ.get("PHANTOM_RIG_PSQL", "[]"))
        if self.backend == "postgres" and not self.psql:
            raise ValueError("PostgreSQL requires an explicit owned psql command")

    def assert_backend(self):
        if self.backend == "postgres":
            assert not self.path.exists(), f"SQLite selected: {self.path} exists"
            identity = self.query("SELECT current_database();")
            assert identity == os.environ["PHANTOM_POSTGRES_DB"], identity
            version = self.query("SELECT version FROM phantom_schema_meta;")
        else:
            assert not os.environ.get("PHANTOM_POSTGRES_HOST"), "SQLite run inherited PostgreSQL config"
            version = self.query("PRAGMA user_version;")
        assert int(version) > 0, f"plugin did not initialize {self.backend}"
        return {"backend": self.backend, "schema": int(version)}

    def query(self, sql):
        if self.backend == "postgres":
            assert not self.path.exists(), f"SQLite selected: {self.path} exists"
            result = subprocess.run(self.psql + ["-X", "-qAt", "-v", "ON_ERROR_STOP=1"],
                                    input=sql, text=True, capture_output=True, check=True)
            return result.stdout.strip()
        with sqlite3.connect(self.path, timeout=30) as db:
            rows = db.execute(sql).fetchall()
        return "\n".join("|".join("" if x is None else str(x) for x in row) for row in rows)

    def execute(self, sql):
        if self.backend == "postgres":
            self.query("BEGIN;\n" + sql + "\nCOMMIT;")
        else:
            with sqlite3.connect(self.path, timeout=30) as db:
                db.executescript("BEGIN;\n" + sql + "\nCOMMIT;")

    def upsert(self, table, keys, values):
        # Fixture inputs only, not an application SQL translator. Standard SQL
        # ON CONFLICT works unchanged on both backends (no INSERT OR REPLACE).
        def literal(value):
            if value is None:
                return "NULL"
            if isinstance(value, int):
                return str(value)
            return "'" + str(value).replace("'", "''") + "'"
        cols = ",".join(values)
        update = ",".join(f"{c}=excluded.{c}" for c in values if c not in keys)
        self.execute(f"INSERT INTO {table} ({cols}) VALUES "
                     f"({','.join(literal(v) for v in values.values())}) "
                     f"ON CONFLICT ({','.join(keys)}) DO UPDATE SET {update};")

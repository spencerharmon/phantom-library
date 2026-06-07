#!/usr/bin/env python3
"""DB row observer for the Phantom Library rig.

Polls one or more (db_path, table, where_clause) targets every 500ms
and logs row state changes (with full column dump) to a timeline file.

Use:
  db-observer.py jellyfin.db:BaseItems:"Path LIKE '%phantom_tmdb99000001%'" \
                 phantom.db:phantom_items:"tmdb_id=99000001"

Writes to /tmp/jf-rig/logs/observer-<pid>.log. Sends SIGTERM-clean.
"""
from __future__ import annotations
import os, sys, time, signal, sqlite3, json
from pathlib import Path

LOG_DIR = Path(os.environ.get("JF_RIG_ROOT", "/tmp/jf-rig")) / "logs"
LOG = LOG_DIR / f"observer-{os.getpid()}.log"

POLL_MS = int(os.environ.get("OBSERVER_POLL_MS", "500"))

_stop = False
def _sig(*_):
    global _stop
    _stop = True
signal.signal(signal.SIGTERM, _sig)
signal.signal(signal.SIGINT, _sig)

def parse_target(spec):
    db, table, where = spec.split(":", 2)
    return db, table, where

def snapshot(db, table, where):
    try:
        conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True, timeout=2)
        conn.row_factory = sqlite3.Row
        rows = list(conn.execute(f"SELECT * FROM {table} WHERE {where}").fetchall())
        conn.close()
        return [dict(r) for r in rows]
    except Exception as e:
        return [{"_observer_error": str(e)}]

def fingerprint(rows):
    return json.dumps(rows, sort_keys=True, default=str)

def main():
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    targets = [parse_target(a) for a in sys.argv[1:]]
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    with LOG.open("w") as f:
        f.write(f"# observer-{os.getpid()} start {time.strftime('%Y-%m-%d %H:%M:%S')} poll={POLL_MS}ms\n")
        for db, table, where in targets:
            f.write(f"# target: {db} {table} WHERE {where}\n")
        f.flush()

        last_fp = {i: None for i in range(len(targets))}
        while not _stop:
            ts = time.strftime("%Y-%m-%d %H:%M:%S.") + f"{int(time.time()*1000)%1000:03d}"
            for i, (db, table, where) in enumerate(targets):
                rows = snapshot(db, table, where)
                fp = fingerprint(rows)
                if fp != last_fp[i]:
                    last_fp[i] = fp
                    f.write(f"{ts} [t{i}] {fp}\n")
                    f.flush()
            time.sleep(POLL_MS / 1000.0)
        f.write(f"# observer-{os.getpid()} stop {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
    print(f"observer-{os.getpid()} log: {LOG}", flush=True)

if __name__ == "__main__":
    main()

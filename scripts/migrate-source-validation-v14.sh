#!/usr/bin/env bash
set -euo pipefail

# TODO(operator-approved): Pre-v1.0 offline schema migration exception for
# source validation v14, approved by operator on 2026-06-24. Normal project
# rule remains wipe/rebuild for schema changes; this script preserves v13
# source candidates while Jellyfin is stopped and upgrades the DB to v14.

DB=${1:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}

if [ ! -f "$DB" ]; then
  echo "ERROR: phantom.db not found: $DB" >&2
  exit 1
fi

case "$DB" in
  /var/lib/jellyfin/*|/etc/jellyfin/*)
    if pgrep -x jellyfin >/dev/null 2>&1 || pgrep -f 'jellyfin.dll' >/dev/null 2>&1; then
      echo "ERROR: Jellyfin appears to be running. Stop jellyfin.service before migrating." >&2
      exit 1
    fi
    ;;
  *)
    echo "Non-production DB path; skipping live Jellyfin process check for clone test."
    ;;
esac

has_column() {
  local table=$1
  local column=$2
  sqlite3 "$DB" "SELECT COUNT(*) FROM pragma_table_info('$table') WHERE name='$column';"
}

has_table() {
  local table=$1
  sqlite3 "$DB" "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='$table';"
}

verify_v14() {
  local version
  version=$(sqlite3 "$DB" 'PRAGMA user_version;')
  if [ "$version" != "14" ]; then
    echo "ERROR: expected user_version=14, got $version" >&2
    exit 1
  fi
  for spec in \
    "source_candidates validation_status" \
    "source_candidates validation_policy_version" \
    "source_candidates selected_file_path" \
    "magnet_failure_cache validation_policy_version"; do
    set -- $spec
    if [ "$(has_column "$1" "$2")" != "1" ]; then
      echo "ERROR: v14 verification failed: $1.$2 missing" >&2
      exit 1
    fi
  done
  for table in bulk_materialise_requests bulk_materialise_items; do
    if [ "$(has_table "$table")" != "1" ]; then
      echo "ERROR: v14 verification failed: table $table missing" >&2
      exit 1
    fi
  done
}

version=$(sqlite3 "$DB" 'PRAGMA user_version;')
case "$version" in
  13) ;;
  14)
    verify_v14
    echo "source validation migration already applied (user_version=14)."
    exit 0
    ;;
  *)
    echo "ERROR: expected phantom.db user_version 13 or 14, got $version" >&2
    exit 1
    ;;
esac

if [ "$(has_table source_candidates)" != "1" ]; then
  echo "ERROR: v13 DB missing source_candidates; refusing." >&2
  exit 1
fi
if [ "$(has_table magnet_failure_cache)" != "1" ]; then
  echo "ERROR: v13 DB missing magnet_failure_cache; refusing." >&2
  exit 1
fi

before_candidates=$(sqlite3 "$DB" 'SELECT COUNT(*) FROM source_candidates;')

# Force WAL into main DB before taking the rollback artifact. Rollback uses
# the checkpointed main DB; copied -wal/-shm are forensic only.
sqlite3 "$DB" 'PRAGMA wal_checkpoint(TRUNCATE);' >/dev/null

backup="${DB}.pre-v14-source-validation.$(date -u +%Y%m%dT%H%M%SZ).bak"
cp -a "$DB" "$backup"
if [ -f "${DB}-wal" ]; then cp -a "${DB}-wal" "${backup}-wal"; fi
if [ -f "${DB}-shm" ]; then cp -a "${DB}-shm" "${backup}-shm"; fi

echo "Backup: $backup"

sqlite3 "$DB" <<'SQL'
PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

DROP TABLE IF EXISTS source_candidates_v14;
CREATE TABLE source_candidates_v14 (
    tmdb_id    INTEGER NOT NULL,
    type       TEXT NOT NULL,
    season     INTEGER NOT NULL DEFAULT -1,
    episode    INTEGER NOT NULL DEFAULT -1,
    preset     TEXT NOT NULL DEFAULT '',
    magnet     TEXT NOT NULL,
    info_hash  TEXT NOT NULL,
    indexer    TEXT NOT NULL,
    title      TEXT NOT NULL,
    seeders    INTEGER,
    size       INTEGER,
    rank       INTEGER NOT NULL,
    source     TEXT NOT NULL,
    fetched_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    validation_status TEXT NOT NULL DEFAULT 'unknown',
    validation_reason TEXT,
    validated_at INTEGER,
    validation_expires_at INTEGER,
    validation_duration_ms INTEGER,
    validation_policy_version TEXT NOT NULL DEFAULT 'unknown',
    selected_file_id INTEGER,
    selected_file_path TEXT,
    selected_file_size INTEGER,
    PRIMARY KEY (tmdb_id,type,season,episode,preset,magnet)
);

INSERT INTO source_candidates_v14
(tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at,
 validation_status,validation_reason,validated_at,validation_expires_at,validation_duration_ms,validation_policy_version,
 selected_file_id,selected_file_path,selected_file_size)
SELECT tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at,
       'unknown',NULL,NULL,NULL,NULL,'unknown',NULL,NULL,NULL
FROM source_candidates;

DROP INDEX IF EXISTS idx_source_candidates_item;
DROP INDEX IF EXISTS idx_source_candidates_item_rank;
DROP INDEX IF EXISTS idx_source_candidates_validation;
DROP INDEX IF EXISTS idx_source_candidates_expiry;
DROP INDEX IF EXISTS idx_source_candidates_hash;
DROP TABLE source_candidates;
ALTER TABLE source_candidates_v14 RENAME TO source_candidates;
CREATE INDEX idx_source_candidates_item_rank
    ON source_candidates(tmdb_id,type,season,episode,preset,rank);
CREATE INDEX idx_source_candidates_validation
    ON source_candidates(tmdb_id,type,season,episode,preset,validation_status,rank);
CREATE INDEX idx_source_candidates_expiry ON source_candidates(expires_at);
CREATE INDEX idx_source_candidates_hash ON source_candidates(info_hash);

DROP TABLE IF EXISTS magnet_failure_cache_v14;
CREATE TABLE magnet_failure_cache_v14 (
    tmdb_id     INTEGER NOT NULL DEFAULT 0,
    imdb_id     TEXT NOT NULL DEFAULT '',
    type        TEXT NOT NULL,
    season      INTEGER NOT NULL DEFAULT 0,
    episode     INTEGER NOT NULL DEFAULT 0,
    preset      TEXT NOT NULL DEFAULT '',
    magnet      TEXT NOT NULL,
    info_hash   TEXT NOT NULL,
    reason      TEXT NOT NULL,
    failed_at   INTEGER NOT NULL,
    retry_after INTEGER NOT NULL,
    validation_policy_version TEXT NOT NULL DEFAULT 'legacy',
    PRIMARY KEY (tmdb_id, imdb_id, type, season, episode, preset, magnet)
);
INSERT INTO magnet_failure_cache_v14
(tmdb_id,imdb_id,type,season,episode,preset,magnet,info_hash,reason,failed_at,retry_after,validation_policy_version)
SELECT tmdb_id,imdb_id,type,season,episode,preset,magnet,info_hash,reason,failed_at,retry_after,'legacy'
FROM magnet_failure_cache;
DROP INDEX IF EXISTS idx_magnet_failure_cache_retry_after;
DROP TABLE magnet_failure_cache;
ALTER TABLE magnet_failure_cache_v14 RENAME TO magnet_failure_cache;
CREATE INDEX idx_magnet_failure_cache_retry_after ON magnet_failure_cache(retry_after);

DELETE FROM magnet_failure_cache
WHERE type='episode'
  AND reason IN ('target_episode_not_found','no_valid_files','fuse_path_missing','no_english_audio','no_main_english_audio','audio_probe_unsupported_format');

CREATE TABLE IF NOT EXISTS bulk_materialise_requests (
    request_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    parent_external_id TEXT NOT NULL,
    parent_kind TEXT NOT NULL,
    tmdb_id INTEGER NOT NULL,
    season INTEGER NOT NULL DEFAULT -1,
    trigger TEXT NOT NULL,
    status TEXT NOT NULL,
    requested_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    last_error TEXT,
    last_unfavorited_at INTEGER,
    generation INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_bulk_materialise_requests_status
    ON bulk_materialise_requests(status, updated_at);
CREATE UNIQUE INDEX IF NOT EXISTS idx_bulk_materialise_requests_active_parent
    ON bulk_materialise_requests(user_id, parent_external_id)
    WHERE status IN ('pending','running');

CREATE TABLE IF NOT EXISTS bulk_materialise_items (
    request_id TEXT NOT NULL,
    tmdb_id INTEGER NOT NULL,
    type TEXT NOT NULL,
    season INTEGER NOT NULL,
    episode INTEGER NOT NULL,
    status TEXT NOT NULL,
    generation INTEGER NOT NULL DEFAULT 0,
    claim_token TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_run_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    last_error TEXT,
    PRIMARY KEY (request_id, tmdb_id, type, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_bulk_materialise_items_due
    ON bulk_materialise_items(status, next_run_at);
CREATE INDEX IF NOT EXISTS idx_bulk_materialise_items_episode
    ON bulk_materialise_items(tmdb_id, type, season, episode);

PRAGMA user_version=14;
COMMIT;
SQL

verify_v14
after_candidates=$(sqlite3 "$DB" 'SELECT COUNT(*) FROM source_candidates;')
if [ "$before_candidates" != "$after_candidates" ]; then
  echo "ERROR: source_candidates row count changed: before=$before_candidates after=$after_candidates" >&2
  exit 1
fi

echo "source validation migration complete: source_candidates=$after_candidates schema=14"

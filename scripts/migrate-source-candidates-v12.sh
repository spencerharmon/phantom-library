#!/usr/bin/env bash
set -euo pipefail

# TODO(operator-approved): Pre-v1.0 offline schema migration exception for
# source_candidates v12, approved by operator on 2026-06-23. Normal project
# rule remains wipe/rebuild for schema changes; this one script exists so the
# operator can preserve already-probed source availability/candidate data while
# Jellyfin is stopped.

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

version=$(sqlite3 "$DB" 'PRAGMA user_version;')
case "$version" in
  11) ;;
  12)
    existing=$(sqlite3 "$DB" "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_candidates';")
    if [ "$existing" = "1" ]; then
      echo "source_candidates migration already applied (user_version=12)."
      exit 0
    fi
    echo "ERROR: user_version=12 but source_candidates table missing; refusing." >&2
    exit 1
    ;;
  *)
    echo "ERROR: expected phantom.db user_version 11 or 12, got $version" >&2
    exit 1
    ;;
esac

backup="${DB}.pre-v12-source-candidates.$(date -u +%Y%m%dT%H%M%SZ).bak"
cp -a "$DB" "$backup"
if [ -f "${DB}-wal" ]; then cp -a "${DB}-wal" "${backup}-wal"; fi
if [ -f "${DB}-shm" ]; then cp -a "${DB}-shm" "${backup}-shm"; fi

echo "Backup: $backup"

sqlite3 "$DB" <<'SQL'
PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS source_candidates (
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
    PRIMARY KEY (tmdb_id, type, season, episode, preset, magnet)
);
CREATE INDEX IF NOT EXISTS idx_source_candidates_item
    ON source_candidates(tmdb_id, type, season, episode, preset, rank);
CREATE INDEX IF NOT EXISTS idx_source_candidates_expiry
    ON source_candidates(expires_at);
CREATE INDEX IF NOT EXISTS idx_source_candidates_hash
    ON source_candidates(info_hash);

INSERT OR IGNORE INTO source_candidates
(tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at)
SELECT
    a.tmdb_id,
    a.type,
    a.season,
    a.episode,
    COALESCE(NULLIF(m.preset,''), 'gostream-default') AS preset,
    a.candidate_magnet,
    a.candidate_info_hash,
    COALESCE(NULLIF(a.candidate_indexer,''), 'availability') AS indexer,
    COALESCE(NULLIF(tm.title,''), NULLIF(te.title,''), '') AS title,
    a.candidate_seeders,
    a.candidate_size,
    1,
    'availability_migration',
    COALESCE(a.checked_at, strftime('%s','now')),
    CASE
      WHEN a.next_check_at IS NOT NULL AND a.next_check_at > strftime('%s','now') THEN a.next_check_at
      ELSE strftime('%s','now','+24 hours')
    END
FROM availability_items a
LEFT JOIN magnet_cache m
  ON m.tmdb_id=a.tmdb_id AND m.type=a.type
 AND m.season=CASE WHEN a.season < 0 THEN 0 ELSE a.season END
 AND m.episode=CASE WHEN a.episode < 0 THEN 0 ELSE a.episode END
LEFT JOIN tmdb_metadata tm
  ON tm.tmdb_id=a.tmdb_id AND tm.type=CASE WHEN a.type='episode' THEN 'series' ELSE a.type END
LEFT JOIN tmdb_episode_cache te
  ON te.series_tmdb_id=a.tmdb_id AND te.season=a.season AND te.episode=a.episode
WHERE a.candidate_magnet IS NOT NULL AND a.candidate_magnet <> ''
  AND a.candidate_info_hash IS NOT NULL AND a.candidate_info_hash <> '';

INSERT OR IGNORE INTO source_candidates
(tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at)
SELECT
    m.tmdb_id,
    m.type,
    CASE WHEN m.type='movie' AND m.season=0 THEN -1 ELSE m.season END,
    CASE WHEN m.type='movie' AND m.episode=0 THEN -1 ELSE m.episode END,
    m.preset,
    m.magnet,
    m.info_hash,
    m.indexer,
    COALESCE(NULLIF(tm.title,''), NULLIF(te.title,''), '') AS title,
    m.seeders,
    m.size,
    1,
    'magnet_cache_migration',
    m.cached_at,
    m.cached_at + m.ttl_seconds
FROM magnet_cache m
LEFT JOIN tmdb_metadata tm
  ON tm.tmdb_id=m.tmdb_id AND tm.type=CASE WHEN m.type='episode' THEN 'series' ELSE m.type END
LEFT JOIN tmdb_episode_cache te
  ON te.series_tmdb_id=m.tmdb_id AND te.season=m.season AND te.episode=m.episode
WHERE m.magnet IS NOT NULL AND m.magnet <> ''
  AND m.info_hash IS NOT NULL AND m.info_hash <> '';

PRAGMA user_version=12;
COMMIT;
SQL

schema=$(sqlite3 "$DB" 'PRAGMA user_version;')
count=$(sqlite3 "$DB" 'SELECT COUNT(*) FROM source_candidates;')
if [ "$schema" != "12" ]; then
  echo "ERROR: migration failed; schema=$schema" >&2
  exit 1
fi

echo "source_candidates migration complete: rows=$count schema=$schema"

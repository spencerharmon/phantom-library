#!/usr/bin/env bash
# Shared DB helpers for Phantom rig scenarios.
# Tests must use an existing cloned DB under /var/tmp/jf-test (or caller-provided
# rig path). Do not clone production DBs during routine test runs.
set -euo pipefail

rig_fail() { echo "FAIL: $*" >&2; exit 1; }

ensure_existing_rig_jellyfin_db() {
  local db=$1
  [ -s "$db" ] || rig_fail "existing Jellyfin DB clone missing/empty: $db. Seed /var/tmp/jf-test once outside test run; do not copy prod during scenario execution."
  chmod u+rw "$db" 2>/dev/null || true
}

migrate_existing_rig_phantom_db() {
  local db=$1
  local repo=${2:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
  [ -s "$db" ] || rig_fail "existing Phantom DB clone missing/empty: $db. Seed /var/tmp/jf-test once or run a reset scenario that intentionally starts with no phantom.db."
  chmod u+rw "$db" 2>/dev/null || true

  local version
  version=$(sqlite3 "$db" 'PRAGMA user_version;')
  case "$version" in
    14)
      "$repo/scripts/migrate-source-validation-v14.sh" "$db" >/tmp/phantom-rig-migrate-v14.log
      ;;
    13)
      "$repo/scripts/migrate-source-validation-v14.sh" "$db" >/tmp/phantom-rig-migrate-v14.log
      ;;
    *)
      rig_fail "existing Phantom DB clone at $db has user_version=$version; expected 13 or 14 for offline SV14 migration. Refresh seed clone outside test run or wipe/reset this rig intentionally."
      ;;
  esac
}

migrate_existing_rig_phantom_db_if_present() {
  local db=$1
  local repo=${2:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
  [ -e "$db" ] || return 0
  migrate_existing_rig_phantom_db "$db" "$repo"
}

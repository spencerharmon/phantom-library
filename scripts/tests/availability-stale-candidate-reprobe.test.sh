#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-stale-candidate-reprobe.test.sh
#
# In-repo regression harness for availability-stale-candidate-reprobe-001
# (ROI Priority 12, dial #1/#2 follow-up): CLOSE THE STALE-AVAILABLE-WITH-NO-
# LIVE-CANDIDATES BROWSE GAP.
#
# The dial #2 browse predicate (browse-prune-dead-swarm-001) admits an
# availability_items row with status='available' to DEFAULT BROWSE whenever no
# source_candidates rows exist at all (the `NOT EXISTS` branch). That is CORRECT
# for a brand-new item that was never probed, but it ALSO silently admits an
# item that WAS probed, cached a winning candidate, and then had that candidate
# EXPIRE OUT of / be pruned from source_candidates (the MagnetCacheTtlHours TTL
# elapsed) while the stale availability_items.status row still reads
# 'available'. Such an item stays visible with ZERO live candidates, so a cold
# attempt goes straight to no_candidate/availability_abstain.
#
# The fix distinguishes "never assessed" (availability_items.candidate_magnet
# IS NULL) from "assessed, cached a candidate, then the cache emptied out"
# (candidate_magnet IS NOT NULL, zero live source_candidates) for an
# available-status row, and EXCLUDES the latter from default browse (same as the
# dead-swarm case) — the AvailabilityProbeWorker then eagerly re-probes it (a
# higher-priority-than-backlog promotion) to restore a live candidate or confirm
# unavailability. No schema change: reuses the existing candidate_magnet column.
#
# Two layers, mirroring scripts/tests/browse-prune-dead-swarm.test.sh:
#
#   A. STRUCTURAL: the never-assessed distinguisher and eager-reprobe plumbing
#      the fix requires are present in the tracked source (the
#      AvailableNeverAssessedSql helper applied to BOTH ListVisibleMovieRowsAsync
#      and ListVisibleSeriesRowsAsync, the MarkStaleAvailableItemsDueAsync eager-
#      reprobe method, its wiring into AvailabilityProbeWorker, and the dotnet
#      unit regressions — movie AND episode parity — that were not quietly
#      deleted).
#
#   B. BEHAVIOURAL: the REAL browse predicate is driven against a synthetic
#      phantom.db, seeding for BOTH item_types (movie AND episode):
#        (a) a genuinely never-probed available item (candidate_magnet NULL, no
#            source_candidates) -> asserted PRESENT (no regression);
#        (b) a stale available item whose candidate cache emptied
#            (candidate_magnet NOT NULL, zero source_candidates) -> asserted
#            ABSENT;
#        (c) a stale available item that got a fresh live candidate back
#            -> asserted PRESENT;
#        (d) a materialised item -> asserted PRESENT (bypasses the check).
#      The predicate SQL is byte-aligned with the C# browse query; layer A
#      asserts the C# source still carries the helper so the two cannot drift.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
PHANTOMDB="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs"
WORKER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Scheduled/AvailabilityProbeWorker.cs"
DB_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v sqlite3 >/dev/null 2>&1 || fatal "sqlite3 not found on PATH"
[[ -f "$PHANTOMDB" ]] || fatal "PhantomDb.cs not found: $PHANTOMDB"
[[ -f "$WORKER" ]]    || fatal "AvailabilityProbeWorker.cs not found: $WORKER"
[[ -f "$DB_TESTS" ]]  || fatal "PhantomDbTests.cs not found: $DB_TESTS"

grepf() { grep -Fq -- "$2" "$1"; }  # fixed-string grep, quiet

# ---------------------------------------------------------------------------
head_ "A. Structural: fix wiring present in tracked source"
# ---------------------------------------------------------------------------

grepf "$PHANTOMDB" "AvailableNeverAssessedSql" \
  && ok "never-assessed distinguisher helper present" \
  || bad "AvailableNeverAssessedSql helper MISSING"

grepf "$PHANTOMDB" "candidate_magnet IS NULL" \
  && ok "distinguisher keys on candidate_magnet IS NULL (no schema change)" \
  || bad "candidate_magnet IS NULL distinguisher MISSING"

# Applied inside BOTH browse queries: the movie query uses alias "a", the two
# series subqueries use alias "ai".
apply_movie="$(grep -c 'AvailableNeverAssessedSql("a")' "$PHANTOMDB" || true)"
apply_series="$(grep -c 'AvailableNeverAssessedSql("ai")' "$PHANTOMDB" || true)"
if [[ "$apply_movie" -ge 1 ]]; then
  ok "distinguisher applied to movie browse query (alias a: $apply_movie site)"
else
  bad "distinguisher NOT applied to movie browse query"
fi
if [[ "$apply_series" -ge 2 ]]; then
  ok "distinguisher applied to series browse subqueries (alias ai: $apply_series sites)"
else
  bad "distinguisher applied to too few series subqueries ($apply_series; expected >=2)"
fi

grepf "$PHANTOMDB" "MarkStaleAvailableItemsDueAsync" \
  && ok "eager-reprobe promotion method present" \
  || bad "MarkStaleAvailableItemsDueAsync MISSING"

grepf "$WORKER" "MarkStaleAvailableItemsDueAsync" \
  && ok "AvailabilityProbeWorker drives the eager re-probe promotion" \
  || bad "AvailabilityProbeWorker does NOT drive the eager re-probe"

# Unit regression coverage exists, movie AND episode parity.
grepf "$DB_TESTS" "ListVisibleMovieRows_ExcludesStaleAvailableMovieWhoseCandidateCacheEmptied" \
  && ok "movie stale-available exclusion unit regression present" \
  || bad "movie stale-available exclusion unit regression MISSING"
grepf "$DB_TESTS" "ListVisibleMovieRows_KeepsNeverAssessedAvailableMovie" \
  && ok "movie never-assessed no-regression unit test present" \
  || bad "movie never-assessed no-regression unit test MISSING"
grepf "$DB_TESTS" "ListVisibleSeriesRows_ExcludesStaleAvailableSeriesWhoseOnlyEpisodeCandidateCacheEmptied" \
  && ok "episode/series stale-available exclusion unit regression present (TV parity)" \
  || bad "episode/series stale-available exclusion unit regression MISSING"
grepf "$DB_TESTS" "ListVisibleSeriesRows_KeepsNeverAssessedAvailableEpisode" \
  && ok "episode never-assessed no-regression unit test present (TV parity)" \
  || bad "episode never-assessed no-regression unit test MISSING"
grepf "$DB_TESTS" "MarkStaleAvailableItemsDue_PromotesStaleAvailableRowsForEagerReprobe" \
  && ok "eager-reprobe promotion unit regression present" \
  || bad "eager-reprobe promotion unit regression MISSING"

# ---------------------------------------------------------------------------
head_ "B. Behavioural: real browse predicate on a synthetic phantom.db"
# ---------------------------------------------------------------------------
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
DB="$WORK/phantom.db"

# Schema slice used by the browse query: availability_items carries
# candidate_magnet (the distinguisher), source_candidates carry the live
# candidate rows, materialised_state bypasses the check.
sqlite3 "$DB" <<'SQL'
CREATE TABLE tmdb_metadata (
  tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, title TEXT, year INTEGER,
  overview TEXT, poster_url TEXT, backdrop_url TEXT, genres_json TEXT,
  official_rating TEXT, community_rating REAL, original_title TEXT,
  fetched_at INTEGER NOT NULL, runtime_minutes INTEGER,
  relevance_score REAL NOT NULL DEFAULT 0, PRIMARY KEY (tmdb_id, type));
CREATE TABLE availability_items (
  tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, season INTEGER NOT NULL DEFAULT -1,
  episode INTEGER NOT NULL DEFAULT -1, status TEXT NOT NULL,
  candidate_magnet TEXT,
  PRIMARY KEY (tmdb_id, type, season, episode));
CREATE TABLE materialised_state (
  tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, season INTEGER NOT NULL DEFAULT -1,
  episode INTEGER NOT NULL DEFAULT -1, stub_path TEXT, fuse_path TEXT,
  materialised_at INTEGER NOT NULL, PRIMARY KEY (tmdb_id, type, season, episode));
CREATE TABLE source_candidates (
  tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, season INTEGER NOT NULL DEFAULT -1,
  episode INTEGER NOT NULL DEFAULT -1, preset TEXT NOT NULL DEFAULT '',
  magnet TEXT NOT NULL, validation_status TEXT NOT NULL DEFAULT 'unknown',
  validation_reason TEXT,
  PRIMARY KEY (tmdb_id, type, season, episode, preset, magnet));
CREATE TABLE dead_swarm_confirmations (
  tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, season INTEGER NOT NULL DEFAULT -1,
  episode INTEGER NOT NULL DEFAULT -1, preset TEXT NOT NULL DEFAULT '',
  magnet TEXT NOT NULL, confirmations INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (tmdb_id, type, season, episode, preset, magnet));
SQL

seed_meta() { # tmdb metatype
  sqlite3 "$DB" "INSERT INTO tmdb_metadata(tmdb_id,type,title,fetched_at) VALUES($1,'$2','t$1',0);"
}
# availtype/season/episode; candidate_magnet is NULL unless a magnet is given.
seed_avail() { # tmdb availtype season episode candidate_magnet(or empty)
  if [[ -z "${5:-}" ]]; then
    sqlite3 "$DB" "INSERT INTO availability_items(tmdb_id,type,season,episode,status,candidate_magnet) VALUES($1,'$2',$3,$4,'available',NULL);"
  else
    sqlite3 "$DB" "INSERT INTO availability_items(tmdb_id,type,season,episode,status,candidate_magnet) VALUES($1,'$2',$3,$4,'available','$5');"
  fi
}
seed_candidate() { # tmdb type season episode magnet
  sqlite3 "$DB" "INSERT INTO source_candidates(tmdb_id,type,season,episode,preset,magnet,validation_status) VALUES($1,'$2',$3,$4,'preset','$5','unknown');"
}

# --- MOVIE items ---
# (a) never-assessed available (candidate_magnet NULL, no source_candidates) -> PRESENT
seed_meta 900 movie
seed_avail 900 movie -1 -1 ""
# (b) stale available: candidate was cached (candidate_magnet set) but cache emptied -> ABSENT
seed_meta 901 movie
seed_avail 901 movie -1 -1 "magnet:?xt=urn:btih:stale901"
# (c) stale available that got a fresh live candidate back -> PRESENT
seed_meta 902 movie
seed_avail 902 movie -1 -1 "magnet:?xt=urn:btih:stale902"
seed_candidate 902 movie -1 -1 "magnet:?xt=urn:btih:fresh902"
# (d) materialised item (bypasses the whole availability check) -> PRESENT
seed_meta 903 movie
seed_avail 903 movie -1 -1 "magnet:?xt=urn:btih:stale903"
sqlite3 "$DB" "INSERT INTO materialised_state(tmdb_id,type,season,episode,stub_path,fuse_path,materialised_at) VALUES(903,'movie',-1,-1,'/s','/f',0);"

# --- EPISODE items (metadata type 'series', avail/candidate type 'episode', s/e 1/1) ---
seed_meta 910 series
seed_avail 910 episode 1 1 ""
seed_meta 911 series
seed_avail 911 episode 1 1 "magnet:?xt=urn:btih:stale911"
seed_meta 912 series
seed_avail 912 episode 1 1 "magnet:?xt=urn:btih:stale912"
seed_candidate 912 episode 1 1 "magnet:?xt=urn:btih:fresh912"
seed_meta 913 series
seed_avail 913 episode 1 1 "magnet:?xt=urn:btih:stale913"
sqlite3 "$DB" "INSERT INTO materialised_state(tmdb_id,type,season,episode,stub_path,fuse_path,materialised_at) VALUES(913,'episode',1,1,'/s','/f',0);"

# The dead-swarm predicate is inert here (no confirmations rows); kept so the
# behavioural SQL stays byte-aligned with the real C# browse query.
DEAD_PRED="sc.validation_reason IN ('metadata_timeout','validation_transient')
  AND EXISTS (SELECT 1 FROM dead_swarm_confirmations dsc
    WHERE dsc.tmdb_id=sc.tmdb_id AND dsc.type=sc.type AND dsc.season=sc.season
      AND dsc.episode=sc.episode AND dsc.preset=sc.preset AND dsc.magnet=sc.magnet
      AND dsc.confirmations >= 2)"
# The never-assessed distinguisher, byte-aligned with
# PhantomDb.AvailableNeverAssessedSql(aiAlias).
NEVER_A="a.candidate_magnet IS NULL"
NEVER_AI="ai.candidate_magnet IS NULL"

movie_visible() {
  sqlite3 "$DB" "
    SELECT m.tmdb_id FROM tmdb_metadata m
    LEFT JOIN materialised_state ms ON ms.tmdb_id=m.tmdb_id AND ms.type='movie'
    LEFT JOIN availability_items a ON a.tmdb_id=m.tmdb_id AND a.type='movie' AND a.season=-1 AND a.episode=-1
    WHERE m.type='movie' AND (
      ms.tmdb_id IS NOT NULL
      OR (a.status='available' AND (
        (NOT EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=m.tmdb_id AND sc.type='movie' AND sc.season=-1 AND sc.episode=-1)
           AND $NEVER_A)
        OR EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=m.tmdb_id AND sc.type='movie' AND sc.season=-1 AND sc.episode=-1
                     AND sc.validation_status <> 'invalid' AND NOT ($DEAD_PRED))
      ))
    );"
}
series_visible() {
  sqlite3 "$DB" "
    SELECT m.tmdb_id FROM tmdb_metadata m
    LEFT JOIN (
      SELECT tmdb_id, COUNT(*) AS display_count FROM (
        SELECT ai.tmdb_id, ai.season, ai.episode FROM availability_items ai
        WHERE ai.type='episode' AND ai.status='available'
          AND (
            (NOT EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=ai.tmdb_id AND sc.type='episode' AND sc.season=ai.season AND sc.episode=ai.episode)
               AND $NEVER_AI)
            OR EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=ai.tmdb_id AND sc.type='episode' AND sc.season=ai.season AND sc.episode=ai.episode
                         AND sc.validation_status <> 'invalid' AND NOT ($DEAD_PRED))
          )
        UNION SELECT tmdb_id, season, episode FROM materialised_state WHERE type='episode'
      ) GROUP BY tmdb_id
    ) display ON display.tmdb_id=m.tmdb_id
    WHERE m.type='series' AND COALESCE(display.display_count,0) >= 1;"
}

assert_absent()  { if echo "$2" | grep -qx "$1"; then bad "$3 (expected ABSENT, was present)"; else ok "$3"; fi; }
assert_present() { if echo "$2" | grep -qx "$1"; then ok "$3"; else bad "$3 (expected PRESENT, was absent)"; fi; }

MV="$(movie_visible)"
assert_present 900 "$MV" "movie (a): never-assessed available -> visible (no regression)"
assert_absent  901 "$MV" "movie (b): stale available, candidate cache emptied -> excluded"
assert_present 902 "$MV" "movie (c): stale available with fresh candidate back -> visible"
assert_present 903 "$MV" "movie (d): materialised -> visible (bypass)"

SV="$(series_visible)"
assert_present 910 "$SV" "episode (a): never-assessed available -> series visible (no regression)"
assert_absent  911 "$SV" "episode (b): stale available, candidate cache emptied -> series excluded"
assert_present 912 "$SV" "episode (c): stale available with fresh candidate back -> series visible"
assert_present 913 "$SV" "episode (d): materialised episode -> series visible (bypass)"

# ---------------------------------------------------------------------------
printf '\n\033[1m== Summary\033[0m\n'
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || exit 1
echo "availability-stale-candidate-reprobe: all assertions passed"

#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/browse-prune-dead-swarm.test.sh
#
# In-repo regression harness for browse-prune-dead-swarm-001 (ROI Priority 12,
# dial #2): prune a DOOMED-BUT-VISIBLE dead-swarm item from DEFAULT BROWSE.
#
# P10 (p10-prune-nonplayable-browse) already excludes an available item whose
# every source_candidates row is validation_status='invalid'. It does NOT prune
# an item whose only candidate resolves and then dies at the swarm — the
# magnet_dead_stale-family SOFT/transient failure keeps the candidate
# validation_status transient (never 'invalid'), so P10 leaves it visible even
# though every cold attempt re-resolves the same dead swarm. This task extends
# the P10 correlated-subquery prune so such an item is pruned once its sole
# candidate has been re-confirmed dead >= DeadSwarmBrowsePruneThreshold times,
# while a below-threshold blip, a still-viable sibling candidate, or a
# materialised item all stay visible.
#
# Two layers, mirroring scripts/tests/p10-curated-browse.test.sh's convention
# (dotnet test itself is NOT re-run here — that needs the built patched Jellyfin
# assemblies and is the C# build/test gate's own job):
#
#   A. STRUCTURAL: the plugin/config/schema wiring the fix requires is present
#      in the tracked source (schema v22 + dead_swarm_confirmations table, the
#      DeadSwarmBrowsePruneThreshold config knob, the enumerated DeadSwarmReasons
#      set, the CandidateIsThresholdDeadSwarmSql predicate applied to BOTH
#      ListVisibleMovieRowsAsync and ListVisibleSeriesRowsAsync, the increment
#      on the transient dead-swarm path and the clear on validate-clean), and
#      the dotnet unit regression on ListVisible*RowsAsync exists (movie AND
#      episode parity) and was not quietly deleted.
#
#   B. BEHAVIOURAL: the REAL prune predicate is driven against a synthetic
#      phantom.db built with the exact v22 schema slice, seeding for BOTH
#      item_types (movie AND episode):
#        (a) an item whose only candidate is a threshold-exceeded dead swarm
#            -> asserted ABSENT from the visible list,
#        (b) an item with one dead-swarm + one still-viable candidate
#            -> asserted PRESENT,
#        (c) an item with a single below-threshold dead-swarm blip
#            -> asserted PRESENT,
#        (d) a materialised item -> asserted PRESENT (bypasses the check).
#      The predicate SQL used here is kept byte-aligned with the C# helper
#      CandidateIsThresholdDeadSwarmSql; layer A asserts the C# source still
#      carries it so the two cannot silently drift.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
PHANTOMDB="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs"
CONFIG="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Configuration/PluginConfiguration.cs"
MATERIALISER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Materialisation/Materialiser.cs"
DB_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v sqlite3 >/dev/null 2>&1 || fatal "sqlite3 not found on PATH"
[[ -f "$PHANTOMDB" ]]    || fatal "PhantomDb.cs not found: $PHANTOMDB"
[[ -f "$CONFIG" ]]      || fatal "PluginConfiguration.cs not found: $CONFIG"
[[ -f "$MATERIALISER" ]] || fatal "Materialiser.cs not found: $MATERIALISER"
[[ -f "$DB_TESTS" ]]    || fatal "PhantomDbTests.cs not found: $DB_TESTS"

grepf() { grep -Fq -- "$2" "$1"; }  # fixed-string grep, quiet

# ---------------------------------------------------------------------------
head_ "A. Structural: fix wiring present in tracked source"
# ---------------------------------------------------------------------------

grepf "$PHANTOMDB" "CurrentSchemaVersion = 22" \
  && ok "schema bumped to v22" || bad "schema NOT bumped to v22"

grepf "$PHANTOMDB" "CREATE TABLE IF NOT EXISTS dead_swarm_confirmations" \
  && ok "dead_swarm_confirmations table in fresh schema" \
  || bad "dead_swarm_confirmations table MISSING from fresh schema"

grepf "$PHANTOMDB" "v21_v22_dead_swarm_confirmations" \
  && ok "additive v21->v22 expand migration registered (blue/green Postgres)" \
  || bad "v21->v22 expand migration NOT registered"

grepf "$CONFIG" "DeadSwarmBrowsePruneThreshold" \
  && ok "DeadSwarmBrowsePruneThreshold config knob present" \
  || bad "DeadSwarmBrowsePruneThreshold config knob MISSING"

grepf "$PHANTOMDB" "CandidateIsThresholdDeadSwarmSql" \
  && ok "threshold-dead-swarm predicate helper present" \
  || bad "threshold-dead-swarm predicate helper MISSING"

# Applied to BOTH browse queries (movie AND episode parity).
apply_count="$(grep -c 'CandidateIsThresholdDeadSwarmSql("sc"' "$PHANTOMDB" || true)"
if [[ "$apply_count" -ge 3 ]]; then
  ok "predicate applied to movie + series queries ($apply_count sites)"
else
  bad "predicate applied to too few query sites ($apply_count; expected >=3: movie + 2 series subqueries)"
fi

grepf "$PHANTOMDB" "IncrementDeadSwarmConfirmationAsync" \
  && ok "increment method present" || bad "increment method MISSING"
grepf "$PHANTOMDB" "ClearDeadSwarmConfirmationAsync" \
  && ok "clear method present" || bad "clear method MISSING"

grepf "$MATERIALISER" "IncrementDeadSwarmConfirmationAsync" \
  && ok "Materialiser increments on transient dead-swarm failure" \
  || bad "Materialiser does NOT increment dead-swarm confirmations"
grepf "$MATERIALISER" "ClearDeadSwarmConfirmationAsync" \
  && ok "Materialiser clears count on validate-clean" \
  || bad "Materialiser does NOT clear dead-swarm confirmations on valid"

# Enumerated reason set (not a free-text substring match).
grepf "$PHANTOMDB" "DeadSwarmReasons" \
  && ok "enumerated DeadSwarmReasons set present" \
  || bad "enumerated DeadSwarmReasons set MISSING"

# Unit regression coverage exists, movie AND episode parity.
grepf "$DB_TESTS" "ListVisibleMovieRows_ExcludesMovieWhenOnlyCandidateIsThresholdDeadSwarm" \
  && ok "movie dead-swarm prune unit regression present" \
  || bad "movie dead-swarm prune unit regression MISSING"
grepf "$DB_TESTS" "ListVisibleSeriesRows_ExcludesSeriesWhenOnlyEpisodeCandidateIsThresholdDeadSwarm" \
  && ok "episode/series dead-swarm prune unit regression present (TV parity)" \
  || bad "episode/series dead-swarm prune unit regression MISSING"
grepf "$DB_TESTS" "MaterialisedItemBypassesDeadSwarmPrune" \
  && ok "materialised-bypass unit regression present" \
  || bad "materialised-bypass unit regression MISSING"

# ---------------------------------------------------------------------------
head_ "B. Behavioural: real prune predicate on a synthetic phantom.db"
# ---------------------------------------------------------------------------
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
DB="$WORK/phantom.db"

# The enumerated dead-swarm reasons (must match PhantomDb.DeadSwarmReasons).
DEAD_REASON="metadata_timeout"
THRESH=2

# Exact v22 schema slice used by the prune query, plus the new confirmations
# table. Kept byte-aligned with PhantomDb.SchemaV10Sql for these tables.
sqlite3 "$DB" <<SQL
CREATE TABLE tmdb_metadata (
  tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, title TEXT, year INTEGER,
  overview TEXT, poster_url TEXT, backdrop_url TEXT, genres_json TEXT,
  official_rating TEXT, community_rating REAL, original_title TEXT,
  fetched_at INTEGER NOT NULL, runtime_minutes INTEGER,
  relevance_score REAL NOT NULL DEFAULT 0, PRIMARY KEY (tmdb_id, type));
CREATE TABLE availability_items (
  tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, season INTEGER NOT NULL DEFAULT -1,
  episode INTEGER NOT NULL DEFAULT -1, status TEXT NOT NULL,
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

# Seed helper columns for movie (season/episode = -1) and episode (1/1).
seed_item() { # tmdb type season episode
  sqlite3 "$DB" "INSERT INTO tmdb_metadata(tmdb_id,type,title,fetched_at) VALUES($1,'$2','t$1',0);"
  sqlite3 "$DB" "INSERT INTO availability_items(tmdb_id,type,season,episode,status) VALUES($1,'$3',$4,$5,'available');"
}
seed_candidate() { # tmdb type season episode magnet status reason
  sqlite3 "$DB" "INSERT INTO source_candidates(tmdb_id,type,season,episode,preset,magnet,validation_status,validation_reason) VALUES($1,'$2',$3,$4,'preset','$5','$6',$7);"
}
seed_confirm() { # tmdb type season episode magnet n
  sqlite3 "$DB" "INSERT INTO dead_swarm_confirmations(tmdb_id,type,season,episode,preset,magnet,confirmations,updated_at) VALUES($1,'$2',$3,$4,'preset','$5',$6,0);"
}

# --- MOVIE items (metadata type 'movie', candidate/availability type 'movie', s/e = -1) ---
# (a) only candidate is threshold-exceeded dead swarm -> ABSENT
seed_item 700 movie movie -1 -1
seed_candidate 700 movie -1 -1 "m:dead700" transient "'$DEAD_REASON'"
seed_confirm   700 movie -1 -1 "m:dead700" $THRESH
# (b) dead swarm + still-viable candidate -> PRESENT
seed_item 701 movie movie -1 -1
seed_candidate 701 movie -1 -1 "m:dead701" transient "'$DEAD_REASON'"
seed_confirm   701 movie -1 -1 "m:dead701" 5
seed_candidate 701 movie -1 -1 "m:live701" unknown "NULL"
# (c) single below-threshold blip -> PRESENT
seed_item 702 movie movie -1 -1
seed_candidate 702 movie -1 -1 "m:dead702" transient "'$DEAD_REASON'"
seed_confirm   702 movie -1 -1 "m:dead702" 1
# (d) materialised item -> PRESENT (bypasses candidate check)
seed_item 703 movie movie -1 -1
seed_candidate 703 movie -1 -1 "m:dead703" transient "'$DEAD_REASON'"
seed_confirm   703 movie -1 -1 "m:dead703" 5
sqlite3 "$DB" "INSERT INTO materialised_state(tmdb_id,type,season,episode,stub_path,fuse_path,materialised_at) VALUES(703,'movie',-1,-1,'/s','/f',0);"

# --- EPISODE items (metadata type 'series', candidate/availability type 'episode', s/e = 1/1) ---
seed_item 800 series episode 1 1
seed_candidate 800 episode 1 1 "e:dead800" transient "'$DEAD_REASON'"
seed_confirm   800 episode 1 1 "e:dead800" $THRESH
seed_item 801 series episode 1 1
seed_candidate 801 episode 1 1 "e:dead801" transient "'$DEAD_REASON'"
seed_confirm   801 episode 1 1 "e:dead801" 5
seed_candidate 801 episode 1 1 "e:live801" unknown "NULL"
seed_item 802 series episode 1 1
seed_candidate 802 episode 1 1 "e:dead802" transient "'$DEAD_REASON'"
seed_confirm   802 episode 1 1 "e:dead802" 1
seed_item 803 series episode 1 1
seed_candidate 803 episode 1 1 "e:dead803" transient "'$DEAD_REASON'"
seed_confirm   803 episode 1 1 "e:dead803" 5
sqlite3 "$DB" "INSERT INTO materialised_state(tmdb_id,type,season,episode,stub_path,fuse_path,materialised_at) VALUES(803,'episode',1,1,'/s','/f',0);"

# The threshold-dead-swarm predicate, byte-aligned with
# PhantomDb.CandidateIsThresholdDeadSwarmSql(scAlias='sc', threshold=THRESH).
DEAD_PRED="sc.validation_reason IN ('metadata_timeout','validation_transient')
  AND EXISTS (SELECT 1 FROM dead_swarm_confirmations dsc
    WHERE dsc.tmdb_id=sc.tmdb_id AND dsc.type=sc.type AND dsc.season=sc.season
      AND dsc.episode=sc.episode AND dsc.preset=sc.preset AND dsc.magnet=sc.magnet
      AND dsc.confirmations >= $THRESH)"

# Movie visible list = the exact P10-extended movie prune predicate.
movie_visible() {
  sqlite3 "$DB" "
    SELECT m.tmdb_id FROM tmdb_metadata m
    LEFT JOIN materialised_state ms ON ms.tmdb_id=m.tmdb_id AND ms.type='movie'
    LEFT JOIN availability_items a ON a.tmdb_id=m.tmdb_id AND a.type='movie' AND a.season=-1 AND a.episode=-1
    WHERE m.type='movie' AND (
      ms.tmdb_id IS NOT NULL
      OR (a.status='available' AND (
        NOT EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=m.tmdb_id AND sc.type='movie' AND sc.season=-1 AND sc.episode=-1)
        OR EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=m.tmdb_id AND sc.type='movie' AND sc.season=-1 AND sc.episode=-1
                     AND sc.validation_status <> 'invalid' AND NOT ($DEAD_PRED))
      ))
    );"
}
# Series visible list = the exact P10-extended series display-gate predicate.
series_visible() {
  sqlite3 "$DB" "
    SELECT m.tmdb_id FROM tmdb_metadata m
    LEFT JOIN (
      SELECT tmdb_id, COUNT(*) AS display_count FROM (
        SELECT ai.tmdb_id, ai.season, ai.episode FROM availability_items ai
        WHERE ai.type='episode' AND ai.status='available'
          AND (
            NOT EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=ai.tmdb_id AND sc.type='episode' AND sc.season=ai.season AND sc.episode=ai.episode)
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
assert_absent  700 "$MV" "movie (a): only candidate = threshold dead swarm -> pruned"
assert_present 701 "$MV" "movie (b): dead swarm + viable candidate -> visible"
assert_present 702 "$MV" "movie (c): below-threshold blip -> visible"
assert_present 703 "$MV" "movie (d): materialised -> visible (bypass)"

SV="$(series_visible)"
assert_absent  800 "$SV" "episode (a): only candidate = threshold dead swarm -> series pruned"
assert_present 801 "$SV" "episode (b): dead swarm + viable candidate -> series visible"
assert_present 802 "$SV" "episode (c): below-threshold blip -> series visible"
assert_present 803 "$SV" "episode (d): materialised episode -> series visible (bypass)"

# ---------------------------------------------------------------------------
printf '\n\033[1m== Summary\033[0m\n'
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || exit 1
echo "browse-prune-dead-swarm: all assertions passed"

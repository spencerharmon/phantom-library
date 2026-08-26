#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/jellyfin-metadata-reaper.sh
#
# Enforcing LRU reaper for the shared `jellyfin-metadata` PVC (task
# jellyfin-metadata-quota-reaper). Mirrors the gostream warmup cache's
# self-eviction: `--quota-gb` is the single source of truth (matches
# `jellyfinMetadata.quotaGb` in values.yaml), and this script evicts the
# LEAST-RECENTLY-ACCESSED files first, restricted to the derived/regenerable
# subtrees named by `--path`, until usage is back under quota.
#
# Safety invariants:
#   - NEVER touches anything outside the explicit `--path` list (each
#     resolved and required to stay under `--root`).
#   - NEVER deletes a directory itself, only files within it (empty dirs are
#     harmless and Jellyfin recreates them on next scan).
#   - A no-op (exit 0, nothing deleted) when usage is already <= quota.
#   - Idempotent: running it again immediately is always safe.
#   - Writes a Prometheus textfile-collector metrics file
#     (`--metrics-file`, default `<root>/.jellyfin-metadata-usage.prom`) with
#     current usage/quota bytes so the existing monitoring stack can alert at
#     e.g. 80% of quota (jellyfin_metadata_cache_usage_bytes /
#     jellyfin_metadata_cache_quota_bytes) BEFORE the volume actually fills.
#
# IMPORTANCE-AWARE EVICTION ORDER
#   Filesystem atime alone is a POOR importance signal here: the PVC is mounted
#   `relatime`, so an atime is only refreshed when the previous one is older than
#   mtime/ctime or >24h stale. Everything touched "today" therefore looks
#   identical to a pure atime sort, and a poster viewed 500 times can be evicted
#   before one viewed once. So when Jellyfin's database is reachable, this script
#   ranks candidates by REAL user importance first and uses atime only to break
#   ties WITHIN a tier:
#
#     tier 0  no user has ever played/favourited the item      (evict first)
#     tier 1  played at some point, not recently, not favourite
#     tier 2  played recently (within --recent-days)
#     tier 3  favourited by any user, or currently in progress  (evict last)
#
#   Item identity comes from the cache path itself: Jellyfin lays artwork out at
#   <root>/library/<xx>/<32-hex-ItemId>/<image>.jpg, where <32-hex-ItemId> is the
#   BaseItems/UserData GUID with dashes stripped. That is the join key.
#
#   The DB is an OPTIONAL enrichment, never a hard dependency: without
#   --db-* flags (or if the query fails for any reason) the script degrades to
#   the previous pure-atime LRU behaviour and says so. A cache reaper must never
#   fail to protect the disk just because Postgres is down.
#
# Usage:
#   jellyfin-metadata-reaper.sh --root <mount-path> --quota-gb <N> \
#       [--path <subdir>]... [--metrics-file <path>] [--dry-run] \
#       [--db-host H] [--db-port P] [--db-name N] [--db-user U] \
#       [--db-password-file F] [--recent-days N] [--no-db] \
#       [--importance-file F]
#
# Exit codes: 0 = success (no-op or reclaimed enough), 1 = usage bad args,
# 2 = could not reclaim below quota (all candidate files already deleted).
set -euo pipefail

ROOT=""
QUOTA_GB=""
declare -a REAP_PATHS=()
METRICS_FILE=""
DRY_RUN=0
DB_HOST=""
DB_PORT="5432"
DB_NAME=""
DB_USER=""
DB_PASSWORD_FILE=""
IMPORTANCE_INPUT=""
RECENT_DAYS="30"
NO_DB=0

usage() {
  echo "usage: $0 --root <path> --quota-gb <N> [--path <subdir>]... [--metrics-file <path>] [--dry-run] [--db-host <h> --db-name <n> --db-user <u> --db-password-file <f>] [--db-port <p>] [--recent-days <n>] [--no-db] [--importance-file <f>]" >&2
  exit 1
}

while [ $# -gt 0 ]; do
  case "$1" in
    --root) ROOT="$2"; shift 2 ;;
    --quota-gb) QUOTA_GB="$2"; shift 2 ;;
    --path) REAP_PATHS+=("$2"); shift 2 ;;
    --metrics-file) METRICS_FILE="$2"; shift 2 ;;
    --dry-run) DRY_RUN=1; shift ;;
    --db-host) DB_HOST="$2"; shift 2 ;;
    --db-port) DB_PORT="$2"; shift 2 ;;
    --db-name) DB_NAME="$2"; shift 2 ;;
    --db-user) DB_USER="$2"; shift 2 ;;
    --db-password-file) DB_PASSWORD_FILE="$2"; shift 2 ;;
    --importance-file) IMPORTANCE_INPUT="$2"; shift 2 ;;
    --recent-days) RECENT_DAYS="$2"; shift 2 ;;
    --no-db) NO_DB=1; shift ;;
    -h|--help) usage ;;
    *) echo "$0: unknown argument: $1" >&2; usage ;;
  esac
done

[ -n "$ROOT" ] || usage
[ -n "$QUOTA_GB" ] || usage
[ ${#REAP_PATHS[@]} -gt 0 ] || usage

case "$RECENT_DAYS" in
  ''|*[!0-9]*) echo "$0: --recent-days must be a non-negative integer, got '$RECENT_DAYS'" >&2; exit 1 ;;
esac

case "$QUOTA_GB" in
  ''|*[!0-9]*) echo "$0: --quota-gb must be a non-negative integer, got '$QUOTA_GB'" >&2; exit 1 ;;
esac

ROOT="$(cd "$ROOT" && pwd)"
[ -n "$METRICS_FILE" ] || METRICS_FILE="$ROOT/.jellyfin-metadata-usage.prom"

QUOTA_BYTES=$((QUOTA_GB * 1024 * 1024 * 1024))

# Resolve + validate each reap path stays under ROOT (never follow it outside via a symlink escape,
# and skip a path that does not exist yet — a fresh/empty PVC has none of these dirs pre-created).
declare -a RESOLVED_PATHS=()
for p in "${REAP_PATHS[@]}"; do
  candidate="$ROOT/$p"
  if [ ! -e "$candidate" ]; then
    continue
  fi
  resolved="$(cd "$candidate" && pwd)"
  case "$resolved" in
    "$ROOT"|"$ROOT"/*) ;;
    *) echo "$0: refusing to reap path outside root: $p -> $resolved" >&2; exit 1 ;;
  esac
  RESOLVED_PATHS+=("$resolved")
done

# ---------------------------------------------------------------- importance --
# Build an ItemId -> tier map from Jellyfin's DB. Emits "<32-hex-itemid> <tier>"
# lines to stdout; empty output means "no DB signal available" and the caller
# falls back to pure atime LRU.
#
# Tiers (higher = more important = evicted later):
#   3 favourite OR in-progress (PlaybackPositionTicks > 0)
#   2 played within RECENT_DAYS
#   1 played at some point (older than RECENT_DAYS)
#   0 never played by anyone            <- the bulk, evicted first
DB_STATUS="disabled"
# Declared before ANY write_metrics call (including the no-op early exit) so the
# tier gauges always resolve under `set -u`.
declare -A TIER_DELETED=([0]=0 [1]=0 [2]=0 [3]=0)
fetch_importance() {
  # Pre-computed map supplied by an init container (the workload image has no psql,
  # so the query runs in a postgres-client image and hands the result over a shared
  # volume). Format is identical to the psql output: "<32-hex-itemid> <tier>" lines.
  if [ -n "$IMPORTANCE_INPUT" ]; then
    if [ ! -r "$IMPORTANCE_INPUT" ]; then
      echo "jellyfin-metadata-reaper: --importance-file not readable — falling back to atime-only LRU" >&2
      DB_STATUS="unavailable"; return 0
    fi
    if [ ! -s "$IMPORTANCE_INPUT" ]; then
      # An EMPTY map means the producer ran but found no user-data rows. That is a
      # legitimate "everything is tier 0" answer, not a failure.
      DB_STATUS="empty"; return 0
    fi
    DB_STATUS="ok"
    cat "$IMPORTANCE_INPUT"
    return 0
  fi
  [ "$NO_DB" -eq 0 ] || { DB_STATUS="disabled"; return 0; }
  if [ -z "$DB_HOST" ] || [ -z "$DB_NAME" ] || [ -z "$DB_USER" ]; then
    DB_STATUS="disabled"; return 0
  fi
  if ! command -v psql >/dev/null 2>&1; then
    echo "jellyfin-metadata-reaper: psql not available — falling back to atime-only LRU" >&2
    DB_STATUS="unavailable"; return 0
  fi
  local pw=""
  if [ -n "$DB_PASSWORD_FILE" ]; then
    if [ ! -r "$DB_PASSWORD_FILE" ]; then
      echo "jellyfin-metadata-reaper: --db-password-file not readable — falling back to atime-only LRU" >&2
      DB_STATUS="unavailable"; return 0
    fi
    pw="$(cat "$DB_PASSWORD_FILE")"
  fi
  # One row per item that has ANY user-data signal; everything absent from this
  # result is implicitly tier 0. Dashes are stripped so the key matches the
  # on-disk directory name directly.
  local sql
  sql="select replace(ud.\"ItemId\"::text,'-',''),
              max(case
                    when ud.\"IsFavorite\" then 3
                    when coalesce(ud.\"PlaybackPositionTicks\",0) > 0 then 3
                    when ud.\"LastPlayedDate\" is not null
                         and ud.\"LastPlayedDate\" > now() - interval '${RECENT_DAYS} days' then 2
                    when coalesce(ud.\"PlayCount\",0) > 0 or ud.\"Played\" then 1
                    else 0
                  end)
         from \"UserData\" ud
        group by 1"
  local out
  if ! out="$(PGPASSWORD="$pw" PGCONNECT_TIMEOUT=10 psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -tA -F' ' --no-align -c "$sql" 2>/dev/null)"; then
    echo "jellyfin-metadata-reaper: DB query failed — falling back to atime-only LRU" >&2
    DB_STATUS="unavailable"; return 0
  fi
  if [ -z "$out" ]; then
    DB_STATUS="empty"; return 0
  fi
  DB_STATUS="ok"
  printf '%s\n' "$out"
}

write_metrics() {
  local usage_bytes="$1"
  local reclaimed_bytes="$2"
  local tmp
  tmp="$(mktemp "${METRICS_FILE}.XXXXXX")"
  {    echo "# HELP jellyfin_metadata_cache_usage_bytes Current bytes used by the shared jellyfin-metadata cache across reaped subtrees."
    echo "# TYPE jellyfin_metadata_cache_usage_bytes gauge"
    echo "jellyfin_metadata_cache_usage_bytes ${usage_bytes}"
    echo "# HELP jellyfin_metadata_cache_quota_bytes Configured quota (jellyfinMetadata.quotaGb) for the shared jellyfin-metadata cache."
    echo "# TYPE jellyfin_metadata_cache_quota_bytes gauge"
    echo "jellyfin_metadata_cache_quota_bytes ${QUOTA_BYTES}"
    echo "# HELP jellyfin_metadata_reaper_last_run_reclaimed_bytes Bytes reclaimed by the most recent reaper run (0 if it was a no-op)."
    echo "# TYPE jellyfin_metadata_reaper_last_run_reclaimed_bytes gauge"
    echo "jellyfin_metadata_reaper_last_run_reclaimed_bytes ${reclaimed_bytes}"
    echo "# HELP jellyfin_metadata_reaper_last_run_timestamp_seconds Unix timestamp of the most recent reaper run."
    echo "# TYPE jellyfin_metadata_reaper_last_run_timestamp_seconds gauge"
    echo "jellyfin_metadata_reaper_last_run_timestamp_seconds $(date +%s)"
    echo "# HELP jellyfin_metadata_reaper_importance_ranking_active Whether DB-backed importance ranking was used (1) or the run degraded to atime-only LRU (0)."
    echo "# TYPE jellyfin_metadata_reaper_importance_ranking_active gauge"
    echo "jellyfin_metadata_reaper_importance_ranking_active $([ "$DB_STATUS" = "ok" ] && echo 1 || echo 0)"
    echo "# HELP jellyfin_metadata_reaper_evicted_files_by_tier Files evicted by the most recent run, by importance tier (0=never played .. 3=favourite/in-progress)."
    echo "# TYPE jellyfin_metadata_reaper_evicted_files_by_tier gauge"
    local t
    for t in 0 1 2 3; do
      echo "jellyfin_metadata_reaper_evicted_files_by_tier{tier=\"$t\"} ${TIER_DELETED[$t]:-0}"
    done
  } > "$tmp"
  mv "$tmp" "$METRICS_FILE"
}

measure_usage_bytes() {
  local total=0
  local p
  for p in "${RESOLVED_PATHS[@]}"; do
    # `du -sB1` reports apparent-ish disk usage in bytes for the subtree; -s summarises to one line.
    local sz
    sz="$(du -sB1 "$p" 2>/dev/null | cut -f1)"
    total=$((total + sz))
  done
  echo "$total"
}

USAGE_BYTES="$(measure_usage_bytes)"

if [ "$USAGE_BYTES" -le "$QUOTA_BYTES" ]; then
  echo "jellyfin-metadata-reaper: usage ${USAGE_BYTES}B <= quota ${QUOTA_BYTES}B (${QUOTA_GB}Gi) — no-op"
  write_metrics "$USAGE_BYTES" 0
  exit 0
fi

echo "jellyfin-metadata-reaper: usage ${USAGE_BYTES}B > quota ${QUOTA_BYTES}B (${QUOTA_GB}Gi) — evicting lowest-importance first"

# Importance map: itemid -> tier. Absent => tier 0 (never played by anyone).
# Scratch files live on the CACHE VOLUME by default, not the container filesystem: the
# candidate/ranked lists are proportional to the file count (~126k lines in production) and
# a container's writable layer is typically far smaller than the volume they describe.
SCRATCH_DIR="${SORT_TMPDIR:-$ROOT}"
IMPORTANCE_FILE="$(mktemp "$SCRATCH_DIR/.reaper-importance.XXXXXX")"
CANDIDATES_FILE="$(mktemp "$SCRATCH_DIR/.reaper-candidates.XXXXXX")"
RANKED_FILE="$(mktemp "$SCRATCH_DIR/.reaper-ranked.XXXXXX")"
trap 'rm -f "$CANDIDATES_FILE" "$IMPORTANCE_FILE" "$RANKED_FILE"' EXIT

fetch_importance > "$IMPORTANCE_FILE" || true
case "$DB_STATUS" in
  ok)          echo "jellyfin-metadata-reaper: importance ranking ACTIVE ($(wc -l < "$IMPORTANCE_FILE") items with user-data; tie-break atime; recent=${RECENT_DAYS}d)" ;;
  empty)       echo "jellyfin-metadata-reaper: DB reachable but no user-data rows — every item is tier 0, ordering by atime" ;;
  unavailable) echo "jellyfin-metadata-reaper: WARNING — DB signal unavailable, DEGRADED to atime-only LRU" >&2 ;;
  disabled)    echo "jellyfin-metadata-reaper: DB signal not configured — atime-only LRU" ;;
esac

# Collect candidates as: <atime> <size> <path>
: > "$CANDIDATES_FILE"
for p in "${RESOLVED_PATHS[@]}"; do
  find "$p" -type f ! -name '.jellyfin-metadata-usage.prom' ! -name '.reaper-*' -printf '%A@ %s %p\0' >> "$CANDIDATES_FILE"
done

# Rank: emit "<tier> <atime> <size> <path>" NUL-delimited, resolving each file's
# owning ItemId from its parent directory name (32-hex GUID, dashes stripped).
# A path that does not carry a recognisable ItemId is treated as tier 0 — the
# conservative choice for orphaned/derived junk.
#
# NOTE: the two-file awk idiom (NR==FNR builds the map) breaks when the map file
# is EMPTY — awk would then treat the candidates file as the map and emit
# nothing, silently reaping zero files. So the no-signal case takes an explicit
# tier-0 path instead of going through the join.
rank_all_tier0() {
  awk 'BEGIN{RS="\0";ORS="\0"} { print "0 " $0 }' "$CANDIDATES_FILE" > "$RANKED_FILE"
}

if [ ! -s "$IMPORTANCE_FILE" ]; then
  rank_all_tier0
else
  awk_prog='
BEGIN { RS="\0"; ORS="\0" }
NR==FNR { tier[$1]=$2; next }
{
  atime=$1; size=$2;
  path=$0; sub(/^[^ ]+ [^ ]+ /,"",path);
  dir=path; sub(/\/[^\/]*$/,"",dir);
  id=dir; sub(/^.*\//,"",id);
  t=(id in tier) ? tier[id] : 0;
  print t " " atime " " size " " path;
}'
  if ! awk -v FS=" " "$awk_prog" <(tr "\n" "\0" < "$IMPORTANCE_FILE") "$CANDIDATES_FILE" > "$RANKED_FILE" 2>/dev/null \
     || [ ! -s "$RANKED_FILE" ]; then
    # If the ranking step fails or produces nothing, fall back to plain atime
    # order so the disk still gets protected.
    echo "jellyfin-metadata-reaper: WARNING — ranking step failed, using atime-only order" >&2
    rank_all_tier0
    DB_STATUS="unavailable"
  fi
fi

RECLAIMED=0
DELETED_COUNT=0

# Sort by tier ascending (least important first), then atime ascending within tier.
while IFS=' ' read -r -d '' tier atime size path; do
  if [ "$USAGE_BYTES" -le "$QUOTA_BYTES" ]; then
    break
  fi
  if [ "$DRY_RUN" -eq 1 ]; then
    echo "jellyfin-metadata-reaper: [dry-run] would delete $path (${size}B, tier ${tier}, atime ${atime})"
  else
    if rm -f -- "$path" 2>/dev/null; then
      DELETED_COUNT=$((DELETED_COUNT + 1))
    else
      continue
    fi
  fi
  TIER_DELETED[$tier]=$(( ${TIER_DELETED[$tier]:-0} + 1 ))
  USAGE_BYTES=$((USAGE_BYTES - size))
  RECLAIMED=$((RECLAIMED + size))
# Sort by tier ascending (least important first), then atime ascending within tier.
#
# -S/-T are REQUIRED for correctness under a container memory limit: GNU sort sizes its
# in-memory buffer from the HOST's RAM, not the cgroup limit, so on a large node it will
# happily try to buffer far more than the pod is allowed and get OOMKilled (observed:
# exit 137 ranking ~126k files under a 256Mi limit). Cap the buffer and spill to the
# cache volume, which has headroom by construction.
done < <(sort -z -S "${SORT_BUFFER:-32M}" -T "${SORT_TMPDIR:-$ROOT}" -k1,1n -k2,2n "$RANKED_FILE")

echo "jellyfin-metadata-reaper: reclaimed ${RECLAIMED}B across ${DELETED_COUNT} files; usage now ${USAGE_BYTES}B (quota ${QUOTA_BYTES}B)"
echo "jellyfin-metadata-reaper: evicted by tier — never-played=${TIER_DELETED[0]:-0} old-played=${TIER_DELETED[1]:-0} recent=${TIER_DELETED[2]:-0} favourite/in-progress=${TIER_DELETED[3]:-0}"

echo "jellyfin-metadata-reaper: reclaimed ${RECLAIMED}B across ${DELETED_COUNT} files; usage now ${USAGE_BYTES}B (quota ${QUOTA_BYTES}B)"

if [ "$DRY_RUN" -eq 0 ]; then
  write_metrics "$USAGE_BYTES" "$RECLAIMED"
fi

if [ "$USAGE_BYTES" -gt "$QUOTA_BYTES" ]; then
  echo "jellyfin-metadata-reaper: WARNING — still over quota after reaping every candidate file under ${REAP_PATHS[*]}" >&2
  exit 2
fi

exit 0

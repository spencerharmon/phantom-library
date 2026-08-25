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
# Usage:
#   jellyfin-metadata-reaper.sh --root <mount-path> --quota-gb <N> \
#       [--path <subdir>]... [--metrics-file <path>] [--dry-run]
#
# Exit codes: 0 = success (no-op or reclaimed enough), 1 = usage bad args,
# 2 = could not reclaim below quota (all candidate files already deleted).
set -euo pipefail

ROOT=""
QUOTA_GB=""
declare -a REAP_PATHS=()
METRICS_FILE=""
DRY_RUN=0

usage() {
  echo "usage: $0 --root <path> --quota-gb <N> [--path <subdir>]... [--metrics-file <path>] [--dry-run]" >&2
  exit 1
}

while [ $# -gt 0 ]; do
  case "$1" in
    --root) ROOT="$2"; shift 2 ;;
    --quota-gb) QUOTA_GB="$2"; shift 2 ;;
    --path) REAP_PATHS+=("$2"); shift 2 ;;
    --metrics-file) METRICS_FILE="$2"; shift 2 ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage ;;
    *) echo "$0: unknown argument: $1" >&2; usage ;;
  esac
done

[ -n "$ROOT" ] || usage
[ -n "$QUOTA_GB" ] || usage
[ ${#REAP_PATHS[@]} -gt 0 ] || usage

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

write_metrics() {
  local usage_bytes="$1"
  local reclaimed_bytes="$2"
  local tmp
  tmp="$(mktemp "${METRICS_FILE}.XXXXXX")"
  {
    echo "# HELP jellyfin_metadata_cache_usage_bytes Current bytes used by the shared jellyfin-metadata cache across reaped subtrees."
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

echo "jellyfin-metadata-reaper: usage ${USAGE_BYTES}B > quota ${QUOTA_BYTES}B (${QUOTA_GB}Gi) — evicting LRU-first"

# Build the candidate file list across ALL reap paths, oldest access-time first. `find ... -printf`
# is GNU-find; the reaper image is expected to ship GNU coreutils/findutils (same family as the
# rest of the jellyfin-phantom image). Null-delimited to survive arbitrary filenames.
CANDIDATES_FILE="$(mktemp)"
trap 'rm -f "$CANDIDATES_FILE"' EXIT

: > "$CANDIDATES_FILE"
for p in "${RESOLVED_PATHS[@]}"; do
  find "$p" -type f -printf '%A@ %s %p\0' >> "$CANDIDATES_FILE"
done

RECLAIMED=0
DELETED_COUNT=0

# Sort by atime ascending (oldest/least-recently-accessed first), NUL-delimited throughout.
while IFS=' ' read -r -d '' atime size path; do
  if [ "$USAGE_BYTES" -le "$QUOTA_BYTES" ]; then
    break
  fi
  if [ "$DRY_RUN" -eq 1 ]; then
    echo "jellyfin-metadata-reaper: [dry-run] would delete $path (${size}B, atime ${atime})"
  else
    if rm -f -- "$path" 2>/dev/null; then
      DELETED_COUNT=$((DELETED_COUNT + 1))
    else
      continue
    fi
  fi
  USAGE_BYTES=$((USAGE_BYTES - size))
  RECLAIMED=$((RECLAIMED + size))
done < <(sort -z -k1,1n "$CANDIDATES_FILE")

echo "jellyfin-metadata-reaper: reclaimed ${RECLAIMED}B across ${DELETED_COUNT} files; usage now ${USAGE_BYTES}B (quota ${QUOTA_BYTES}B)"

if [ "$DRY_RUN" -eq 0 ]; then
  write_metrics "$USAGE_BYTES" "$RECLAIMED"
fi

if [ "$USAGE_BYTES" -gt "$QUOTA_BYTES" ]; then
  echo "jellyfin-metadata-reaper: WARNING — still over quota after reaping every candidate file under ${REAP_PATHS[*]}" >&2
  exit 2
fi

exit 0

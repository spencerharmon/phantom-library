#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# prestage-materialised.sh
#
# WHAT:  Reads Phantom Library's materialised_state table and asks gostream
#        Vault Mode to prestage every materialised stub via
#        POST /api/library/prestage.
#
# WHY:   Automatic prestage only affects future materialisations. This script
#        backfills existing materialised movies and episodes without mutating
#        phantom.db or jellyfin.db.
#
# EFFECTS: gostream may rewrite each stub JSON with persist=true and start
#          background FUSE reads that populate the warmup cache. The script
#          does not write SQLite databases and does not delete anything.
#
# SAFETY: default is dry-run. Use --commit to send prestage requests.
#
# OVERRIDES:
#   PHANTOM_DB        Phantom SQLite DB path
#                     (default /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db)
#   GOSTREAM_URL      gostream library-control URL (default http://127.0.0.1:9080)
#   PRESTAGE_PRIORITY gostream Vault priority 0..100 (default 50)
#   HOST_STUB_PREFIX  optional prefix to rewrite from host paths before POST
#   CONTAINER_STUB_PREFIX optional prefix to rewrite to container paths before POST
#
# EXAMPLE:
#   scripts/prestage-materialised.sh --commit
#   HOST_STUB_PREFIX=/var/gostream/gostream-mkv-real \
#   CONTAINER_STUB_PREFIX=/mnt/gostream-mkv-real \
#     scripts/prestage-materialised.sh --commit
# ---------------------------------------------------------------------------

set -euo pipefail

PHANTOM_DB="${PHANTOM_DB:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}"
GOSTREAM_URL="${GOSTREAM_URL:-http://127.0.0.1:9080}"
PRESTAGE_PRIORITY="${PRESTAGE_PRIORITY:-50}"
HOST_STUB_PREFIX="${HOST_STUB_PREFIX:-}"
CONTAINER_STUB_PREFIX="${CONTAINER_STUB_PREFIX:-}"
COMMIT=0
LIMIT=""
TYPE_FILTER=""

usage() {
    cat <<'EOF'
Usage: prestage-materialised.sh [--commit] [--limit N] [--type movie|episode] [-h|--help]

  (default)          dry-run; prints rows and target requests.
  --commit           send POST /api/library/prestage requests.
  --limit N          process at most N materialised rows.
  --type TYPE        restrict to movie or episode.
  -h, --help         show this help.

Environment overrides:
  PHANTOM_DB  GOSTREAM_URL  PRESTAGE_PRIORITY  HOST_STUB_PREFIX  CONTAINER_STUB_PREFIX
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --commit) COMMIT=1; shift ;;
        --limit)
            [[ $# -ge 2 ]] || { echo "--limit requires N" >&2; exit 2; }
            LIMIT="$2"; shift 2 ;;
        --type)
            [[ $# -ge 2 ]] || { echo "--type requires movie|episode" >&2; exit 2; }
            TYPE_FILTER="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown arg: $1" >&2; usage >&2; exit 2 ;;
    esac
done

command -v sqlite3 >/dev/null || { echo "sqlite3 not found" >&2; exit 1; }
command -v curl >/dev/null || { echo "curl not found" >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 not found" >&2; exit 1; }

[[ -r "$PHANTOM_DB" ]] || { echo "phantom DB not readable: $PHANTOM_DB" >&2; exit 1; }
[[ "$PRESTAGE_PRIORITY" =~ ^[0-9]+$ ]] || { echo "PRESTAGE_PRIORITY must be integer 0..100" >&2; exit 1; }
if (( PRESTAGE_PRIORITY < 0 || PRESTAGE_PRIORITY > 100 )); then
    echo "PRESTAGE_PRIORITY must be integer 0..100" >&2
    exit 1
fi
if [[ -n "$TYPE_FILTER" && "$TYPE_FILTER" != "movie" && "$TYPE_FILTER" != "episode" ]]; then
    echo "--type must be movie or episode" >&2
    exit 2
fi
if [[ -n "$LIMIT" && ! "$LIMIT" =~ ^[0-9]+$ ]]; then
    echo "--limit must be positive integer" >&2
    exit 2
fi

where="WHERE stub_path IS NOT NULL AND length(trim(stub_path)) > 0"
if [[ -n "$TYPE_FILTER" ]]; then
    where="$where AND type = '$TYPE_FILTER'"
fi
limit_sql=""
if [[ -n "$LIMIT" ]]; then
    limit_sql="LIMIT $LIMIT"
fi

sql="SELECT type, tmdb_id, season, episode, stub_path FROM materialised_state $where ORDER BY materialised_at ASC, tmdb_id ASC, type ASC $limit_sql;"

json_body() {
    local stub_path="$1"
    python3 - "$stub_path" "$PRESTAGE_PRIORITY" <<'PY'
import json
import sys
print(json.dumps({"stub_path": sys.argv[1], "priority": int(sys.argv[2])}, separators=(",", ":")))
PY
}

rewrite_stub_path() {
    local path="$1"
    if [[ -n "$HOST_STUB_PREFIX" && -n "$CONTAINER_STUB_PREFIX" && "$path" == "$HOST_STUB_PREFIX"* ]]; then
        printf '%s%s\n' "$CONTAINER_STUB_PREFIX" "${path#"$HOST_STUB_PREFIX"}"
    else
        printf '%s\n' "$path"
    fi
}

post_prestage() {
    local stub_path="$1"
    local body http_code response_file
    body="$(json_body "$stub_path")"
    response_file="$(mktemp)"
    http_code="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X POST "$GOSTREAM_URL/api/library/prestage" \
        -H 'Content-Type: application/json' \
        --data-binary "$body")" || {
            cat "$response_file" >&2 || true
            rm -f "$response_file"
            return 1
        }

    if [[ "$http_code" =~ ^2 ]]; then
        rm -f "$response_file"
        return 0
    fi

    echo "HTTP $http_code for $stub_path: $(cat "$response_file")" >&2
    rm -f "$response_file"
    return 1
}

mode="DRY-RUN"
if (( COMMIT == 1 )); then
    mode="COMMIT"
fi

echo "mode=$mode"
echo "phantom_db=$PHANTOM_DB"
echo "gostream_url=$GOSTREAM_URL"
echo "priority=$PRESTAGE_PRIORITY"
if [[ -n "$HOST_STUB_PREFIX" || -n "$CONTAINER_STUB_PREFIX" ]]; then
    echo "path_rewrite=$HOST_STUB_PREFIX -> $CONTAINER_STUB_PREFIX"
fi

count=0
ok=0
fail=0
while IFS=$'\t' read -r type tmdb_id season episode stub_path; do
    [[ -n "${stub_path:-}" ]] || continue
    count=$((count + 1))
    target_stub="$(rewrite_stub_path "$stub_path")"
    label="$type/$tmdb_id s=$season e=$episode"
    if (( COMMIT == 0 )); then
        echo "DRY-RUN prestage $label stub=$target_stub"
        continue
    fi

    if post_prestage "$target_stub"; then
        ok=$((ok + 1))
        echo "OK prestage $label stub=$target_stub"
    else
        fail=$((fail + 1))
        echo "FAIL prestage $label stub=$target_stub" >&2
    fi
done < <(sqlite3 -readonly -batch -separator $'\t' "$PHANTOM_DB" "$sql")

echo "rows=$count ok=$ok failed=$fail"
if (( fail > 0 )); then
    exit 1
fi

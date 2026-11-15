#!/usr/bin/env bash
# Required acceptance: real .NET + PostgreSQL (4,000,000 rows), then movie/TV rig.
# No dry-run, reduced-row, missing-DSN, missing-rig, or optional-success path.
# PHANTOM_TEST_POSTGRES_DSN: Npgsql DSN; requires CREATE SCHEMA, not a production DB.
# Without it, provision an owned disposable postgres:16 container or local cluster.
# PHANTOM_P0_RIG_RUNNER: optional executable replacing live-rig-run.sh. It receives
# the two required scenario paths as arguments and MUST run them, not just build.
# Dependencies: .NET 9, patched jellyfin/ source, bash/coreutils, podman OR local
# initdb/pg_ctl (unless DSN supplied). Default rig additionally needs its documented
# sandbox seed, user systemd, registry access, podman, Python, sqlite3, curl, ffmpeg.
set -euo pipefail

ROOT="$(realpath "$(dirname "${BASH_SOURCE[0]}")/../..")"
[[ "$PWD" == "$ROOT" ]] || { printf 'Run from the code worktree root (beehive submodule worktree exec).\n' >&2; exit 2; }
[[ "${PHANTOM_CI_DRYRUN:-0}" == 0 ]] || { printf 'Dry-run is forbidden for P0 acceptance.\n' >&2; exit 2; }
command -v dotnet >/dev/null || { printf '.NET SDK 9 is required.\n' >&2; exit 2; }
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Never reuse an earlier run's artifacts as acceptance evidence.
mkdir -p "${PHANTOM_P0_EVIDENCE_ROOT:-$ROOT/artifacts/p0-availability}"
export PHANTOM_P0_EVIDENCE_DIR
PHANTOM_P0_EVIDENCE_DIR="$(mktemp -d "${PHANTOM_P0_EVIDENCE_ROOT:-$ROOT/artifacts/p0-availability}/run-XXXXXXXX")"
printf 'P0 evidence: %s\n' "$PHANTOM_P0_EVIDENCE_DIR"
container=""
pgdata=""
heartbeat=""
cleanup() {
    local status=$?
    trap - EXIT
    if [[ -n "$heartbeat" ]]; then kill "$heartbeat" 2>/dev/null || :; wait "$heartbeat" 2>/dev/null || :; fi
    # Only this invocation's disposable database is stopped, never another rig or server.
    if [[ -n "$container" ]]; then
        podman logs "$container" >"$PHANTOM_P0_EVIDENCE_DIR/postgres.log" 2>&1 || status=1
        podman rm -f "$container" >/dev/null || status=1
    fi
    if [[ -n "$pgdata" ]]; then
        pg_ctl -D "$pgdata" -m fast -w stop >>"$PHANTOM_P0_EVIDENCE_DIR/postgres.log" 2>&1 || status=1
    fi
    printf '%s\n' "$status" >"$PHANTOM_P0_EVIDENCE_DIR/exit-status.txt"
    printf 'P0 exit=%s; evidence retained at %s\n' "$status" "$PHANTOM_P0_EVIDENCE_DIR"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
(while sleep 30; do printf 'P0 acceptance still running (%s); evidence: %s\n' "$(date -u +%FT%TZ)" "$PHANTOM_P0_EVIDENCE_DIR"; done) &
heartbeat=$!
{
    date -u +%FT%TZ
    uname -a
    dotnet --info
    if command -v lscpu >/dev/null; then lscpu; else printf 'lscpu unavailable\n'; fi
    if command -v free >/dev/null; then free -b; else printf 'free unavailable\n'; fi
    df -h "$ROOT"
    printf '\nCache protocol: normal planner; ANALYZE after bulk hydration.\n'
    printf 'No OS/PG cache flush, prewarm, enable_seqscan=off, timing retries or best-of selection.\n'
    printf 'First execution of each scenario/variant is retained and gated.\n'
} >"$PHANTOM_P0_EVIDENCE_DIR/hardware-cache.txt"

if [[ -z "${PHANTOM_TEST_POSTGRES_DSN:-}" ]]; then
    if command -v podman >/dev/null && podman info >/dev/null 2>&1; then
        container="phantom-p0-pg-$$-$RANDOM"
        password="p0-$RANDOM-$RANDOM-$RANDOM"
        podman run -d --name "$container" -p 127.0.0.1::5432 \
            -e POSTGRES_USER=phantom -e "POSTGRES_PASSWORD=$password" -e POSTGRES_DB=phantom_p0 \
            docker.io/library/postgres:16 >"$PHANTOM_P0_EVIDENCE_DIR/postgres-container.txt"
        ready=0
        for ((attempt=0; attempt<60; attempt++)); do
            if podman exec "$container" pg_isready -U phantom -d phantom_p0 >/dev/null 2>&1; then ready=1; break; fi
            sleep 1
        done
        [[ "$ready" == 1 ]] || { printf 'Disposable PostgreSQL did not become ready.\n' >&2; exit 1; }
        port="$(podman port "$container" 5432/tcp)"
        export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=${port##*:};Username=phantom;Password=$password;Database=phantom_p0"
        podman inspect --format '{{.ImageName}} {{.Image}}' "$container" >"$PHANTOM_P0_EVIDENCE_DIR/postgres-image.txt"
    else
        if command -v pg_config >/dev/null; then export PATH="$(pg_config --bindir):$PATH"; fi
        command -v initdb >/dev/null && command -v pg_ctl >/dev/null || {
            printf 'REQUIRED: PHANTOM_TEST_POSTGRES_DSN, working podman, or local initdb/pg_ctl. No tests skipped.\n' >&2; exit 2;
        }
        [[ "$EUID" != 0 ]] || { printf 'Local initdb cannot run as root; use a DSN or podman.\n' >&2; exit 2; }
        # Unix socket in a private directory avoids collisions and network exposure.
        pgdata="$(mktemp -d "${TMPDIR:-/tmp}/phantom-p0-pg-XXXXXXXX")"
        initdb -D "$pgdata" --auth=trust -U phantom >"$PHANTOM_P0_EVIDENCE_DIR/initdb.log"
        pg_ctl -D "$pgdata" -l "$PHANTOM_P0_EVIDENCE_DIR/postgres-server.log" \
            -o "-k $pgdata -c listen_addresses=''" -w start
        export PHANTOM_TEST_POSTGRES_DSN="Host=$pgdata;Username=phantom;Database=postgres"
    fi
fi

database_status=0
dotnet test tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj \
    -c Release -p:UseSharedCompilation=false --disable-build-servers \
    --filter 'FullyQualifiedName~P0AvailabilityClaimsPostgresTests|FullyQualifiedName~AvailabilityProbeWorkerTests|FullyQualifiedName~AvailabilityProbeBatchWorkerTests' \
    --logger 'trx;LogFileName=p0-availability.trx' --logger 'console;verbosity=normal' \
    --results-directory "$PHANTOM_P0_EVIDENCE_DIR" 2>&1 | tee "$PHANTOM_P0_EVIDENCE_DIR/dotnet.log" || database_status=$?

# Verify test discovery/counts as well as the runner exit code. A zero-test filter
# must never make the acceptance gate green. Python is also a rig dependency.
if ! python3 - "$PHANTOM_P0_EVIDENCE_DIR/p0-availability.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results = root.findall('.//t:UnitTestResult', ns)
p0 = [r for r in results if 'P0AvailabilityClaimsPostgresTests' in r.attrib.get('testName', '')]
assert len(p0) >= 8, f'Required P0 cases missing: {len(p0)}'
assert all(r.attrib.get('outcome') == 'Passed' for r in results), 'Failed/skipped/not-executed test'
assert any('FourMillionRows_' in r.attrib.get('testName', '') for r in p0), '4M plan test missing'
assert any('PostgresWorker_RealBatchClaimAndProbeRegressions' in r.attrib.get('testName', '') for r in results), 'Required PostgreSQL worker test missing'
PY
then database_status=1; fi

rig_status=0
scenarios=(tools/rig-scenarios/35-channel-e2e-playback.sh tools/rig-scenarios/36-channel-episode-e2e-playback.sh)
export PHANTOM_RIG_SCENARIOS="${scenarios[*]}"
export PHANTOM_REPO_ROOT="$ROOT"
if [[ -n "${PHANTOM_P0_RIG_RUNNER:-}" ]]; then
    [[ -x "$PHANTOM_P0_RIG_RUNNER" ]] || { printf 'PHANTOM_P0_RIG_RUNNER must be executable.\n' >&2; exit 2; }
    "$PHANTOM_P0_RIG_RUNNER" "${scenarios[@]}" 2>&1 | tee "$PHANTOM_P0_EVIDENCE_DIR/rig.log" || rig_status=$?
else
    # The shared rig runner resets fixed paths/units. Refuse an occupied rig and
    # missing seed rather than killing somebody else's work or cloning production.
    if ! command -v systemctl >/dev/null || ! systemctl --user show-environment >/dev/null 2>&1 \
        || [[ ! -f /var/tmp/jf-test/data/data/jellyfin.db ]]; then
        printf 'REQUIRED rig unavailable: user systemd and existing /var/tmp/jf-test sandbox seed are required. Supply an isolated PHANTOM_P0_RIG_RUNNER otherwise.\n' | tee "$PHANTOM_P0_EVIDENCE_DIR/rig.log"
        rig_status=1
    else
        for unit in rig-jellyfin rig-tmdb-mock rig-gostream-mock rig-observer; do
            if systemctl --user is-active --quiet "$unit.service" || systemctl --user is-active --quiet "$unit.scope"; then
                printf 'REFUSING occupied rig: %s\n' "$unit" | tee -a "$PHANTOM_P0_EVIDENCE_DIR/rig.log"
                rig_status=1
            fi
        done
        if [[ "$rig_status" == 0 ]]; then
            bash tools/ci/live-rig-run.sh 2>&1 | tee "$PHANTOM_P0_EVIDENCE_DIR/rig.log" || rig_status=$?
        fi
    fi
fi
printf 'database_status=%s\nrig_status=%s\n' "$database_status" "$rig_status" >"$PHANTOM_P0_EVIDENCE_DIR/gates.txt"
[[ "$database_status" == 0 && "$rig_status" == 0 ]]

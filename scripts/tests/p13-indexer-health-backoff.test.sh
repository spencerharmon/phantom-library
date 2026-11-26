#!/usr/bin/env bash
# Required real .NET + PostgreSQL + loopback HTTP regression, no optional skips.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RESULTS="${PHANTOM_TEST_RESULTS:-$(mktemp -d "${TMPDIR:-/tmp}/indexer-health.XXXXXX")}"
mkdir -p "$RESULTS"
PGC=""
cleanup() {
    dotnet build-server shutdown >"$RESULTS/build-server-shutdown.log" 2>&1
    if [[ -n "$PGC" ]]; then
        podman logs "$PGC" >"$RESULTS/postgres.log" 2>&1
        podman rm -f "$PGC" >/dev/null
    fi
}
trap cleanup EXIT
if [[ -z "${PHANTOM_TEST_POSTGRES_DSN:-}" ]]; then
    PGC="phantom-indexer-health-$$-$RANDOM"
    password="fixture-$RANDOM-$RANDOM"
    podman run -d --name "$PGC" -p 127.0.0.1::5432 \
        -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD="$password" -e POSTGRES_DB=health_test \
        "${PHANTOM_TEST_POSTGRES_IMAGE:-docker.io/library/postgres@sha256:9d9e487c1bd6413c52ccb3c02f46c8e37fa1e1d1a32a84519eb4e68d0ad1da30}" >"$RESULTS/container-id"
    port="$(podman port "$PGC" 5432/tcp)"
    ready=0
    for ((i=0; i<90; i++)); do
        if podman exec "$PGC" pg_isready -h 127.0.0.1 -U phantom -d health_test >/dev/null 2>&1; then ready=1; break; fi
        sleep 1
    done
    [[ "$ready" == 1 ]] || { printf 'PostgreSQL not ready\n' >&2; exit 1; }
    export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=${port##*:};Username=phantom;Password=$password;Database=health_test"
fi
python3 "$ROOT/scripts/tests/p13-health-prepare.py" "$RESULTS"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
flags=(-m:2 -p:UseSharedCompilation=false -nodeReuse:false)
python3 "$ROOT/scripts/tests/p13-health-negative.py" "$RESULTS"
dotnet test "$RESULTS/build/tests/IndexerHealth.Tests/IndexerHealth.Tests.csproj" "${flags[@]}" \
    -p:LegacyPhantomDbSource="$RESULTS/PhantomDb.v24.cs" \
    --logger 'trx;LogFileName=health.trx' --logger 'console;verbosity=normal' --results-directory "$RESULTS" 2>&1 | tee "$RESULTS/dotnet.log"
python3 - "$RESULTS/health.trx" <<'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
results = root.findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
assert len(results) == 18, len(results)
assert all(r.attrib['outcome'] == 'Passed' for r in results), [(r.attrib['testName'], r.attrib['outcome']) for r in results]
print('PASS indexer health: 18 executed, 18 passed, 0 skipped (two colours, real PostgreSQL + HTTP, v24/v25 overlap)')
PY
# Existing parsing/fan-out regression suite: health does not erase partial hits,
# break degenerate-query guards, or reintroduce aggregate searches.
dotnet test "$RESULTS/build/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj" "${flags[@]}" \
    --filter 'FullyQualifiedName~ParallelIndexerSearchTests|FullyQualifiedName~ProwlarrClientTests|FullyQualifiedName~TorrentioClientTests|FullyQualifiedName~MagnetSelectorTests|FullyQualifiedName~DegenerateQueryGuardTests|FullyQualifiedName~PhantomDbTests|FullyQualifiedName~PhantomDbPostgresIntegrationTests' \
    --logger 'console;verbosity=normal' 2>&1 | tee "$RESULTS/existing-tests.log"
printf 'Evidence retained: %s\n' "$RESULTS"

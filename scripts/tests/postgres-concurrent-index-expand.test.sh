#!/usr/bin/env bash
# Actual .NET helper + mandatory PostgreSQL, never an optional/skipped test.
# CI supplies an isolated service DSN; locally provision a disposable container.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RESULTS="${PHANTOM_TEST_RESULTS:-$(mktemp -d "${TMPDIR:-/tmp}/concurrent-expand.XXXXXX")}"
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
command -v dotnet >/dev/null
command -v python3 >/dev/null
if [[ -z "${PHANTOM_TEST_POSTGRES_DSN:-}" ]]; then
    command -v podman >/dev/null
    PGC="phantom-concurrent-expand-$$-$RANDOM"
    password="fixture-$RANDOM-$RANDOM"
    podman run -d --name "$PGC" -p 127.0.0.1::5432 \
        -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD="$password" -e POSTGRES_DB=expand_test \
        "${PHANTOM_TEST_POSTGRES_IMAGE:-docker.io/library/postgres@sha256:9d9e487c1bd6413c52ccb3c02f46c8e37fa1e1d1a32a84519eb4e68d0ad1da30}" >"$RESULTS/container-id"
    port="$(podman port "$PGC" 5432/tcp)"
    ready=0
    for ((i=0; i<90; i++)); do
        if podman exec "$PGC" pg_isready -h 127.0.0.1 -U phantom -d expand_test >/dev/null 2>&1; then
            ready=1
            break
        fi
        sleep 1
    done
    [[ "$ready" == 1 ]] || { printf 'PostgreSQL did not become ready\n' >&2; exit 1; }
    export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=${port##*:};Username=phantom;Password=$password;Database=expand_test"
fi
: "${PHANTOM_TEST_POSTGRES_DSN:?An isolated PostgreSQL DSN is required}"
export PHANTOM_TEST_POSTGRES_REQUIRED=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
# Compile the production helper files directly, plus its unchanged legacy tests.
# No Jellyfin clone/patch or second mock implementation is needed for this substrate.
dotnet test "$ROOT/tests/SchemaExpand.Tests/SchemaExpand.Tests.csproj" \
    --filter 'FullyQualifiedName~ConcurrentIndexExpandTests|FullyQualifiedName~SchemaExpandMigratorPostgresTests' \
    -p:UseSharedCompilation=false --logger 'trx;LogFileName=expand.trx' \
    --logger 'console;verbosity=normal' --results-directory "$RESULTS" 2>&1 | tee "$RESULTS/dotnet.log"
python3 - "$RESULTS/expand.trx" <<'PY'
import sys
import xml.etree.ElementTree as ET
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
root = ET.parse(sys.argv[1]).getroot()
results = root.findall('.//t:UnitTestResult', ns)
expected = {'ConcurrentIndexExpandTests': 35, 'SchemaExpandMigratorPostgresTests': 12}
for cls, count in expected.items():
    selected = [r for r in results if cls + '.' in r.attrib['testName']]
    assert len(selected) == count, (cls, count, len(selected))
    assert all(r.attrib['outcome'] == 'Passed' for r in selected), [(r.attrib['testName'], r.attrib['outcome']) for r in selected]
    print(f'{cls}: executed={len(selected)} passed={len(selected)} skipped=0 failed=0')
assert len(results) == sum(expected.values()), len(results)
for r in sorted(results, key=lambda r: r.attrib['testName']):
    for text in r.findall('.//t:StdOut', ns):
        print(text.text or '')
print(f'PASS exact accounting: {len(results)} executed, {len(results)} passed, 0 skipped')
PY
printf 'Evidence retained: %s\n' "$RESULTS"

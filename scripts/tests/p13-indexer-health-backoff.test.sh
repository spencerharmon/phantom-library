#!/usr/bin/env bash
# Mandatory real plugin + PostgreSQL + two independent client/DB instances.
# Uses only an isolated PG schema/container and synthetic loopback HTTP upstream.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RESULTS="${PHANTOM_TEST_RESULTS:-$(mktemp -d "${TMPDIR:-/tmp}/p13-health.XXXXXX")}"
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
    PGC="phantom-p13-health-$$-$RANDOM"
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
    [[ "$ready" == 1 ]] || { printf 'PostgreSQL did not become ready\n' >&2; exit 1; }
    export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=${port##*:};Username=phantom;Password=$password;Database=health_test"
fi
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# The regular repository build needs the recorded patched Jellyfin source.
[[ -f "$ROOT/jellyfin/MediaBrowser.Controller/MediaBrowser.Controller.csproj" ]] || {
    git -C "$ROOT" submodule update --init --depth 1 jellyfin
}
# Build a REAL schema-v24 plugin from the immutable pre-change source. No mock
# old-reader implementation and no binary fixture; archive works in shallow CI.
OLD_REF=9e0555b330659afe2e0b3414c5fed171335cf075
mkdir -p "$RESULTS/old-source"
curl --fail --location --retry 3 --max-time 120 \
    "https://codeload.github.com/spencerharmon/phantom-library/tar.gz/$OLD_REF" -o "$RESULTS/old-source.tar.gz"
tar -xzf "$RESULTS/old-source.tar.gz" --strip-components=1 -C "$RESULTS/old-source"
tar -C "$ROOT/jellyfin" --exclude=.git --exclude=bin --exclude=obj -cf - . \
    | tar -C "$RESULTS/old-source/jellyfin" -xf -
dotnet build "$RESULTS/old-source/src/Jellyfin.Plugin.PhantomLibrary" -p:UseSharedCompilation=false \
    -o "$RESULTS/old-plugin" >"$RESULTS/old-build.log" 2>&1 || { tail -80 "$RESULTS/old-build.log"; exit 1; }
export PHANTOM_TEST_OLD_PLUGIN="$RESULTS/old-plugin/Jellyfin.Plugin.PhantomLibrary.dll"
dotnet test "$ROOT/tests/IndexerHealth.Tests/IndexerHealth.Tests.csproj" \
    -p:UseSharedCompilation=false --filter 'FullyQualifiedName~IndexerHealthBackoffTests' \
    --logger 'trx;LogFileName=health.trx' --logger 'console;verbosity=normal' \
    --results-directory "$RESULTS" 2>&1 | tee "$RESULTS/dotnet.log"
python3 - "$RESULTS/health.trx" <<'PY'
import sys
import xml.etree.ElementTree as ET
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results = ET.parse(sys.argv[1]).findall('.//t:UnitTestResult', ns)
assert len(results) == 22, f'Expected 22 integration cases, got {len(results)}'
assert all(r.attrib['outcome'] == 'Passed' for r in results), [(r.attrib['testName'], r.attrib['outcome']) for r in results]
print('PASS p13 health: 22 executed, 22 passed, 0 skipped (real .NET, PostgreSQL, two colours, HTTP, v24/v25 overlap)')
PY
printf 'Evidence retained: %s\n' "$RESULTS"

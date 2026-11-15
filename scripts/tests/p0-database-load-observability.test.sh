#!/usr/bin/env bash
set -euo pipefail
root="$(realpath "$(dirname "${BASH_SOURCE[0]}")/../..")"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
pg=""
mkdir -p "$root/.p0-test-runtime"
podman() { command podman --root "$root/.p0-test-runtime/storage" --runroot "$root/.p0-test-runtime/run" "$@"; }
cleanup() {
    local status=$?
    dotnet build-server shutdown
    if [[ -n "$pg" ]]; then podman rm -f "$pg"; fi
    exit "$status"
}
trap cleanup EXIT
bash "$root/scripts/tests/p0-prepare-jellyfin.sh"
if [[ -z "${PHANTOM_TEST_POSTGRES_DSN:-}" ]]; then
    pg="phantom-p0-${RANDOM}-$$"
    podman run -d --name "$pg" -p 127.0.0.1::5432 \
        -e POSTGRES_PASSWORD=regression -e POSTGRES_DB=regression docker.io/library/postgres:16-alpine
    port="$(podman port "$pg" 5432/tcp)"
    export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=${port##*:};Username=postgres;Password=regression;Database=regression"
    for attempt in {1..30}; do
        if podman exec "$pg" pg_isready -h 127.0.0.1 -U postgres -d regression; then break; fi
        sleep 1
    done
    podman exec "$pg" pg_isready -h 127.0.0.1 -U postgres -d regression
fi
dotnet restore "$root/jellyfin/src/Jellyfin.CodeAnalysis/Jellyfin.CodeAnalysis.csproj" -p:UseSharedCompilation=false
dotnet test "$root/tests/Jellyfin.Plugin.PhantomLibrary.Tests" -c Release -p:UseSharedCompilation=false \
    --filter 'FullyQualifiedName~P0Observability|FullyQualifiedName~AvailabilityProbeWorkerTests|FullyQualifiedName~PhantomDbPostgresIntegrationTests'
python3 "$root/scripts/tests/p0-database-load-parsing.test.py"

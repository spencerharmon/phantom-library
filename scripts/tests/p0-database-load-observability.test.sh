#!/usr/bin/env bash
set -euo pipefail
root="$(realpath "$(dirname "${BASH_SOURCE[0]}")/../..")"
mkdir -p "$root/.p0-test-runtime/tmp"
export TMPDIR="$root/.p0-test-runtime/tmp"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cleanup() {
    local status=$?
    dotnet build-server shutdown
    exit "$status"
}
trap cleanup EXIT
bash "$root/scripts/tests/p0-prepare-jellyfin.test.sh"
bash "$root/scripts/tests/gitea-nonrig-workflow.test.sh"
bash "$root/scripts/tests/p0-prepare-jellyfin.sh"
dotnet restore "$root/jellyfin/src/Jellyfin.CodeAnalysis/Jellyfin.CodeAnalysis.csproj" -p:UseSharedCompilation=false
bash "$root/tools/ci/with-postgres.sh" dotnet test "$root/tests/Jellyfin.Plugin.PhantomLibrary.Tests" -c Release -p:UseSharedCompilation=false \
    --filter 'FullyQualifiedName~P0Observability|FullyQualifiedName~AvailabilityProbeWorkerTests|FullyQualifiedName~PhantomDbPostgresIntegrationTests'
python3 "$root/scripts/tests/p0-database-load-parsing.test.py"

#!/usr/bin/env bash
# Full chart -> init -> persisted XML -> running Jellyfin plugin -> HTTP measurement rig.
# Owns only temporary synthetic data/processes; never uses a live PVC or production DB.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
if ! command -v helm >/dev/null; then
    tar xzf "$ROOT/scripts/tests/vendor/helm-v3.16.3-linux-amd64.tar.gz" -C "$WORK"
    export PATH="$WORK/linux-amd64:$PATH"
fi
# Same patched fork as the repository's jellyfin gitlink. Archive bootstrap also works
# in a check sandbox without git metadata; no shared checkout or installed host server.
if [[ ! -f "$ROOT/jellyfin/Jellyfin.Server/Jellyfin.Server.csproj" ]]; then
    curl --fail --location --retry 3 --max-time 180 \
        https://codeload.github.com/spencerharmon/jellyfin/tar.gz/248462dbb7f62f70b1473e6c22e54a7cfd05a5c9 \
        -o "$WORK/jellyfin.tar.gz"
    tar xzf "$WORK/jellyfin.tar.gz" --strip-components=1 -C "$ROOT/jellyfin"
fi
python3 "$ROOT/tools/rig-scenarios/52-swarm-health-config.py" --root "$ROOT" --work "$WORK" --chart-only
# Build the actual plugin and Jellyfin server used by the integration rig.
for project in src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj jellyfin/Jellyfin.Server/Jellyfin.Server.csproj; do
    dotnet build "$ROOT/$project" -c Release --disable-build-servers -m:2 -p:UseSharedCompilation=false -p:CopyLocalLockFileAssemblies=true >"$WORK/build.log" 2>&1 || { cat "$WORK/build.log"; exit 1; }
done
python3 "$ROOT/tools/rig-scenarios/52-swarm-health-config.py" --root "$ROOT" --work "$WORK"

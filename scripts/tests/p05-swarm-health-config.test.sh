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
python3 "$ROOT/tools/rig-scenarios/52-swarm-health-config.py" --root "$ROOT" --work "$WORK" --chart-only

# Build tree for the actual plugin + patched Jellyfin server. An initialized jellyfin
# gitlink checkout is built in place (bin/obj are ignored). Otherwise the pinned fork is
# fetched into a PRIVATE staging tree under $WORK: the test must never populate the
# jellyfin/ gitlink path itself, since a non-git directory there blocks the nested
# submodule checkout (`git submodule update --init jellyfin`) of this worktree.
BUILD="$ROOT"
if [[ ! -f "$ROOT/jellyfin/Jellyfin.Server/Jellyfin.Server.csproj" ]]; then
    # Pin = the recorded gitlink when git metadata is present; the literal is the
    # same commit for a check sandbox without git metadata.
    PIN="248462dbb7f62f70b1473e6c22e54a7cfd05a5c9"
    if recorded="$(git -C "$ROOT" rev-parse --verify --quiet HEAD:jellyfin 2>/dev/null)"; then
        PIN="$recorded"
    fi
    BUILD="$WORK/build"
    mkdir -p "$BUILD/jellyfin"
    cp -R "$ROOT/src" "$ROOT/Directory.Build.props" "$ROOT/NuGet.config" "$BUILD/"
    rm -rf "$BUILD"/src/*/bin "$BUILD"/src/*/obj
    curl --fail --silent --show-error --location --retry 3 --max-time 180 \
        "https://codeload.github.com/spencerharmon/jellyfin/tar.gz/$PIN" -o "$WORK/jellyfin.tar.gz"
    tar xzf "$WORK/jellyfin.tar.gz" --strip-components=1 -C "$BUILD/jellyfin"
fi
for project in src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj jellyfin/Jellyfin.Server/Jellyfin.Server.csproj; do
    dotnet build "$BUILD/$project" -c Release --disable-build-servers -m:2 -p:UseSharedCompilation=false -p:CopyLocalLockFileAssemblies=true >"$WORK/build.log" 2>&1 || { cat "$WORK/build.log"; exit 1; }
done
python3 "$ROOT/tools/rig-scenarios/52-swarm-health-config.py" --root "$ROOT" --work "$WORK" --build-root "$BUILD"

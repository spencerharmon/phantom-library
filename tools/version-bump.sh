#!/usr/bin/env bash
#
# version-bump.sh — CI step wrapper for the plugin version auto-bump tool.
#
# Invoked by actions:phantom-library-cd-autodeploy-dev's pipeline as a build
# step before the image build/push. It auto-increments the plugin version
# (revision, the 4th part) and keeps build.yaml + the plugin csproj in lockstep,
# so every merge produces a distinct, monotonically-increasing version. The
# image entrypoint only re-installs the plugin when the baked meta.json version
# DIFFERS from the PVC copy (install.sh derives meta.json from build.yaml), so
# without this bump an un-bumped merge silently no-ops on dev.
#
# Usage:
#   tools/version-bump.sh [--set A.B.C[.D]] [--check] [-- <extra tool args>]
#
#   (no args)          auto-bump the revision.
#   --set A.B.C[.D]    set an explicit target (must be >= current; monotonic).
#   --check            verify lockstep without writing (exit 3 on mismatch).
#
# Prints the new four-part version as the sole stdout line (for CI capture).
# Requires the .NET SDK on PATH (the CI base image already provides it).
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
PROJ="$SCRIPT_DIR/version-bump/PhantomVersionBump.csproj"

# Build once (Release), quietly; the produced dll is invoked below.
dotnet build "$PROJ" -c Release --nologo -v quiet 1>&2

DLL="$SCRIPT_DIR/version-bump/bin/Release/net9.0/phantom-version-bump.dll"
if [[ ! -f "$DLL" ]]; then
  echo "version-bump.sh: built tool not found at $DLL" >&2
  exit 2
fi

exec dotnet "$DLL" --repo "$REPO_ROOT" "$@"

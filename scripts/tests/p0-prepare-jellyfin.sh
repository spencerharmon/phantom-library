#!/usr/bin/env bash
# Fresh regression clones must use the gitlink, not a similarly named upstream tag.
set -euo pipefail
root="$(realpath "$(dirname "${BASH_SOURCE[0]}")/../..")"
if [[ -f "$root/jellyfin/MediaBrowser.Controller/MediaBrowser.Controller.csproj" ]]; then
    exit 0
fi
sha="$(git -C "$root" rev-parse HEAD:jellyfin)"
git clone --no-checkout https://github.com/jellyfin/jellyfin.git "$root/jellyfin"
git -C "$root/jellyfin" checkout --detach "$sha"

#!/usr/bin/env bash
# Fresh regression clones must use the gitlink, not a similarly named upstream tag.
set -euo pipefail
root="$(realpath "$(dirname "${BASH_SOURCE[0]}")/../..")"
dir="${PHANTOM_JELLYFIN_DIR:-$root/jellyfin}"
read -r mode type sha path < <(git -C "$root" ls-tree HEAD -- jellyfin)
if [[ "$mode" != 160000 || "$type" != commit || "$path" != jellyfin ]]; then
    echo "ERROR: HEAD must record a jellyfin gitlink" >&2
    exit 1
fi
url="${PHANTOM_JELLYFIN_REPO:-$(git -C "$root" config --get submodule.jellyfin.url || git -C "$root" config -f .gitmodules --get submodule.jellyfin.url)}"
[[ -n "$url" ]] || { echo "ERROR: no configured Jellyfin repository" >&2; exit 1; }

# Check .git explicitly: an empty submodule directory otherwise resolves to the
# parent repository. Both standalone clones and submodule/worktree gitfiles work.
if [[ ! -e "$dir/.git" ]]; then
    git clone --depth 1 -- "$url" "$dir"
fi
if ! git -C "$dir" cat-file -e "$sha^{commit}" 2>/dev/null; then
    git -C "$dir" fetch --depth 1 -- "$url" "$sha"
fi
git -C "$dir" cat-file -e "$sha^{commit}"
if [[ "$(git -C "$dir" rev-parse --verify HEAD 2>/dev/null || true)" != "$sha" ]]; then
    if [[ -n "$(git -C "$dir" status --porcelain)" ]]; then
        echo "ERROR: refusing to replace dirty Jellyfin checkout; expected $sha" >&2
        exit 1
    fi
    git -C "$dir" checkout --detach "$sha"
fi
[[ "$(git -C "$dir" rev-parse HEAD)" == "$sha" ]]
[[ -f "$dir/MediaBrowser.Controller/MediaBrowser.Controller.csproj" ]]
printf 'Verified Jellyfin gitlink: %s\n' "$sha"

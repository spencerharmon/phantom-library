#!/usr/bin/env bash
# REQ-M14-MOBILE — mobile-viewport DOM/API evidence for the source-management UX.
#
# Unlike the numbered live-Jellyfin rig scenarios, this one needs no server: a
# mobile *browser* runs the same jellyfin-web SPA and the same phantomKebab.js
# custom-JS shim as desktop, so we execute that real shim against a faithful
# minimal DOM sized to a phone viewport and assert the injected controls, touch
# sizing, responsive layout, and the API calls each tap fires (movie + TV
# episode parity). Self-contained; runs in any checkout/worktree.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v node >/dev/null 2>&1; then
  echo "node is required to run the mobile source-management DOM/API evidence" >&2
  exit 3
fi

exec node "$here/phantom-kebab-mobile-dom.mjs"

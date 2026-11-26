#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/native-episode-web-queue.test.sh
#
# Definition of done for p14-native-episode-web-queue: a materialised channel
# episode stays in Jellyfin's NATIVE all-series episode queue and plays from
# the ordinary jellyfin-web Play action.
#
# Root cause (see docs/tasks/p14-native-episode-web-queue.md): core's channel
# "latest" refresh (Home "Latest in Phantom Shows") and the per-item
# IChannelItemRefreshManager refresh reached existing items through the channel
# ROOT and re-parented them there. The episode lost Season/Series
# (SeriesId/SeasonId/SeriesPresentationUniqueKey) and the latest prune deleted
# the Series/Season, so Series.GetEpisodes (web's all-series queue) was empty or
# 404 while the season-scoped browse still worked.
#
#   1. Patch stack 0001..0007 applies with `git apply --check` (install.sh's
#      method) to pristine upstream v10.11.9; the patched ChannelManager
#      hierarchy + per-item refresh tests pass (`dotnet test`, exact count).
#   2. Fresh synthetic isolated rig (tools/rig-scenarios/isolated-playback-rig.py,
#      builds v10.11.9 + all patches + THIS checkout's plugin, owns every
#      process/DB, no production state): movie parity scenario AND episode
#      scenario with --episode-web-queue:
#        - regular (non-admin) user; native-open materialise; unscoped AND
#          season-scoped queue (IsMissing=false, startItemId) exact order;
#          IsMissing=true empty; persisted Series/Season/Episode hierarchy;
#        - Home Latest refresh, then a second native-open without re-browse
#          (per-item refresh), each followed by the same assertions;
#        - pinned (sha256) official jellyfin-web 10.11.11 in Chromium on a
#          desktop AND a mobile viewport: login, Home, details, Play -> web's
#          own all-series queue lists the episode, PlaybackInfo 200, <video>
#          advances, nonzero stream bytes.
#      Evidence (build/patch/web/plugin hashes, raw responses, persisted rows,
#      screenshots, stream byte counts) is retained under artifacts/.
#
# No dry-run and no optional skip: a missing tool, build, browser or failed
# assertion fails the check.
# ---------------------------------------------------------------------------
set -euo pipefail
export PYTHONDONTWRITEBYTECODE=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG=v10.11.9
EXPECTED_CHANNEL_TESTS=13

fatal() { printf 'FATAL: %s\n' "$*" >&2; exit 2; }
note()  { printf '\n== %s\n' "$*"; }

for tool in dotnet node npm python3 patch git tar; do
    command -v "$tool" >/dev/null 2>&1 || fatal "$tool not found (required; no skip)"
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/native-episode-web-queue.XXXXXX")"
cleanup() {
    dotnet build-server shutdown >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
note "1. patch stack on pristine $TAG + patched ChannelManager tests"
python3 - "$WORK/jellyfin.tar.gz" "$TAG" <<'PY'
import sys, urllib.request
urllib.request.urlretrieve(f"https://codeload.github.com/jellyfin/jellyfin/tar.gz/refs/tags/{sys.argv[2]}", sys.argv[1])
PY
tar -xzf "$WORK/jellyfin.tar.gz" -C "$WORK"
SRC="$WORK/jellyfin-${TAG#v}"
[[ -f "$SRC/SharedVersion.cs" ]] || fatal "unexpected upstream archive layout"
git -C "$SRC" init -q
mapfile -t PATCHES < <(printf '%s\n' "$ROOT"/scripts/jellyfin-patches/*.patch | sort)
[[ ${#PATCHES[@]} -ge 7 ]] || fatal "expected >= 7 jellyfin patches, found ${#PATCHES[@]}"
for patch in "${PATCHES[@]}"; do
    git -C "$SRC" apply --check "$patch" || fatal "does not apply cleanly to $TAG: $(basename "$patch")"
    git -C "$SRC" apply "$patch"
    printf '  applied %s\n' "$(basename "$patch")"
done
set +e
dotnet test "$SRC/tests/Jellyfin.LiveTv.Tests/Jellyfin.LiveTv.Tests.csproj" -p:AllowMissingPrunePackageData=true \
    -p:UseSharedCompilation=false -nodeReuse:false --filter "FullyQualifiedName~Jellyfin.LiveTv.Tests.Channels.ChannelManager" \
    --logger "trx;LogFileName=channels.trx" --results-directory "$WORK/results" >"$WORK/dotnet-test.log" 2>&1
rc=$?
set -e
[[ $rc -eq 0 ]] || { tail -60 "$WORK/dotnet-test.log" >&2; fatal "patched ChannelManager tests failed ($rc)"; }
python3 - "$WORK/results/channels.trx" "$EXPECTED_CHANNEL_TESTS" <<'PY' || fatal "TRX accounting failed"
import sys, xml.etree.ElementTree as ET
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
results = ET.parse(sys.argv[1]).getroot().findall(".//t:UnitTestResult", ns)
outcomes = {}
for r in results:
    outcomes.setdefault(r.get("outcome"), []).append(r.get("testName"))
passed = outcomes.get("Passed", [])
others = {k: v for k, v in outcomes.items() if k != "Passed"}
if others or len(passed) != int(sys.argv[2]):
    raise SystemExit(f"passed={len(passed)} expected={sys.argv[2]} others={others}")
for must in ("GetParentChainIds_EpisodeProtectsSeasonSeriesAndChannel", "IsValidChannelParent_SeasonOfSameChannel_IsKept",
             "IsValidChannelParent_EmptyDeletedOrForeignParent_IsRepaired"):
    if not any(must in n for n in passed):
        raise SystemExit("missing test " + must)
print(f"  TRX: {len(passed)} passed, 0 skipped/failed")
PY

# ---------------------------------------------------------------------------
note "2. browser toolchain (lockfile-pinned Playwright)"
npm ci --prefix "$ROOT/tools/rig-scenarios/browser" --ignore-scripts --no-audit --no-fund
if [[ -z "${PHANTOM_RIG_CHROMIUM:-}" ]]; then
    if command -v chromium >/dev/null 2>&1; then
        PHANTOM_RIG_CHROMIUM="$(command -v chromium)"
        export PHANTOM_RIG_CHROMIUM
    else
        "$ROOT/tools/rig-scenarios/browser/node_modules/.bin/playwright" install chromium
    fi
fi

# ---------------------------------------------------------------------------
note "3. isolated rig: movie parity + episode native queue / real web Play"
python3 "$ROOT/tools/rig-scenarios/isolated-playback-rig.py" --backend sqlite --scenario both --episode-web-queue \
    | tee "$WORK/rig.log"
grep -q '^NATIVE_EPISODE_WEB_QUEUE_OK' "$WORK/rig.log" || fatal "episode web queue scenario did not complete"
grep -q '^WEB_PLAY_OK desktop' "$WORK/rig.log" || fatal "desktop web Play not proven"
grep -q '^WEB_PLAY_OK mobile' "$WORK/rig.log" || fatal "mobile web Play not proven"
grep -q '^CHANNEL_E2E_PLAYBACK_OK' "$WORK/rig.log" || fatal "movie regression did not complete"
grep -q '^PLAYBACK_PARITY_OK backend=sqlite movie=passed episode=passed' "$WORK/rig.log" || fatal "rig did not pass both scenarios"
printf '\nNATIVE EPISODE WEB QUEUE: ALL CHECKS PASSED (evidence: %s)\n' "$(sed -n 's/^EVIDENCE //p' "$WORK/rig.log")"

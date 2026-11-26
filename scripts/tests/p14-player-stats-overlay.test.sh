#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p14-player-stats-overlay.test.sh
#
# Definition of done for p14-player-stats-overlay (ROI P14): live source +
# stream-health visibility in the Jellyfin web/mobile player.
#
#   1. REAL .NET controller tests (`dotnet test`) — the production
#      PhantomLibraryPlayerStatsController + PlayerStatsService + GostreamClient +
#      a real SQLite PhantomDb against a FAKE GOSTREAM (an HttpListener on a
#      loopback port speaking the gostream:phantom-player-stats-endpoint
#      contract) over real HTTP: movie AND episode payloads, null preservation,
#      token never leaked (and actually sent), 404/401/500/unreachable gostream,
#      not-materialised / materialising / non-phantom / no-source states, the
#      caller-session scoping and per-item 403. Exact test count, zero skips.
#   2. The REAL overlay script (phantomPlayerStats.js + the phantomBadges.js
#      bootstrap) executed in node:vm against a minimal DOM on a virtual clock:
#      polls ONLY while the player is open, at 2–5 s, stops on close /
#      background / collapse / playback-stopped, re-arms on the next item, one
#      request in flight, movie + episode render, desktop + mobile layouts,
#      untrusted text never parsed as HTML.
#   3. The rig gostream mock's new /api/library/stats endpoint (real HTTP server
#      on an ephemeral port; loaded-on-add semantics, 404/400, stats log).
#   4. The live-rig scenario tools/rig-scenarios/52-player-stats-overlay.sh
#      (movie AND episode on :18096; overlay served/injected; stats payload;
#      polling stops when playback stops) is executable, syntax/shellcheck/
#      py_compile clean, prod-port-refusing and trap-clean. It is RUN here
#      when PHANTOM_P14_LIVE_RIG=1 (fails hard if the rig cannot start) or
#      automatically when the seeded rig prerequisites exist and :18096 is free.
#
# Exit 0 = all assertions passed. No optional skip of 1–3.
# ---------------------------------------------------------------------------
set -euo pipefail
# Importing tools/rig-scenarios/gostream-mock.py must not rewrite the tracked
# __pycache__ artefact (keeps the checkout clean).
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
TEST_PROJ="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj"
TEST_CLASS="Jellyfin.Plugin.PhantomLibrary.Tests.PlayerStatsOverlayTests"
EXPECTED_TESTS=31
OVERLAY_HARNESS="$REPO_ROOT/tools/rig-scenarios/phantom-player-stats-overlay-dom.mjs"
EXPECTED_OVERLAY_CASES=16
GOSTREAM_MOCK="$REPO_ROOT/tools/rig-scenarios/gostream-mock.py"
SCENARIO="$REPO_ROOT/tools/rig-scenarios/52-player-stats-overlay.sh"

fatal() { printf 'FATAL: %s\n' "$*" >&2; exit 2; }
note()  { printf '\n== %s\n' "$*"; }
pass()  { printf '  PASS %s\n' "$*"; }

command -v dotnet  >/dev/null 2>&1 || fatal "dotnet not found (required for the .NET controller tests; no skip)"
command -v node    >/dev/null 2>&1 || fatal "node not found (required to execute the overlay script; no skip)"
command -v python3 >/dev/null 2>&1 || fatal "python3 not found (required for the gostream mock + TRX accounting)"
for f in "$TEST_PROJ" "$OVERLAY_HARNESS" "$GOSTREAM_MOCK" "$SCENARIO"; do
    [[ -f "$f" ]] || fatal "missing: $f"
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/p14-player-stats.XXXXXX")"
cleanup() {
    dotnet build-server shutdown >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
note "0. jellyfin nested submodule (compile dependency of the test project)"
if [[ ! -f "$REPO_ROOT/jellyfin/Jellyfin.Data/Jellyfin.Data.csproj" ]]; then
    ref_args=()
    if [[ -n "${PHANTOM_JELLYFIN_REFERENCE:-}" && -d "$PHANTOM_JELLYFIN_REFERENCE" ]]; then
        ref_args=(--reference "$PHANTOM_JELLYFIN_REFERENCE")
    fi
    git -C "$REPO_ROOT" -c protocol.file.allow=always submodule update --init "${ref_args[@]}" jellyfin \
        || fatal "could not initialise the jellyfin submodule"
fi
[[ -f "$REPO_ROOT/jellyfin/Jellyfin.Data/Jellyfin.Data.csproj" ]] || fatal "jellyfin submodule still not populated"
pass "jellyfin sources present"

# ---------------------------------------------------------------------------
note "1. .NET controller tests against a fake gostream ($TEST_CLASS)"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
set +e
dotnet test "$TEST_PROJ" -p:UseSharedCompilation=false \
    --filter "FullyQualifiedName~$TEST_CLASS" \
    --logger "trx;LogFileName=p14.trx" --results-directory "$WORK/results" \
    --logger "console;verbosity=normal" >"$WORK/dotnet-test.log" 2>&1
rc=$?
set -e
grep -E '^\s+(Passed|Failed|Skipped) |error|Total tests|Passed!|Failed!' "$WORK/dotnet-test.log" | tail -60 || true
[[ "$rc" -eq 0 ]] || { tail -80 "$WORK/dotnet-test.log" >&2; fatal "dotnet test failed (exit $rc)"; }
TRX="$WORK/results/p14.trx"
[[ -f "$TRX" ]] || fatal "TRX result not produced: $TRX"
python3 - "$TRX" "$EXPECTED_TESTS" <<'PY' || fatal "TRX accounting failed"
import sys, xml.etree.ElementTree as ET
trx, expected = sys.argv[1], int(sys.argv[2])
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
results = ET.parse(trx).getroot().findall(".//t:UnitTestResult", ns)
outcomes = {}
for r in results:
    outcomes.setdefault(r.get("outcome"), []).append(r.get("testName"))
passed = outcomes.get("Passed", [])
others = {k: v for k, v in outcomes.items() if k != "Passed"}
print(f"  TRX: {len(passed)} passed, non-passed={ {k: len(v) for k, v in others.items()} }")
if others:
    raise SystemExit(f"non-passed outcomes (no skips allowed): {others}")
if len(passed) != expected:
    raise SystemExit(f"expected exactly {expected} tests, got {len(passed)}: a test was added/removed without updating EXPECTED_TESTS")
for must in ("Current_PlayingMaterialisedMovie_ReturnsSourceAndLiveGostreamStats",
             "Current_PlayingMaterialisedEpisode_ReturnsSameShapeAsMovie",
             "SerialisedPayload_NeverLeaksTokenOrGostreamUrl_AndKeepsExplicitNulls",
             "GostreamTorrentNotFound_ReportsNotLoaded_KeepsSourceMetadata",
             "ForItem_NonAdminNotPlayingIt_IsForbidden_AndNeverCallsGostream"):
    if not any(must in n for n in passed):
        raise SystemExit(f"required test missing: {must}")
PY
pass "$EXPECTED_TESTS/$EXPECTED_TESTS .NET controller tests passed (0 skipped)"

# ---------------------------------------------------------------------------
note "2. real overlay script: DOM + polling lifecycle (node:vm, virtual clock)"
node "$OVERLAY_HARNESS" | tee "$WORK/overlay.log"
summary="$(tail -n 1 "$WORK/overlay.log")"
[[ "$summary" == "$EXPECTED_OVERLAY_CASES passed, 0 failed" ]] || fatal "overlay harness summary '$summary' != '$EXPECTED_OVERLAY_CASES passed, 0 failed'"
pass "overlay polling lifecycle / layout / injection cases"

# ---------------------------------------------------------------------------
note "3. rig gostream mock: /api/library/stats contract over real HTTP"
JF_RIG_ROOT="$WORK/rig" python3 - "$GOSTREAM_MOCK" <<'PY'
import importlib.util, json, sys, threading, urllib.error, urllib.request
from http.server import ThreadingHTTPServer
spec = importlib.util.spec_from_file_location("gostream_mock", sys.argv[1])
gm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gm)
srv = ThreadingHTTPServer(("127.0.0.1", 0), gm.Handler)
threading.Thread(target=srv.serve_forever, daemon=True).start()
base = f"http://127.0.0.1:{srv.server_address[1]}"

def get(q):
    try:
        r = urllib.request.urlopen(f"{base}/api/library/stats?{q}", timeout=5)
        return r.status, json.load(r), r.headers.get("Cache-Control")
    except urllib.error.HTTPError as e:
        return e.code, json.load(e), e.headers.get("Cache-Control")

H = "1111111111111111111111111111111111111111"
s, b, _ = get("hash=" + H)
assert (s, b) == (404, {"error": "torrent_not_found"}), (s, b)
s, b, _ = get("hash=nothex")
assert (s, b) == (400, {"error": "invalid_infohash"}), (s, b)
# /api/library/add path marks the magnet's btih loaded (uppercase normalised)
gm.mark_loaded(gm.btih_of("magnet:?xt=urn:btih:" + H.upper() + "&dn=Phantom+Rig+Alpha"))
s, b, cc = get("hash=" + H)
assert s == 200 and cc == "no-store", (s, cc)
expected_keys = {"hash", "peers", "seeders", "download_rate_bytes_per_sec", "bytes_completed", "size", "read_offset",
                 "buffered_ahead_bytes", "buffered_ahead_seconds", "stream_state", "last_read_at",
                 "torrent_sampled_at", "stream_sampled_at"}
assert set(b) == expected_keys, set(b) ^ expected_keys
assert b["hash"] == H and b["buffered_ahead_seconds"] is None and b["stream_state"] == "streaming", b
seed = int(H[:6], 16)
assert b["peers"] == 12 + seed % 5 and b["seeders"] == 7 + seed % 3, b
log = [json.loads(l) for l in open(gm.STATS_LOG) if l.strip()]
assert sorted(e["status"] for e in log) == [200, 400, 404], log
srv.shutdown()
print("  gostream mock stats: 404 unknown, 400 invalid, 200 after add, explicit nulls, request log OK")
PY
pass "gostream mock stats endpoint"

# ---------------------------------------------------------------------------
note "4. live-rig scenario 52 (movie + episode on :18096)"
[[ -x "$SCENARIO" ]] || fatal "scenario not executable: $SCENARIO"
bash -n "$SCENARIO" || fatal "scenario has a bash syntax error"
if command -v shellcheck >/dev/null 2>&1; then
    shellcheck -S warning "$SCENARIO" || fatal "scenario fails shellcheck -S warning"
    pass "shellcheck -S warning clean"
fi
python3 - "$SCENARIO" "$WORK" <<'PY' || fatal "embedded python in scenario does not compile"
import re, sys, py_compile
src = open(sys.argv[1]).read()
bodies = re.findall(r"<<'PY'[^\n]*\n(.*?)\nPY\n", src, re.S)
assert len(bodies) >= 3, f"expected heredoc python bodies, found {len(bodies)}"
for i, b in enumerate(bodies):
    p = f"{sys.argv[2]}/heredoc-{i}.py"
    open(p, "w").write(b)
    py_compile.compile(p, doraise=True)
print(f"  {len(bodies)} quoted-heredoc python bodies py_compile clean")
PY
# Structural guarantees the scenario must keep (it is the live proof).
for needle in \
    'REFUSING to run against production port :8096' \
    'trap cleanup EXIT INT TERM' \
    'rig-up.sh --reset' \
    'rig-down.sh' \
    'Plugins/PhantomLibrary/playerstats.js' \
    'PlayerStats/Current' \
    'Sessions/Playing' \
    'phantom-player-stats-overlay-dom.mjs --live' \
    'play_and_assert "$ALPHA_ID" movie' \
    'play_and_assert "$EP_ID" episode' \
    'gostream still receiving stats requests after playback stopped'; do
    grep -qF -- "$needle" "$SCENARIO" || fatal "scenario lost required step: $needle"
done
pass "scenario structure: prod refusal, trap teardown, movie+episode, served/injected script, stop-polling proof"

run_live=0
if [[ "${PHANTOM_P14_LIVE_RIG:-0}" == "1" ]]; then
    run_live=1
elif [[ -s /var/tmp/jf-test/data/data/jellyfin.db && -f "$REPO_ROOT/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll" ]] \
    && ! (exec 3<>/dev/tcp/127.0.0.1/18096) 2>/dev/null; then
    run_live=1
fi
if [[ "$run_live" == "1" ]]; then
    PHANTOM_REPO_ROOT="$REPO_ROOT" bash "$SCENARIO" | tee "$WORK/scenario-52.log"
    grep -q '^PLAYER_STATS_OVERLAY_OK' "$WORK/scenario-52.log" || fatal "live scenario 52 did not complete"
    pass "LIVE scenario 52 passed (movie + episode)"
else
    printf '  NOTE: live rig not run here (no seeded /var/tmp/jf-test clone + built patched Jellyfin, or :18096 already bound by a non-rig server).\n'
    printf '        Run it on the rig host: PHANTOM_P14_LIVE_RIG=1 %s\n' "${BASH_SOURCE[0]}"
fi

printf '\nP14 PLAYER STATS OVERLAY: ALL CHECKS PASSED\n'

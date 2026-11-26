#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/postgres-media-size-int64.test.sh
#
# Definition-of-done harness for postgres-media-size-int64-expand.
#
# Bug: PostgreSQL INTEGER is 32-bit, so a real >= 2 GiB movie/episode size
# threw `22003 integer out of range` in PhantomDb.UpsertSourceCandidatesAsync
# (source_candidates.size INTEGER) — and the same overflow sat latent in
# source_candidates.selected_file_size, availability_items.candidate_size and
# magnet_cache.size. The v23 EXPAND adds nullable BIGINT *_bytes companions
# (never retyping/dropping the INTEGER columns), dual-writes, and resolves
# reads via PhantomDb.ResolvedSizeSql so a still-running pre-v23 writer keeps
# working against the shared database.
#
# This harness is NOT a grep/structure check. It:
#   1. ensures the `jellyfin` nested submodule (needed to compile the plugin
#      + test project) is populated — initialising it if missing;
#   2. provisions an ISOLATED, throwaway PostgreSQL 16 (rootless podman,
#      random loopback port, unique container name, torn down on EXIT);
#   3. runs the REAL .NET persistence integration suite
#      (PhantomDbPostgresMediaSizeInt64Tests — real PhantomDb + Npgsql against
#      that Postgres) with PHANTOM_TEST_POSTGRES_REQUIRED=1 so a missing DSN is
#      a hard failure, never a silent skip;
#   4. parses the TRX result and asserts EXACTLY the expected number of tests
#      executed and passed (0 failed, 0 skipped / not-executed), and that the
#      clone-rehearsal predicted/actual counts were emitted and are equal.
#
# Proves: >= 5 GiB movie + episode round trips through source_candidates
# (incl. selected-file size), magnet_cache and availability_items (incl. the
# aliased browse read path); fresh schema shape; prior-schema (v22) self-expand
# with predicted==actual row counts/checksums and zero data update; idempotency
# (recorded no-op, raw DDL re-run, re-open); legacy (pre-v23) writer/reader
# overlap (including old read(-1) -> cache replacement -> availability re-mark,
# magnet/hash changes, selected-file ID/path changes); forward tolerance of a
# newer stamp; generation-fenced availability completion. No production database
# is touched; no flip/contract is applied.
#
# No optional skip: a missing podman / dotnet / Postgres is a FAILURE (exit 2).
# Exit 0 = all assertions passed.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
TEST_PROJ="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj"
TEST_CLASS="Jellyfin.Plugin.PhantomLibrary.Tests.PhantomDbPostgresMediaSizeInt64Tests"
# Original 14 + 6 cache/availability sentinel, 6 selected-file and 2 source-identity cases.
EXPECTED_TESTS=28
PG_IMAGE="${PHANTOM_TEST_POSTGRES_IMAGE:-docker.io/library/postgres:16-alpine}"

fatal() { printf 'FATAL: %s\n' "$*" >&2; exit 2; }
note()  { printf '== %s\n' "$*"; }

command -v podman >/dev/null 2>&1 || fatal "podman not found (required to provision the isolated PostgreSQL; no skip)"
command -v dotnet >/dev/null 2>&1 || fatal "dotnet not found (required for the .NET persistence integration; no skip)"
command -v python3 >/dev/null 2>&1 || fatal "python3 not found (required to parse the TRX result)"
[[ -f "$TEST_PROJ" ]] || fatal "test project not found: $TEST_PROJ"

# ---------------------------------------------------------------------------
note "1. jellyfin nested submodule (compile dependency)"
if [[ ! -f "$REPO_ROOT/jellyfin/Jellyfin.Data/Jellyfin.Data.csproj" ]]; then
    ref_args=()
    if [[ -n "${PHANTOM_JELLYFIN_REFERENCE:-}" && -d "$PHANTOM_JELLYFIN_REFERENCE" ]]; then
        ref_args=(--reference "$PHANTOM_JELLYFIN_REFERENCE")
    fi
    git -C "$REPO_ROOT" -c protocol.file.allow=always submodule update --init "${ref_args[@]}" jellyfin \
        || fatal "could not initialise the jellyfin submodule"
fi
[[ -f "$REPO_ROOT/jellyfin/Jellyfin.Data/Jellyfin.Data.csproj" ]] || fatal "jellyfin submodule still not populated"

# ---------------------------------------------------------------------------
note "2. isolated PostgreSQL ($PG_IMAGE)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/pgsize-int64.XXXXXX")"
PGC="phantom-pgsize-int64-$$-$RANDOM"
cleanup() {
    dotnet build-server shutdown >/dev/null 2>&1 || true
    podman rm -f "$PGC" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

PGPASS="pgsize-$RANDOM$RANDOM"
started=0
for _ in 1 2 3 4 5; do
    PGPORT=$(( (RANDOM % 20000) + 30000 ))
    if podman run -d --name "$PGC" \
            -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD="$PGPASS" -e POSTGRES_DB=phantom_size_test \
            -p "127.0.0.1:$PGPORT:5432" "$PG_IMAGE" >"$WORK/pg-start.log" 2>&1; then
        started=1
        break
    fi
    podman rm -f "$PGC" >/dev/null 2>&1 || true
done
[[ "$started" -eq 1 ]] || { cat "$WORK/pg-start.log" >&2; fatal "could not start the isolated PostgreSQL container"; }

ready=0
for _ in $(seq 1 90); do
    # pg_isready inside the container AND a real query over the mapped port's
    # server (the entrypoint restarts postgres once after init).
    if podman exec "$PGC" pg_isready -h 127.0.0.1 -U phantom -d phantom_size_test >/dev/null 2>&1 \
        && podman exec -e PGPASSWORD="$PGPASS" "$PGC" psql -h 127.0.0.1 -U phantom -d phantom_size_test -tAc 'SELECT 1' 2>/dev/null | grep -qx 1; then
        ready=1
        break
    fi
    sleep 1
done
[[ "$ready" -eq 1 ]] || { podman logs "$PGC" >&2 || true; fatal "isolated PostgreSQL never became ready"; }
PG_VERSION="$(podman exec -e PGPASSWORD="$PGPASS" "$PGC" psql -h 127.0.0.1 -U phantom -d phantom_size_test -tAc 'SHOW server_version')"
printf '   postgres %s on 127.0.0.1:%s (container %s)\n' "$PG_VERSION" "$PGPORT" "$PGC"

# ---------------------------------------------------------------------------
note "3. .NET persistence integration (real PhantomDb + Npgsql)"
export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=$PGPORT;Username=phantom;Password=$PGPASS;Database=phantom_size_test;Pooling=true"
export PHANTOM_TEST_POSTGRES_REQUIRED=1
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
set +e
dotnet test "$TEST_PROJ" -p:UseSharedCompilation=false \
    --filter "FullyQualifiedName~$TEST_CLASS" \
    --logger "trx;LogFileName=pgsize.trx" --results-directory "$WORK/results" \
    --logger "console;verbosity=normal" 2>&1 | tee "$WORK/dotnet-test.log"
rc=${PIPESTATUS[0]}
set -e
[[ "$rc" -eq 0 ]] || fatal "dotnet test failed (exit $rc)"

# ---------------------------------------------------------------------------
note "4. result accounting (no skip, exact count, rehearsal predicted==actual)"
TRX="$WORK/results/pgsize.trx"
[[ -f "$TRX" ]] || fatal "TRX result not produced: $TRX"
python3 - "$TRX" "$EXPECTED_TESTS" "$TEST_CLASS" <<'PY'
import sys, xml.etree.ElementTree as ET
trx, expected, cls = sys.argv[1], int(sys.argv[2]), sys.argv[3]
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
root = ET.parse(trx).getroot()
results = root.findall(".//t:UnitTestResult", ns)
outcomes = {}
for r in results:
    outcomes.setdefault(r.get("outcome"), []).append(r.get("testName"))
passed = outcomes.get("Passed", [])
other = {k: v for k, v in outcomes.items() if k != "Passed"}
print(f"   tests: {len(results)} total, {len(passed)} passed, other={ {k: len(v) for k, v in other.items()} }")
for name in sorted(passed):
    print(f"   PASS {name}")
if other:
    for k, v in other.items():
        for n in v:
            print(f"   {k.upper()} {n}")
    sys.exit("non-passed test outcomes present (skip/fail/not-executed are all failures)")
if len(passed) != expected:
    sys.exit(f"expected exactly {expected} executed+passed tests, got {len(passed)}")
short = cls.rsplit('.', 1)[-1]
if not all(short in n for n in passed):
    sys.exit("unexpected test outside the target class was counted")
import re
text = "\n".join((e.text or "") for e in root.iter("{%s}StdOut" % ns["t"]))
pred = sorted(re.findall(r"REHEARSAL predicted ([a-z_]+) = (\S+)", text))
act = sorted(re.findall(r"REHEARSAL actual +([a-z_]+) = (\S+)", text))
if len(pred) != 3:
    sys.exit(f"clone-rehearsal predicted counts not emitted for 3 tables: {pred}")
if pred != act:
    sys.exit(f"clone-rehearsal predicted != actual: {pred} vs {act}")
print("   clone rehearsal on a v22 clone (rows/md5 of legacy size columns), predicted == actual:")
for t, v in pred:
    print(f"     {t}: predicted {v}  actual {dict(act)[t]}")
PY

printf '\npostgres-media-size-int64: all %d integration tests passed against isolated PostgreSQL %s\n' "$EXPECTED_TESTS" "$PG_VERSION"

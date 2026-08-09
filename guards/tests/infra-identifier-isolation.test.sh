#!/bin/sh
# guards/tests/infra-identifier-isolation.test.sh — the AUTHORING PROOF for the
# infra-identifier-isolation mutation guard. A guard is enforcement, so its
# definition of done is "provably refuses," not "compiles." This drives the guard
# against synthetic pass patches (the guard's real AUTHORITY is the committed diff)
# and asserts the four mandated properties plus placeholder-safety and fail-closed:
#   (1) an ADDED banned identifier in a protected file, honeybee   -> REFUSE
#   (2) a clean diff (neutral placeholder) in a protected file     -> allow
#   (3) the SAME violating diff WITHOUT honeybee identity            -> allow (flip path)
#   (4) verdict depends on DIFF CONTENT, not hardcoded:
#         (4a) the banned identifier only on a REMOVED (-) line     -> allow
#         (4b) a banned identifier in a NON-protected (unmatched) file -> allow
#   (5) RFC2606/RFC5737 placeholders are NOT treated as identifiers  -> allow
#   (6) the promised patch is unavailable                           -> FAIL CLOSED
#
# Run: guards/tests/infra-identifier-isolation.test.sh   (exits non-zero on any failure)
set -eu

here="$(cd "$(dirname "$0")/.." && pwd)"           # guards/
GUARD="$here/infra-identifier-isolation.sh"
[ -x "$GUARD" ] || { echo "FAIL: $GUARD not executable"; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

SRC="src/Jellyfin.Plugin.PhantomLibrary/State/Db/PostgresDbProvider.cs"
CHART="deploy/helm/phantom-library/values.yaml"
OTHER="scripts/phantom-migrate-jellyfindb-to-postgres.sh"  # NOT under a Protects glob

# write_patch <file> <path> : synthesize a minimal unified diff for one file,
# feeding each remaining arg pair as +/- body lines via the ADD_* / DEL_* env below.
mk_patch() {
	# $1 dest file; ADD (newline list) added body lines; DEL removed body lines
	dest="$1"; out="$2"
	{
		echo "diff --git a/$dest b/$dest"
		echo "index 1111111..2222222 100644"
		echo "--- a/$dest"
		echo "+++ b/$dest"
		echo "@@ -1,3 +1,3 @@"
		echo " context line unchanged"
		printf '%s\n' "${DEL:-}" | while IFS= read -r l; do [ -z "$l" ] || echo "-$l"; done
		printf '%s\n' "${ADD:-}" | while IFS= read -r l; do [ -z "$l" ] || echo "+$l"; done
	} >"$out"
}

fails=0
run_case() {
	name="$1"; expect="$2"
	set +e
	out="$("$GUARD" 2>&1)"; rc=$?
	set -e
	if [ "$expect" = refuse ]; then
		if [ "$rc" -eq 0 ]; then echo "FAIL [$name]: expected REFUSE (non-zero), got exit 0"; fails=$((fails+1)); else echo "ok   [$name]: refused (exit $rc)"; fi
	else
		if [ "$rc" -ne 0 ]; then echo "FAIL [$name]: expected allow (exit 0), got exit $rc:"; printf '%s\n' "$out"; fails=$((fails+1)); else echo "ok   [$name]: allowed"; fi
	fi
}

p="$work/patch"

# (1) added banned host in a protected src file, honeybee -> refuse
ADD='var host = "jellyfin.polyfam.studio";' DEL='' mk_patch "$SRC" "$p"
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$SRC" \
	run_case "added domain in src (honeybee)" refuse

# (1b) added RFC1918 cluster IP in the chart, honeybee -> refuse
ADD='  host: "10.42.0.44"' DEL='' mk_patch "$CHART" "$p"
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$CHART" \
	run_case "added cluster IP in chart (honeybee)" refuse

# (1c) added home-LAN LB IP, honeybee -> refuse
ADD='  loadBalancerIP: 192.168.1.99' DEL='' mk_patch "$CHART" "$p"
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$CHART" \
	run_case "added LB IP in chart (honeybee)" refuse

# (2) clean diff (neutral placeholder) in a protected file, honeybee -> allow
ADD='  host: ""  # supplied at runtime; docs use example.com' DEL='' mk_patch "$CHART" "$p"
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$CHART" \
	run_case "clean placeholder in chart (honeybee)" allow

# (3) same violating diff WITHOUT honeybee identity (sanctioned actor / flip path) -> allow
ADD='var host = "jellyfin.polyfam.studio";' DEL='' mk_patch "$SRC" "$p"
BEEHIVE_HONEYBEE=0 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$SRC" \
	run_case "added domain in src (non-honeybee)" allow

# (4a) content-dependence: identifier only on a REMOVED line -> allow (removing a leak is good)
ADD='var host = cfg.Host;' DEL='var host = "jellyfin.polyfam.studio";' mk_patch "$SRC" "$p"
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$SRC" \
	run_case "identifier only removed (honeybee)" allow

# (4b) content-dependence: identifier ADDED but in a NON-protected file (not matched) -> allow.
#      The patch carries the OTHER (unmatched) file; MATCHED_FILES lists only the clean SRC edit.
{
	ADD='cfg.Host = cfg.Host;' DEL='' mk_patch "$SRC" "$work/pa"
	ADD='PGHOST=postgres.postgres.svc.cluster.local  # 10.42.0.44' DEL='' mk_patch "$OTHER" "$work/pb"
	cat "$work/pa" "$work/pb"
} >"$p"
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$SRC" \
	run_case "identifier added only in unmatched file" allow

# (5) placeholder-safety: RFC2606 example.com + RFC5737 test IP added -> allow (not identifiers)
ADD='// e.g. host=example.com ip=192.0.2.10 (RFC5737)' DEL='' mk_patch "$SRC" "$p"
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$p" BEEHIVE_GUARD_MATCHED_FILES="$SRC" \
	run_case "RFC placeholders added (honeybee)" allow

# (6) promised patch unavailable -> fail closed
BEEHIVE_HONEYBEE=1 BEEHIVE_GUARD_DIFF_PATCH="$work/does-not-exist" BEEHIVE_GUARD_MATCHED_FILES="$SRC" \
	run_case "patch unavailable" refuse

if [ "$fails" -ne 0 ]; then
	echo "FAILED: $fails guard proof case(s) failed"
	exit 1
fi
echo "PASS: infra-identifier-isolation guard proven (content-dependent + placeholder-safe + fail-closed)"

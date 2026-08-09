#!/bin/sh
# guards/infra-identifier-isolation.sh — mutation guard (beehive mutation-guard primitive).
#
# Refuse a HONEYBEE change that introduces a deployment-specific INFRASTRUCTURE
# IDENTIFIER into the environment-AGNOSTIC plugin source (src/**) or Helm chart
# (deploy/helm/**). Per the beehive-wide AGENTS.md "Code <-> infrastructure dividing
# line" rule, such values (real hostnames/domains, private/cluster IPs) live ONLY on
# the infrastructure side (the Flux HelmReleases / config / Secrets) and are supplied
# to the code at runtime — never committed into the shared, environment-agnostic
# repo. A hardcoded site identifier silently binds this code to ONE environment, so
# it is exactly a "wrong-environment" change (GATES.md `infra-identifier-isolation`).
#
# AUTHORITY: the change's own committed diff. The guard inspects ONLY the ADDED lines
# (`+`) of the files THIS guard protects (BEEHIVE_GUARD_MATCHED_FILES) in the pass
# patch (BEEHIVE_GUARD_DIFF_PATCH) — so removing an identifier is never flagged, and a
# pre-existing identifier on an unchanged line is out of scope. Fails CLOSED if the
# patch the runner promised is unavailable (cannot verify => refuse).
#
# BANNED (all deployment-specific, all placeholder-safe — RFC2606 example.com/.net and
# RFC5737 192.0.2/198.51.100/203.0.113 test ranges are deliberately NOT matched):
#   - the deployment domain  polyfam.studio  (any host under it: apex, color, dev, ...)
#   - RFC1918 / cluster IPs   192.168.x  10.42.x/10.43.x (k3s pod/svc CIDRs)  172.16-31.x
# Generic naming that is NOT a site identifier (e.g. the chart's color-agnostic
# `jellyfin_prod`/`phantom_prod` default DB names) is intentionally NOT banned.
#
# ABI (set by the runner): BEEHIVE_HONEYBEE=1 for a honeybee pass;
# BEEHIVE_GUARD_MATCHED_FILES = newline-separated changed files this guard protects;
# BEEHIVE_GUARD_DIFF_PATCH = path to the full pass patch.
#
# Exit 0 = allow, non-zero = refuse.
set -eu

fail_closed() {
	echo "guard infra-identifier-isolation: FAIL CLOSED: $*" >&2
	exit 1
}

# Site-specific identifiers, one ERE per line. Each IP pattern requires a trailing
# octet digit so it matches an address, not an unrelated dotted token (a .NET TFM,
# a package version, etc.).
BANNED='polyfam\.studio
192\.168\.[0-9]
10\.4[23]\.[0-9]
172\.(1[6-9]|2[0-9]|3[01])\.[0-9]'

# Only a honeybee change is guarded; the sanctioned non-honeybee actor does not run
# through this gate at all (ABI), so it is never blocked.
if [ "${BEEHIVE_HONEYBEE:-0}" != "1" ]; then
	exit 0
fi

# Nothing this guard protects changed -> allow (the runner only fires us on an
# intersection, but stay correct if invoked with an empty match).
[ -n "${BEEHIVE_GUARD_MATCHED_FILES:-}" ] || exit 0

PATCH="${BEEHIVE_GUARD_DIFF_PATCH:-}"
[ -n "$PATCH" ] && [ -r "$PATCH" ] || fail_closed "pass patch unavailable (BEEHIVE_GUARD_DIFF_PATCH='${BEEHIVE_GUARD_DIFF_PATCH:-}') — cannot verify added lines"

tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
printf '%s\n' "${BEEHIVE_GUARD_MATCHED_FILES}" >"$tmp"

# Extract the ADDED lines that belong to a protected file: walk the unified diff,
# tracking the current destination path from each `+++ b/<path>` header, and emit
# `<path>\t<added-content>` for every `+` body line whose file is in the matched set.
added="$(awk '
	FNR==NR { if ($0 != "") want[$0]=1; next }
	/^\+\+\+ /{
		p=$0; sub(/^\+\+\+ /,"",p); sub(/^[ab]\//,"",p); sub(/\t.*$/,"",p);
		cur=p; next
	}
	/^--- /{ next }
	/^@@/{ next }
	/^\+/{ if (cur in want) print cur "\t" substr($0,2) }
' "$tmp" "$PATCH")"

[ -n "$added" ] || exit 0

violations=""
oldIFS="$IFS"
IFS='
'
for pat in $BANNED; do
	[ -n "$pat" ] || continue
	hits="$(printf '%s\n' "$added" | grep -En "$pat" || true)"
	[ -n "$hits" ] && violations="$violations
pattern /$pat/:
$hits"
done
IFS="$oldIFS"

if [ -n "$violations" ]; then
	echo "guard infra-identifier-isolation: REFUSED — this honeybee change adds a deployment-specific infrastructure identifier to environment-agnostic code (src/** or deploy/helm/**):" >&2
	printf '%s\n' "$violations" >&2
	echo "" >&2
	echo "Per AGENTS.md, such values live ONLY on the infrastructure side (Flux HelmReleases / config / Secrets) and are supplied at runtime. Use a neutral placeholder (example.com, 192.0.2.0/24) and read the real value from chart values / env / a Secret." >&2
	exit 1
fi
exit 0

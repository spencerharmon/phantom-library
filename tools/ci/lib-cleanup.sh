#!/usr/bin/env bash
# tools/ci/lib-cleanup.sh
#
# Shared "no reusable build servers / no leftover processes" cleanup used by
# both tools/ci/nonrig-build-test.sh (as an EXIT trap) and
# tools/ci/nonrig-cleanup.sh (as the Zuul post-run hook).
#
# The .NET SDK leaves long-lived helper processes behind by default:
#   - VBCSCompiler   (the Roslyn shared-compilation server)
#   - MSBuild.dll    (persistent build nodes, when node reuse is on)
#   - testhost       (spawned by `dotnet test`)
# On a reused CI executor these leak file locks and state between runs. We
# disable them at the source (MSBUILDDISABLENODEREUSE=1 +
# -p:UseSharedCompilation=false in the build script), then clean up defensively
# here and VERIFY none survive.
#
# Safety: the raw pattern-kill is OFF unless PHANTOM_CI_PKILL=1 (default 1),
# and is force-skipped in dry-run, so sourcing this on a shared dev box never
# nukes an unrelated `dotnet`. `dotnet build-server shutdown` is always safe.

# phantom_ci_cleanup_dotnet <had_error:0|1>
# Returns 0 when the executor is clean, non-zero when leftovers survive and
# strict mode is on.
phantom_ci_cleanup_dotnet() {
    local had_error="${1:-0}"
    local dryrun="${PHANTOM_CI_DRYRUN:-0}"
    local do_pkill="${PHANTOM_CI_PKILL:-1}"
    local strict="${PHANTOM_CI_STRICT_LEFTOVERS:-1}"
    # Patterns that identify dotnet build servers / test hosts.
    local pat='VBCSCompiler|testhost|MSBuild\.dll'

    printf '\n--- cleanup: shutting down dotnet build servers\n'

    if [ "$dryrun" = 1 ]; then
        do_pkill=0
        echo "cleanup: dry-run — skipping real build-server shutdown / pkill"
    elif command -v dotnet >/dev/null 2>&1; then
        dotnet build-server shutdown >/dev/null 2>&1 \
            && echo "cleanup: dotnet build-server shutdown ok" \
            || echo "cleanup: dotnet build-server shutdown reported an error (ignored)"
    else
        echo "cleanup: no dotnet on PATH — nothing to shut down"
    fi

    if [ "$do_pkill" = 1 ] && command -v pkill >/dev/null 2>&1; then
        # Target only OUR process tree where possible to avoid collateral on a
        # shared host; fall back to pattern kill on a single-use CI node.
        pkill -f "$pat" 2>/dev/null || true
        sleep 1
    fi

    # Verify: is anything left?
    local leftover=""
    if command -v pgrep >/dev/null 2>&1; then
        leftover="$(pgrep -af "$pat" 2>/dev/null || true)"
    fi

    if [ -n "$leftover" ]; then
        echo "cleanup: WARNING — leftover build/test processes still present:"
        echo "$leftover"
        if [ "$strict" = 1 ] && [ "$dryrun" != 1 ]; then
            echo "cleanup: ERROR — strict leftover check failed (no reusable" \
                 "build servers allowed to survive the job)"
            return 1
        fi
        return 0
    fi

    echo "cleanup: verified no leftover dotnet/testhost/VBCSCompiler/MSBuild processes"
    return 0
}

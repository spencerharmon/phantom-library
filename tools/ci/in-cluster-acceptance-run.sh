#!/usr/bin/env bash
# tools/ci/in-cluster-acceptance-run.sh
#
# P3 Stage 5 — the in-cluster acceptance rig. Unlike tools/ci/live-rig-run.sh
# (which stands up the plugin's OWN throwaway Jellyfin against built images),
# this script proves the ACTUAL deployed phantom-library-bluegreen-deploy
# stack on the target cluster: the real dev/idle color Pod, its co-located
# gostream FUSE mount, and its shared-Postgres plugin state
# (jellyfin_dev/phantom_dev — the per-color sqlite topology was retired by the
# 2026-08-25 cutover).
#
# It NEVER touches the production color. The production apex host is refused
# outright (see the guard below); only the color that is currently the
# inactive/dev role may be driven.
#
# Everything here is READ-mostly against real deployed data, plus a scoped,
# rig-owned, fully torn-down mutation footprint:
#   - one rig-only Jellyfin ApiKey row (minted directly via the plugin's own
#     Postgres-backed ApiKeys table — functionally identical to the admin API
#     minting an API key, just issued without an interactive admin session;
#     see docs/tasks/in-cluster-acceptance-rig.md "Why a DB-minted API key")
#   - two rig-only non-admin Jellyfin users (per-user isolation proof)
#   - a transient per-user hidden-item row, immediately unhidden
# All of the above are deleted by the EXIT trap unconditionally — a mid-run
# failure never leaves rig users, rig prefs, or a rig API key behind.
#
# Knobs (env):
#   PHANTOM_INCLUSTER_ADMIN_TOKEN   a Jellyfin admin ApiKey/AccessToken already
#                                    minted on the target color (preferred —
#                                    see provision-in-cluster-rig-creds.md). If
#                                    unset, the rig mints + retires its OWN
#                                    rig-only key via the DB (PHANTOM_INCLUSTER_KUBE_CONTEXT
#                                    reach required either way for the FUSE/DB
#                                    assertions).
#   PHANTOM_INCLUSTER_DEV_HOST       the current inactive/dev role's public
#                                    host (e.g. dev.jellyfin.example.com).
#   PHANTOM_INCLUSTER_PROD_HOST      the apex/prod host — refused as a target,
#                                    used only for the safety guard.
#   PHANTOM_INCLUSTER_NAMESPACE      k8s namespace (default: phantom-library).
#   PHANTOM_INCLUSTER_KUBE_CONTEXT   kubectl context/kubeconfig reaching the
#                                    target cluster (default: current context).
#   PHANTOM_CI_DRYRUN=1              echo the heavy/mutating steps instead of
#                                    running them (toolchain-agnostic; no
#                                    cluster/network access needed). Used by
#                                    the in-repo regression check.
#
# Exit non-zero on ANY assertion failure. Prints a `RESULT:` line per
# scenario so the change doc can quote pass/fail evidence directly.
set -euo pipefail

REPO_ROOT="${PHANTOM_REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$REPO_ROOT"

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
NAMESPACE="${PHANTOM_INCLUSTER_NAMESPACE:-phantom-library}"
DEV_HOST="${PHANTOM_INCLUSTER_DEV_HOST:-}"
PROD_HOST="${PHANTOM_INCLUSTER_PROD_HOST:-}"
KCTX=()
[ -n "${PHANTOM_INCLUSTER_KUBE_CONTEXT:-}" ] && KCTX=(--context "$PHANTOM_INCLUSTER_KUBE_CONTEXT")

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
result() { printf 'RESULT: %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

kube() { kubectl "${KCTX[@]}" -n "$NAMESPACE" "$@"; }

log "phantom-library in-cluster acceptance rig"
note "namespace:  $NAMESPACE"
note "dev host:   ${DEV_HOST:-<unset>}"
note "prod host:  ${PROD_HOST:-<unset>}"
note "dry run:    $DRYRUN"

# --- prod safety guard ---------------------------------------------------
# Never let a misconfigured env point this rig at the apex/prod host. This
# is a hard string-equality refusal — cheap, unconditional, checked first.
if [ -n "$DEV_HOST" ] && [ -n "$PROD_HOST" ] && [ "$DEV_HOST" = "$PROD_HOST" ]; then
    fail "PHANTOM_INCLUSTER_DEV_HOST equals PHANTOM_INCLUSTER_PROD_HOST ($DEV_HOST) — refusing to drive the rig at what may be the production apex."
fi

if [ "$DRYRUN" = 1 ]; then
    [ -n "$DEV_HOST" ] || DEV_HOST="dev.example.com"
fi
[ -n "$DEV_HOST" ] || fail "PHANTOM_INCLUSTER_DEV_HOST is required"

# --- resolve which color is the dev host, LIVE from the Ingress ----------
# Per GATES.md THE AUTHORITY: never infer color from a CNAME/comment/label —
# resolve it from the live Ingress Host rules for the target namespace.
resolve_color() {
    local host="$1" color
    if [ "$DRYRUN" = 1 ]; then
        echo "green"
        return 0
    fi
    for color in blue green; do
        if kube get ingress "phantom-library-$color" \
            -o jsonpath='{range .spec.rules[*]}{.host}{"\n"}{end}' 2>/dev/null \
            | grep -qx "$host"; then
            echo "$color"
            return 0
        fi
    done
    return 1
}

log "resolving dev color from the live Ingress (never a cached/CNAME guess)"
COLOR="$(resolve_color "$DEV_HOST")" || fail "no phantom-library-{blue,green} Ingress carries host $DEV_HOST — cannot resolve the dev color live."
note "dev host $DEV_HOST -> color=$COLOR"

# Refuse outright if the resolved color ALSO carries the apex/prod host —
# that would mean DEV_HOST is misconfigured to point at prod.
if [ "$DRYRUN" != 1 ] && [ -n "$PROD_HOST" ]; then
    kube get ingress "phantom-library-$COLOR" \
        -o jsonpath='{range .spec.rules[*]}{.host}{"\n"}{end}' 2>/dev/null \
        | grep -qx "$PROD_HOST" \
        && fail "resolved color '$COLOR' ALSO carries the apex/prod host ($PROD_HOST) — refusing (this would be the active/prod color)."
fi

POD=""
find_pod() {
    if [ "$DRYRUN" = 1 ]; then
        POD="phantom-library-$COLOR-dryrun-pod"
        return 0
    fi
    POD="$(kube get pods -l "app.kubernetes.io/instance=phantom-library-$COLOR" \
        -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
    [ -n "$POD" ] || POD="$(kube get pods -o name 2>/dev/null | grep "phantom-library-$COLOR-[0-9a-f]\{6,10\}-" | head -1 | sed 's#^pod/##')"
    [ -n "$POD" ] || return 1
}

kpod() {
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: kubectl -n $NAMESPACE exec $POD -c jellyfin -- $*"
        return 0
    fi
    kube exec "$POD" -c jellyfin -- "$@"
}

# --- guaranteed rig-state cleanup, always runs ---------------------------
RIG_UID_A=""; RIG_UID_B=""; RIG_KEY_NAME=""; RIG_TOKEN_MINTED=""
_torn_down=0
teardown() {
    local ec=$?
    [ "$_torn_down" = 1 ] && exit "$ec"
    _torn_down=1
    log "tearing down rig-owned state (guaranteed EXIT trap)"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: delete rig users + rig API key (nothing minted in a dry run)"
        exit "$ec"
    fi
    if [ -n "${ADMIN_TOKEN:-}" ]; then
        for uid in "$RIG_UID_A" "$RIG_UID_B"; do
            [ -n "$uid" ] || continue
            kpod curl -s -o /dev/null -X DELETE -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Users/$uid" || true
        done
    fi
    if [ -n "$RIG_TOKEN_MINTED" ] && [ -n "$RIG_KEY_NAME" ]; then
        kpod sh -c "PGPASSWORD=\$POSTGRES_PASSWORD psql -h \$POSTGRES_HOST -U \$POSTGRES_USER -d \$POSTGRES_DB -c \"DELETE FROM \\\"ApiKeys\\\" WHERE \\\"Name\\\"='$RIG_KEY_NAME'\"" || true
    fi
    exit "$ec"
}
trap teardown EXIT

log "locating the $COLOR color's Jellyfin Pod"
find_pod || fail "no Running phantom-library-$COLOR Pod found in namespace $NAMESPACE"
note "pod: $POD"

# =========================================================================
# 1. rollout Ready
# =========================================================================
log "[1] deployment rollout status"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: kubectl rollout status deploy/phantom-library-$COLOR"
    result "rollout: DRYRUN skipped"
else
    kube rollout status "deploy/phantom-library-$COLOR" --timeout=60s \
        || fail "phantom-library-$COLOR deployment did not report Ready"
    result "rollout: phantom-library-$COLOR Ready"
fi

# =========================================================================
# 2. HTTPS + cert SAN on the dev host
# =========================================================================
log "[2] HTTPS + certificate SAN on $DEV_HOST"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: openssl s_client -connect $DEV_HOST:443 | openssl x509 -noout -ext subjectAltName"
    result "tls: DRYRUN skipped"
else
    SAN="$(echo | openssl s_client -connect "$DEV_HOST:443" -servername "$DEV_HOST" 2>/dev/null \
        | openssl x509 -noout -ext subjectAltName 2>/dev/null || true)"
    [ -n "$SAN" ] || fail "could not read a certificate / SAN from https://$DEV_HOST:443"
    printf '%s\n' "$SAN" | grep -qE "DNS:(\*\.)?[A-Za-z0-9.-]*$(printf '%s' "$DEV_HOST" | sed -E 's/^[^.]+\.//')" \
        || fail "certificate SAN does not cover $DEV_HOST: $SAN"
    HTTP_CODE="$(curl -s -o /dev/null -w '%{http_code}' "https://$DEV_HOST/System/Info/Public")"
    [ "$HTTP_CODE" = "200" ] || fail "https://$DEV_HOST/System/Info/Public returned $HTTP_CODE, expected 200"
    result "tls: valid HTTPS + SAN covers $DEV_HOST; /System/Info/Public=200"
fi

# =========================================================================
# 3. gostream FUSE live mountpoint co-located in the Pod
# =========================================================================
log "[3] gostream virtual-MKV FUSE mountpoint"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: kubectl exec $POD -c jellyfin -- mountpoint \$GOSTREAM_MOUNT_PATH"
    result "fuse: DRYRUN skipped"
else
    MOUNT_PATH="$(kpod sh -c 'echo "$GOSTREAM_MOUNT_PATH"')"
    [ -n "$MOUNT_PATH" ] || fail "GOSTREAM_MOUNT_PATH not set in the $COLOR Pod's jellyfin container env"
    kpod mountpoint "$MOUNT_PATH" || fail "$MOUNT_PATH is not a live mountpoint inside $POD"
    result "fuse: $MOUNT_PATH is a live mountpoint in $POD"
fi

# =========================================================================
# 4. shared-Postgres plugin state (never the retired per-color sqlite)
# =========================================================================
log "[4] shared-Postgres plugin schema (phantom_dev)"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: psql -c \"select to_regclass('user_hidden_items'), to_regclass('user_prefs')\""
    result "postgres: DRYRUN skipped"
else
    TABLES="$(kpod sh -c 'PGPASSWORD="$PHANTOM_POSTGRES_PASSWORD" psql -h "$PHANTOM_POSTGRES_HOST" -U "$PHANTOM_POSTGRES_USER" -d "$PHANTOM_POSTGRES_DB" -tAc "select to_regclass('"'"'public.user_hidden_items'"'"'), to_regclass('"'"'public.user_prefs'"'"')"')"
    printf '%s' "$TABLES" | grep -q 'user_hidden_items' || fail "phantom_dev.user_hidden_items not found via to_regclass (still on retired sqlite topology?): '$TABLES'"
    printf '%s' "$TABLES" | grep -q 'user_prefs' || fail "phantom_dev.user_prefs not found via to_regclass: '$TABLES'"
    result "postgres: phantom_dev.user_hidden_items + user_prefs present (shared-Postgres topology confirmed)"
fi

# =========================================================================
# 5. admin API access (provided token, or DB-minted rig-only fallback)
# =========================================================================
log "[5] admin API access"
ADMIN_TOKEN="${PHANTOM_INCLUSTER_ADMIN_TOKEN:-}"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: mint/verify admin ApiKey"
    ADMIN_TOKEN="dryrun-token"
    result "admin-api: DRYRUN skipped"
elif [ -n "$ADMIN_TOKEN" ]; then
    code="$(kpod curl -s -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Users)"
    [ "$code" = "200" ] || fail "provided PHANTOM_INCLUSTER_ADMIN_TOKEN rejected by /Users (http=$code)"
    result "admin-api: provided token accepted (200)"
else
    RIG_KEY_NAME="phantom-icar-$(date +%s)"
    RIG_TOKEN_MINTED="$(head -c16 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    kpod sh -c "PGPASSWORD=\$POSTGRES_PASSWORD psql -h \$POSTGRES_HOST -U \$POSTGRES_USER -d \$POSTGRES_DB -c \"INSERT INTO \\\"ApiKeys\\\" (\\\"DateCreated\\\",\\\"DateLastActivity\\\",\\\"Name\\\",\\\"AccessToken\\\") VALUES (now(), now(), '$RIG_KEY_NAME', '$RIG_TOKEN_MINTED')\"" \
        || fail "could not mint a rig-only ApiKeys row in jellyfin_dev"
    ADMIN_TOKEN="$RIG_TOKEN_MINTED"
    code="$(kpod curl -s -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Users)"
    [ "$code" = "200" ] || fail "DB-minted rig ApiKey rejected by /Users (http=$code)"
    result "admin-api: rig-minted key '$RIG_KEY_NAME' accepted (200); torn down at exit"
fi

# =========================================================================
# 6. scenario 35/42 parity: movie e2e resolve + per-user show/hide (live, real catalog)
# =========================================================================
log "[6] movie channel + per-user show/hide (REQ-M14-PER-USER, live catalog)"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: provision rig-only users A/B, hide/unhide a real catalog movie, assert isolation"
    result "movie+per-user: DRYRUN skipped"
else
    CH_MOVIES="$(kpod curl -s -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Channels \
        | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((c['Id'] for c in d['Items'] if c.get('Name')=='Phantom Movies'),''))")"
    [ -n "$CH_MOVIES" ] || fail "Phantom Movies channel not registered on the deployed stack"

    read -r MOVIE_ID MOVIE_TMDB <<<"$(kpod curl -s -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Channels/$CH_MOVIES/Items?Limit=1&Fields=ProviderIds" \
        | python3 -c "import json,sys; d=json.load(sys.stdin); i=d['Items'][0]; print(i['Id'], (i.get('ProviderIds') or {}).get('Tmdb',''))")"
    [ -n "$MOVIE_ID" ] && [ -n "$MOVIE_TMDB" ] || fail "could not resolve a real movie + tmdb id from the deployed Phantom Movies channel"
    note "using real catalog movie tmdb=$MOVIE_TMDB item=$MOVIE_ID"

    uidfor() {
        local name=$1
        kpod curl -s -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
            -d "{\"Name\":\"$name\"}" http://localhost:8096/Users/New \
            | python3 -c "import json,sys; print(json.load(sys.stdin).get('Id',''))"
    }
    RIG_UID_A="$(uidfor "phantom-icar-a-$$")"
    RIG_UID_B="$(uidfor "phantom-icar-b-$$")"
    [ -n "$RIG_UID_A" ] && [ -n "$RIG_UID_B" ] || fail "could not provision rig-only users A/B"
    kpod curl -s -o /dev/null -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
        -d "{\"Id\":\"$RIG_UID_A\",\"CurrentPw\":\"\",\"NewPw\":\"rigpass-a-$$\"}" "http://localhost:8096/Users/$RIG_UID_A/Password"
    kpod curl -s -o /dev/null -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
        -d "{\"Id\":\"$RIG_UID_B\",\"CurrentPw\":\"\",\"NewPw\":\"rigpass-b-$$\"}" "http://localhost:8096/Users/$RIG_UID_B/Password"

    TOK_A="$(kpod curl -s -X POST -H 'Content-Type: application/json' \
        -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-icar\", Device=\"rig-a\", DeviceId=\"rig-a-$$\", Version=\"1.0\"" \
        -d "{\"Username\":\"phantom-icar-a-$$\",\"Pw\":\"rigpass-a-$$\"}" http://localhost:8096/Users/AuthenticateByName \
        | python3 -c "import json,sys; print(json.load(sys.stdin).get('AccessToken',''))")"
    TOK_B="$(kpod curl -s -X POST -H 'Content-Type: application/json' \
        -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-icar\", Device=\"rig-b\", DeviceId=\"rig-b-$$\", Version=\"1.0\"" \
        -d "{\"Username\":\"phantom-icar-b-$$\",\"Pw\":\"rigpass-b-$$\"}" http://localhost:8096/Users/AuthenticateByName \
        | python3 -c "import json,sys; print(json.load(sys.stdin).get('AccessToken',''))")"
    [ -n "$TOK_A" ] && [ -n "$TOK_B" ] || fail "could not authenticate rig-only users A/B"

    has_tmdb() {
        local tok=$1
        kpod curl -s -H "X-Emby-Token: $tok" "http://localhost:8096/Channels/$CH_MOVIES/Items?Limit=200&Fields=ProviderIds" \
            | python3 -c "import json,sys
d=json.load(sys.stdin)
print('1' if any(str((i.get('ProviderIds') or {}).get('Tmdb'))=='$MOVIE_TMDB' for i in d['Items']) else '0')"
    }
    [ "$(has_tmdb "$TOK_A")" = "1" ] || fail "baseline: real movie not visible to rig user A"
    [ "$(has_tmdb "$TOK_B")" = "1" ] || fail "baseline: real movie not visible to rig user B"

    hide_code="$(kpod curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Emby-Token: $TOK_A" "http://localhost:8096/Plugins/PhantomLibrary/User/Hidden/movie/$MOVIE_TMDB")"
    [ "$hide_code" = "204" ] || fail "A hide movie returned $hide_code, expected 204"
    [ "$(has_tmdb "$TOK_A")" = "0" ] || fail "after A hides: movie still visible to A"
    [ "$(has_tmdb "$TOK_B")" = "1" ] || fail "after A hides: movie no longer visible to B (cross-user leak)"

    unhide_code="$(kpod curl -s -o /dev/null -w '%{http_code}' -X DELETE -H "X-Emby-Token: $TOK_A" "http://localhost:8096/Plugins/PhantomLibrary/User/Hidden/movie/$MOVIE_TMDB")"
    [ "$unhide_code" = "204" ] || fail "A unhide movie returned $unhide_code, expected 204"
    [ "$(has_tmdb "$TOK_A")" = "1" ] || fail "after A unhides: movie did not return to A's browse"

    result "movie+per-user: real catalog movie tmdb=$MOVIE_TMDB hide/unhide isolated live (A hidden, B unaffected, restored)"

    # -------------------- scenario 35 parity: real playback resolve -----
    PB_CODE="$(kpod curl -s -o /dev/null -w '%{http_code}' -m 20 -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
        -d '{}' "http://localhost:8096/Items/$MOVIE_ID/PlaybackInfo")"
    [ "$PB_CODE" = "200" ] || fail "PlaybackInfo for real catalog movie returned $PB_CODE, expected 200"
    result "scenario-35-parity: PlaybackInfo resolves live (200) for real catalog movie $MOVIE_ID"
fi

# =========================================================================
# 7. scenario 36 parity: TV series/season drill on real catalog data
# =========================================================================
log "[7] TV series/season drill (real catalog, scenario 36 parity)"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: resolve Phantom Shows channel, drill a real series -> season(s)"
    result "tv-drill: DRYRUN skipped"
else
    CH_SHOWS="$(kpod curl -s -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Channels \
        | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((c['Id'] for c in d['Items'] if c.get('Name')=='Phantom Shows'),''))")"
    [ -n "$CH_SHOWS" ] || fail "Phantom Shows channel not registered on the deployed stack"

    SERIES_ID="$(kpod curl -s -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Channels/$CH_SHOWS/Items?Limit=1" \
        | python3 -c "import json,sys; print(json.load(sys.stdin)['Items'][0]['Id'])")"
    [ -n "$SERIES_ID" ] || fail "could not resolve a real series from the deployed Phantom Shows channel"

    CHILDREN="$(kpod curl -s -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Channels/$CH_SHOWS/Items?Limit=200&FolderId=$SERIES_ID" \
        | python3 -c "import json,sys; print(json.load(sys.stdin).get('TotalRecordCount',0))")"
    [ "${CHILDREN:-0}" -ge 1 ] || fail "real series $SERIES_ID expanded to $CHILDREN season/episode children, expected >=1"
    result "tv-drill: real series $SERIES_ID drills to $CHILDREN season(s) live"
fi

log "in-cluster acceptance rig PASSED against $COLOR ($DEV_HOST)"
# EXIT trap tears down rig-owned users/key next.

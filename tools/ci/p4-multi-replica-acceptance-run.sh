#!/usr/bin/env bash
# tools/ci/p4-multi-replica-acceptance-run.sh
#
# P4 acceptance bar — retargets/extends tools/ci/in-cluster-acceptance-run.sh
# (P3 Stage 5) at a color running >=2 Jellyfin+gostream replicas (StatefulSet
# .spec.replicas > 1, see deploy/helm/phantom-library/templates/statefulset.yaml)
# instead of the single-replica case. Proves that Stage A (shared Postgres —
# jellyfin_dev via Jellyfin.Pgsql AND phantom_dev via the plugin's own
# Postgres-backed provider) + Stage B (consolidated single-container
# co-located gostream, one per replica) actually deliver horizontal scale
# WITHOUT single-writer corruption — not just that the single-replica rig
# passes.
#
# Reuses every P3 Stage 5 assertion (rollout Ready, HTTPS+SAN, admin API,
# movie/TV channel e2e, per-user show/hide) unchanged, and ADDS the
# multi-replica-specific proofs that a single-Pod rig cannot make:
#   - the color's StatefulSet actually runs >=PHANTOM_MULTI_MIN_REPLICAS Ready
#     replicas (never silently degrades to N=1 and still "passes").
#   - EVERY replica Pod (not just Pod 0) has its OWN live gostream FUSE
#     mountpoint (proves per-replica co-location, not one shared mount).
#   - cross-replica write visibility: a per-user hide mutation issued
#     directly against replica 0's own localhost API is immediately visible
#     when read directly against replica N-1's own localhost API — proving
#     the shared-Postgres state is the single source of truth and no
#     replica-local cache/writer diverges (the "no single-writer
#     corruption" bar).
#   - fan-out playback: PlaybackInfo for the same real catalog movie
#     resolves 200 when hit directly against EVERY replica Pod (never just
#     through the load-balanced Service, which could mask one broken
#     replica).
#
# Knobs (env):
#   (all of tools/ci/in-cluster-acceptance-run.sh's knobs apply unchanged)
#   PHANTOM_MULTI_MIN_REPLICAS   minimum Ready replica count required
#                                 (default: 2).
#   PHANTOM_CI_DRYRUN=1           echo the heavy/mutating steps instead of
#                                 running them (toolchain-agnostic; no
#                                 cluster/network access needed).
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
MIN_REPLICAS="${PHANTOM_MULTI_MIN_REPLICAS:-2}"
KCTX=()
[ -n "${PHANTOM_INCLUSTER_KUBE_CONTEXT:-}" ] && KCTX=(--context "$PHANTOM_INCLUSTER_KUBE_CONTEXT")

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
result() { printf 'RESULT: %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

kube() { kubectl "${KCTX[@]}" -n "$NAMESPACE" "$@"; }

log "phantom-library P4 multi-replica acceptance rig"
note "namespace:     $NAMESPACE"
note "dev host:      ${DEV_HOST:-<unset>}"
note "prod host:     ${PROD_HOST:-<unset>}"
note "min replicas:  $MIN_REPLICAS"
note "dry run:       $DRYRUN"

# --- prod safety guard ---------------------------------------------------
if [ -n "$DEV_HOST" ] && [ -n "$PROD_HOST" ] && [ "$DEV_HOST" = "$PROD_HOST" ]; then
    fail "PHANTOM_INCLUSTER_DEV_HOST equals PHANTOM_INCLUSTER_PROD_HOST ($DEV_HOST) — refusing to drive the rig at what may be the production apex."
fi

if [ "$DRYRUN" = 1 ]; then
    [ -n "$DEV_HOST" ] || DEV_HOST="dev.example.com"
fi
[ -n "$DEV_HOST" ] || fail "PHANTOM_INCLUSTER_DEV_HOST is required"

# --- resolve which color is the dev host, LIVE from the Ingress ----------
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

if [ "$DRYRUN" != 1 ] && [ -n "$PROD_HOST" ]; then
    kube get ingress "phantom-library-$COLOR" \
        -o jsonpath='{range .spec.rules[*]}{.host}{"\n"}{end}' 2>/dev/null \
        | grep -qx "$PROD_HOST" \
        && fail "resolved color '$COLOR' ALSO carries the apex/prod host ($PROD_HOST) — refusing (this would be the active/prod color)."
fi

STS="phantom-library-$COLOR"
declare -a PODS=()
find_pods() {
    if [ "$DRYRUN" = 1 ]; then
        PODS=("$STS-0" "$STS-1")
        return 0
    fi
    local ready spec want
    spec="$(kube get statefulset "$STS" -o jsonpath='{.spec.replicas}' 2>/dev/null || true)"
    [ -n "$spec" ] || return 1
    ready="$(kube get statefulset "$STS" -o jsonpath='{.status.readyReplicas}' 2>/dev/null || echo 0)"
    want="$spec"
    local i
    PODS=()
    for ((i = 0; i < want; i++)); do
        PODS+=("$STS-$i")
    done
    echo "$ready"
}

kpodn() {
    local pod="$1"; shift
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: kubectl -n $NAMESPACE exec $pod -c jellyfin -- $*"
        return 0
    fi
    kube exec "$pod" -c jellyfin -- "$@"
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
    local anchor="${PODS[0]:-}"
    if [ -n "${ADMIN_TOKEN:-}" ] && [ -n "$anchor" ]; then
        for uid in "$RIG_UID_A" "$RIG_UID_B"; do
            [ -n "$uid" ] || continue
            kpodn "$anchor" curl -s -o /dev/null -X DELETE -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Users/$uid" || true
        done
    fi
    if [ -n "$RIG_TOKEN_MINTED" ] && [ -n "$RIG_KEY_NAME" ] && [ -n "$anchor" ]; then
        kpodn "$anchor" sh -c "PGPASSWORD=\$PHANTOM_POSTGRES_PASSWORD psql -h \$PHANTOM_POSTGRES_HOST -U \$PHANTOM_POSTGRES_USER -d \$PHANTOM_POSTGRES_DB -c \"DELETE FROM \\\"ApiKeys\\\" WHERE \\\"Name\\\"='$RIG_KEY_NAME'\"" || true
    fi
    exit "$ec"
}
trap teardown EXIT

# =========================================================================
# 1. multi-replica rollout: StatefulSet reports >= MIN_REPLICAS Ready
# =========================================================================
log "[1] multi-replica rollout status ($STS, requiring >=$MIN_REPLICAS Ready)"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: kubectl rollout status statefulset/$STS; kubectl get statefulset $STS -o jsonpath=readyReplicas"
    find_pods >/dev/null
    result "rollout: DRYRUN skipped (assuming ${#PODS[@]} replicas)"
else
    kube rollout status "statefulset/$STS" --timeout=120s \
        || fail "$STS StatefulSet did not report Ready"
    READY="$(find_pods)"
    [ -n "$READY" ] || fail "could not read $STS StatefulSet .spec.replicas / .status.readyReplicas"
    [ "${#PODS[@]}" -ge "$MIN_REPLICAS" ] || fail "$STS declares ${#PODS[@]} replicas, expected >=$MIN_REPLICAS for the P4 multi-replica bar"
    [ "$READY" -ge "$MIN_REPLICAS" ] || fail "$STS reports readyReplicas=$READY, expected >=$MIN_REPLICAS"
    result "rollout: $STS Ready with $READY/${#PODS[@]} replicas (>=$MIN_REPLICAS required)"
fi
note "replica pods: ${PODS[*]}"

# =========================================================================
# 2. HTTPS + cert SAN on the dev host (fronting the multi-replica Service)
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
# 3. per-replica gostream FUSE co-location — EVERY replica, not just one
# =========================================================================
log "[3] gostream virtual-MKV FUSE mountpoint on EVERY replica"
if [ "$DRYRUN" = 1 ]; then
    for p in "${PODS[@]}"; do
        note "DRYRUN: kubectl exec $p -c jellyfin -- mountpoint \$GOSTREAM_MOUNT_PATH"
    done
    result "fuse: DRYRUN skipped for ${#PODS[@]} replicas"
else
    for p in "${PODS[@]}"; do
        MOUNT_PATH="$(kpodn "$p" sh -c 'echo "$GOSTREAM_MOUNT_PATH"')"
        [ -n "$MOUNT_PATH" ] || fail "GOSTREAM_MOUNT_PATH not set in $p's jellyfin container env"
        kpodn "$p" mountpoint "$MOUNT_PATH" || fail "$MOUNT_PATH is not a live mountpoint inside $p"
        note "$p: $MOUNT_PATH is a live mountpoint (own co-located FUSE instance)"
    done
    result "fuse: all ${#PODS[@]} replicas have their OWN live gostream FUSE mountpoint"
fi

# =========================================================================
# 4. shared-Postgres plugin state visible identically from every replica
# =========================================================================
log "[4] shared-Postgres plugin schema (phantom_dev) from every replica"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: psql -c \"select to_regclass('user_hidden_items'), to_regclass('user_prefs')\" from each replica"
    result "postgres: DRYRUN skipped"
else
    for p in "${PODS[@]}"; do
        TABLES="$(kpodn "$p" sh -c 'PGPASSWORD="$PHANTOM_POSTGRES_PASSWORD" psql -h "$PHANTOM_POSTGRES_HOST" -U "$PHANTOM_POSTGRES_USER" -d "$PHANTOM_POSTGRES_DB" -tAc "select to_regclass('"'"'public.user_hidden_items'"'"'), to_regclass('"'"'public.user_prefs'"'"')"')"
        printf '%s' "$TABLES" | grep -q 'user_hidden_items' || fail "$p: phantom_dev.user_hidden_items not found via to_regclass"
        printf '%s' "$TABLES" | grep -q 'user_prefs' || fail "$p: phantom_dev.user_prefs not found via to_regclass"
    done
    result "postgres: phantom_dev.user_hidden_items + user_prefs present identically from all ${#PODS[@]} replicas (single shared DB, no per-replica drift)"
fi

# =========================================================================
# 5. admin API access (provided token, or DB-minted rig-only fallback)
# =========================================================================
log "[5] admin API access"
ADMIN_TOKEN="${PHANTOM_INCLUSTER_ADMIN_TOKEN:-}"
ANCHOR="${PODS[0]:-$STS-0}"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: mint/verify admin ApiKey"
    ADMIN_TOKEN="dryrun-token"
    result "admin-api: DRYRUN skipped"
elif [ -n "$ADMIN_TOKEN" ]; then
    code="$(kpodn "$ANCHOR" curl -s -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Users)"
    [ "$code" = "200" ] || fail "provided PHANTOM_INCLUSTER_ADMIN_TOKEN rejected by /Users (http=$code)"
    result "admin-api: provided token accepted (200)"
else
    RIG_KEY_NAME="phantom-p4mr-$(date +%s)"
    RIG_TOKEN_MINTED="$(head -c16 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    kpodn "$ANCHOR" sh -c "PGPASSWORD=\$PHANTOM_POSTGRES_PASSWORD psql -h \$PHANTOM_POSTGRES_HOST -U \$PHANTOM_POSTGRES_USER -d \$PHANTOM_POSTGRES_DB -c \"INSERT INTO \\\"ApiKeys\\\" (\\\"DateCreated\\\",\\\"DateLastActivity\\\",\\\"Name\\\",\\\"AccessToken\\\") VALUES (now(), now(), '$RIG_KEY_NAME', '$RIG_TOKEN_MINTED')\"" \
        || fail "could not mint a rig-only ApiKeys row in jellyfin_dev via $ANCHOR"
    ADMIN_TOKEN="$RIG_TOKEN_MINTED"
    code="$(kpodn "$ANCHOR" curl -s -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Users)"
    [ "$code" = "200" ] || fail "DB-minted rig ApiKey rejected by /Users (http=$code) on $ANCHOR"
    # Because ApiKeys lives in the ONE shared jellyfin_dev DB, the same key must
    # authenticate against every OTHER replica too — first cross-replica proof.
    for p in "${PODS[@]}"; do
        [ "$p" = "$ANCHOR" ] && continue
        code2="$(kpodn "$p" curl -s -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Users)"
        [ "$code2" = "200" ] || fail "rig-minted key (minted via $ANCHOR) rejected by $p's /Users (http=$code2) — shared-DB auth not consistent across replicas"
    done
    result "admin-api: rig-minted key '$RIG_KEY_NAME' (minted via $ANCHOR) accepted (200) on ALL ${#PODS[@]} replicas; torn down at exit"
fi

# =========================================================================
# 6. movie e2e + per-user show/hide (REQ-M14-PER-USER, live catalog)
# =========================================================================
log "[6] movie channel + per-user show/hide (live catalog, via Service)"
MOVIE_TMDB=""
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: provision rig-only users A/B, hide/unhide a real catalog movie, assert isolation"
    result "movie+per-user: DRYRUN skipped"
else
    CH_MOVIES="$(kpodn "$ANCHOR" curl -s -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Channels \
        | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((c['Id'] for c in d['Items'] if c.get('Name')=='Phantom Movies'),''))")"
    [ -n "$CH_MOVIES" ] || fail "Phantom Movies channel not registered on the deployed stack"

    read -r MOVIE_ID MOVIE_TMDB <<<"$(kpodn "$ANCHOR" curl -s -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Channels/$CH_MOVIES/Items?Limit=1&Fields=ProviderIds" \
        | python3 -c "import json,sys; d=json.load(sys.stdin); i=d['Items'][0]; print(i['Id'], (i.get('ProviderIds') or {}).get('Tmdb',''))")"
    [ -n "$MOVIE_ID" ] && [ -n "$MOVIE_TMDB" ] || fail "could not resolve a real movie + tmdb id from the deployed Phantom Movies channel"
    note "using real catalog movie tmdb=$MOVIE_TMDB item=$MOVIE_ID"

    uidfor() {
        local name=$1
        kpodn "$ANCHOR" curl -s -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
            -d "{\"Name\":\"$name\"}" http://localhost:8096/Users/New \
            | python3 -c "import json,sys; print(json.load(sys.stdin).get('Id',''))"
    }
    RIG_UID_A="$(uidfor "phantom-p4mr-a-$$")"
    RIG_UID_B="$(uidfor "phantom-p4mr-b-$$")"
    [ -n "$RIG_UID_A" ] && [ -n "$RIG_UID_B" ] || fail "could not provision rig-only users A/B"
    kpodn "$ANCHOR" curl -s -o /dev/null -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
        -d "{\"Id\":\"$RIG_UID_A\",\"CurrentPw\":\"\",\"NewPw\":\"rigpass-a-$$\"}" "http://localhost:8096/Users/$RIG_UID_A/Password"
    kpodn "$ANCHOR" curl -s -o /dev/null -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
        -d "{\"Id\":\"$RIG_UID_B\",\"CurrentPw\":\"\",\"NewPw\":\"rigpass-b-$$\"}" "http://localhost:8096/Users/$RIG_UID_B/Password"

    TOK_A="$(kpodn "$ANCHOR" curl -s -X POST -H 'Content-Type: application/json' \
        -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-p4mr\", Device=\"rig-a\", DeviceId=\"rig-a-$$\", Version=\"1.0\"" \
        -d "{\"Username\":\"phantom-p4mr-a-$$\",\"Pw\":\"rigpass-a-$$\"}" http://localhost:8096/Users/AuthenticateByName \
        | python3 -c "import json,sys; print(json.load(sys.stdin).get('AccessToken',''))")"
    TOK_B="$(kpodn "$ANCHOR" curl -s -X POST -H 'Content-Type: application/json' \
        -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-p4mr\", Device=\"rig-b\", DeviceId=\"rig-b-$$\", Version=\"1.0\"" \
        -d "{\"Username\":\"phantom-p4mr-b-$$\",\"Pw\":\"rigpass-b-$$\"}" http://localhost:8096/Users/AuthenticateByName \
        | python3 -c "import json,sys; print(json.load(sys.stdin).get('AccessToken',''))")"
    [ -n "$TOK_A" ] && [ -n "$TOK_B" ] || fail "could not authenticate rig-only users A/B"

    has_tmdb() {
        local pod=$1 tok=$2
        kpodn "$pod" curl -s -H "X-Emby-Token: $tok" "http://localhost:8096/Channels/$CH_MOVIES/Items?Limit=200&Fields=ProviderIds" \
            | python3 -c "import json,sys
d=json.load(sys.stdin)
print('1' if any(str((i.get('ProviderIds') or {}).get('Tmdb'))=='$MOVIE_TMDB' for i in d['Items']) else '0')"
    }
    [ "$(has_tmdb "$ANCHOR" "$TOK_A")" = "1" ] || fail "baseline: real movie not visible to rig user A"
    [ "$(has_tmdb "$ANCHOR" "$TOK_B")" = "1" ] || fail "baseline: real movie not visible to rig user B"

    hide_code="$(kpodn "$ANCHOR" curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Emby-Token: $TOK_A" "http://localhost:8096/Plugins/PhantomLibrary/User/Hidden/movie/$MOVIE_TMDB")"
    [ "$hide_code" = "204" ] || fail "A hide movie (via $ANCHOR) returned $hide_code, expected 204"
    [ "$(has_tmdb "$ANCHOR" "$TOK_A")" = "0" ] || fail "after A hides (via $ANCHOR): movie still visible to A"
    [ "$(has_tmdb "$ANCHOR" "$TOK_B")" = "1" ] || fail "after A hides: movie no longer visible to B (cross-user leak)"

    # -------- cross-replica write visibility (the "no single-writer     --
    # -------- corruption" bar): the hide was written via $ANCHOR; every --
    # -------- OTHER replica must see it too via ITS OWN localhost API,  --
    # -------- proving state lives in shared Postgres, not a replica-    --
    # -------- local cache/writer.                                      --
    for p in "${PODS[@]}"; do
        [ "$p" = "$ANCHOR" ] && continue
        [ "$(has_tmdb "$p" "$TOK_A")" = "0" ] || fail "cross-replica corruption: movie hidden via $ANCHOR is STILL visible to A via $p (state not shared / single-writer corruption)"
        [ "$(has_tmdb "$p" "$TOK_B")" = "1" ] || fail "cross-replica corruption: movie hidden by A leaked to B when read via $p"
    done

    unhide_code="$(kpodn "$ANCHOR" curl -s -o /dev/null -w '%{http_code}' -X DELETE -H "X-Emby-Token: $TOK_A" "http://localhost:8096/Plugins/PhantomLibrary/User/Hidden/movie/$MOVIE_TMDB")"
    [ "$unhide_code" = "204" ] || fail "A unhide movie (via $ANCHOR) returned $unhide_code, expected 204"
    [ "$(has_tmdb "$ANCHOR" "$TOK_A")" = "1" ] || fail "after A unhides: movie did not return to A's browse via $ANCHOR"
    for p in "${PODS[@]}"; do
        [ "$p" = "$ANCHOR" ] && continue
        [ "$(has_tmdb "$p" "$TOK_A")" = "1" ] || fail "unhide via $ANCHOR did not propagate to $p (cross-replica write not visible)"
    done

    result "movie+per-user: hide/unhide isolated AND consistently visible/absent across all ${#PODS[@]} replicas (no single-writer corruption)"

    # -------------------- scenario 35 parity: fan-out playback resolve --
    for p in "${PODS[@]}"; do
        PB_CODE="$(kpodn "$p" curl -s -o /dev/null -w '%{http_code}' -m 20 -X POST -H "X-Emby-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' \
            -d '{}' "http://localhost:8096/Items/$MOVIE_ID/PlaybackInfo")"
        [ "$PB_CODE" = "200" ] || fail "PlaybackInfo for real catalog movie returned $PB_CODE on $p, expected 200 (fan-out playback failure)"
    done
    result "scenario-35-parity: PlaybackInfo resolves live (200) for real catalog movie $MOVIE_ID on ALL ${#PODS[@]} replicas (stable fan-out)"
fi

# =========================================================================
# 7. scenario 36 parity: TV series/season drill, resolved from every replica
# =========================================================================
log "[7] TV series/season drill (real catalog, scenario 36 parity, fan-out)"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: resolve Phantom Shows channel, drill a real series -> season(s) from every replica"
    result "tv-drill: DRYRUN skipped"
else
    CH_SHOWS="$(kpodn "$ANCHOR" curl -s -H "X-Emby-Token: $ADMIN_TOKEN" http://localhost:8096/Channels \
        | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((c['Id'] for c in d['Items'] if c.get('Name')=='Phantom Shows'),''))")"
    [ -n "$CH_SHOWS" ] || fail "Phantom Shows channel not registered on the deployed stack"

    SERIES_ID="$(kpodn "$ANCHOR" curl -s -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Channels/$CH_SHOWS/Items?Limit=1" \
        | python3 -c "import json,sys; print(json.load(sys.stdin)['Items'][0]['Id'])")"
    [ -n "$SERIES_ID" ] || fail "could not resolve a real series from the deployed Phantom Shows channel"

    for p in "${PODS[@]}"; do
        CHILDREN="$(kpodn "$p" curl -s -H "X-Emby-Token: $ADMIN_TOKEN" "http://localhost:8096/Channels/$CH_SHOWS/Items?Limit=200&FolderId=$SERIES_ID" \
            | python3 -c "import json,sys; print(json.load(sys.stdin).get('TotalRecordCount',0))")"
        [ "${CHILDREN:-0}" -ge 1 ] || fail "real series $SERIES_ID expanded to $CHILDREN season/episode children on $p, expected >=1"
    done
    result "tv-drill: real series $SERIES_ID drills to >=1 season/episode child on ALL ${#PODS[@]} replicas (stable fan-out)"
fi

log "P4 multi-replica acceptance rig PASSED against $COLOR ($DEV_HOST) with ${#PODS[@]} replicas"
# EXIT trap tears down rig-owned users/key next.

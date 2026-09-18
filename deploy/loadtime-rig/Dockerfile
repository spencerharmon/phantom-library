# deploy/loadtime-rig/Dockerfile
#
# ROI Priority 9 — the purpose-built main-container image for the in-cluster
# daily load-time CronJob (deploy/helm/phantom-library values.yaml
# `loadtimeDaily.image`). ttfb-daily-rig-incluster-cronjob shipped that
# CronJob defaulting `loadtimeDaily.image` to the infra-identifier-safe
# placeholder `docker.io/example/phantom-loadtime-rig:latest`, which is not a
# real pullable image — so the CronJob is inert even where enabled. This
# Dockerfile is that real image's source: a small, PINNED base carrying
# EXACTLY the toolchain tools/ci/loadtime-daily-run.sh needs, and nothing
# else.
#
# The CronJob's init container clones the phantom-library source fresh per
# run (values `loadtimeDaily.gitImage` + `repoUrl`/`repoRef`), so NO repo code
# is baked into this image — it carries only the toolchain the daily script
# shells out to:
#   bash, kubectl (pinned, resolve the dev color live from the Ingress),
#   curl, ca-certificates, openssl, jq, python3
# — the SAME set .gitea/workflows/phantom-loadtime-daily.yaml installs inline
# on the CI runner today.
#
# Base is pinned to a concrete point-release tag (NEVER `:latest`) so the
# image can never silently drift onto an untested base. The published tag
# (<forge-host>/<owner>/loadtime-rig:<tag>) is likewise never a floating tag —
# see .gitea/workflows/build-loadtime-rig-image.yml.
#
# No deployment-specific identifier is baked here (infra-identifier rule):
# every host/token/endpoint the daily script needs is supplied at run time
# from the CronJob's ConfigMap/Secret, never from this image.

# Pinned Debian slim base (concrete point-release tag, not :latest / :stable).
FROM docker.io/library/debian:12.7-slim

# Pinned kubectl version — a concrete release, never `stable`/`latest`, so the
# daily job's `kubectl get ingress` color-resolution runs a known client.
ARG KUBECTL_VERSION=v1.31.1
ARG TARGETARCH=amd64

# Exactly the toolchain tools/ci/loadtime-daily-run.sh (and the P8/1 engine,
# P8/2 emitter, P8/5 guard it shells out to) requires: bash + curl +
# ca-certificates + openssl + jq + python3. kubectl is installed separately
# below (pinned download), matching the CI workflow's inline install.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
        bash \
        curl \
        ca-certificates \
        openssl \
        jq \
        python3; \
    rm -rf /var/lib/apt/lists/*; \
    curl -fsSL -o /usr/local/bin/kubectl \
        "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${TARGETARCH}/kubectl"; \
    chmod +x /usr/local/bin/kubectl; \
    kubectl version --client=true --output=yaml >/dev/null

# The CronJob sets its own command/args (it clones the repo and invokes
# tools/ci/loadtime-daily-run.sh); a bash login shell is a sane default.
SHELL ["/bin/bash", "-c"]
CMD ["/bin/bash"]

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
#   curl, ca-certificates, openssl, jq, python3, dotnet (.NET 9 SDK)
# — the SAME set .gitea/workflows/phantom-loadtime-daily.yaml installs inline
# on the CI runner today (that workflow runs ON the pinned
# mcr.microsoft.com/dotnet/sdk:9.0.305-noble base, which is where its `dotnet`
# comes from).
#
# WHY dotnet: step [3] of tools/ci/loadtime-daily-run.sh runs
# tools/perf/loadtime-guard.sh, whose ratcheting regression guard shells out
# to `dotnet run --project tools/perf/ratchet-guard -c Release` (net9.0).
# Without a .NET SDK on PATH the guard fails with "dotnet: command not found"
# (exit 127), which loadtime-daily-run.sh treats as a hard tool-error
# (guard_rc=2) and fails the whole daily run even after a fresh Mimir sample
# already landed. The .NET 9 SDK is copied from the SAME concrete, pinned
# upstream SDK image the CI workflow runs on (never :latest), via a first
# build stage, so the image can never drift onto an untested runtime.
#
# Base is pinned to a concrete point-release tag (NEVER `:latest`) so the
# image can never silently drift onto an untested base. The published tag
# (<forge-host>/<owner>/loadtime-rig:<tag>) is likewise never a floating tag —
# see .gitea/workflows/build-loadtime-rig-image.yml.
#
# No deployment-specific identifier is baked here (infra-identifier rule):
# every host/token/endpoint the daily script needs is supplied at run time
# from the CronJob's ConfigMap/Secret, never from this image.

# Pinned .NET 9 SDK base (concrete point-release tag, never :latest / :stable)
# — the SAME image .gitea/workflows/phantom-loadtime-daily.yaml runs its daily
# job under, so `dotnet` on PATH here is byte-identical to CI's. This carries
# the .NET 9 SDK that tools/perf/loadtime-guard.sh's `dotnet run --project
# tools/perf/ratchet-guard -c Release` (net9.0) requires; without it the guard
# exits 127 ("dotnet: command not found") and fails the whole daily run. The
# base is Ubuntu-noble, so apt installs the remaining toolchain below exactly
# as on the pure-Debian variant.
FROM mcr.microsoft.com/dotnet/sdk:9.0.305-noble

# Pinned kubectl version — a concrete release, never `stable`/`latest`, so the
# daily job's `kubectl get ingress` color-resolution runs a known client.
ARG KUBECTL_VERSION=v1.31.1
ARG TARGETARCH=amd64

# Exactly the toolchain tools/ci/loadtime-daily-run.sh (and the P8/1 engine,
# P8/2 emitter, P8/5 guard it shells out to) requires: bash + curl +
# ca-certificates + openssl + jq + python3 + dotnet. dotnet is provided by the
# pinned SDK base image above; bash/curl/ca-certificates/openssl/jq/python3 are
# apt-installed here, and kubectl is a separate pinned download below — matching
# the CI workflow's inline install.
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
    kubectl version --client=true --output=yaml >/dev/null; \
    dotnet --info >/dev/null

# The CronJob sets its own command/args (it clones the repo and invokes
# tools/ci/loadtime-daily-run.sh); a bash login shell is a sane default.
SHELL ["/bin/bash", "-c"]
CMD ["/bin/bash"]

#!/bin/sh
# Executed by swarm-owned CI. Credentials stay in a disposable client config,
# outside the narrowly scoped Docker context; never build args or image layers.
set -eu
: "${REGISTRY_URL:?}" "${REGISTRY_USERNAME:?}" "${REGISTRY_TOKEN:?}" "${GITHUB_SHA:?}"
export CONTAINER_ENGINE=docker SWARM_HEALTH_SOURCE_REVISION="$GITHUB_SHA"
export DOCKER_CONFIG
DOCKER_CONFIG=$(mktemp -d)
trap 'rm -rf "$DOCKER_CONFIG"' EXIT HUP INT TERM
registry=${REGISTRY_URL#*://}
export SWARM_HEALTH_TAG="$registry/$REGISTRY_USERNAME/swarm-health:$GITHUB_SHA"
export SWARM_HEALTH_EVIDENCE="$DOCKER_CONFIG/build-evidence.json"
printf '%s' "$REGISTRY_TOKEN" | docker login --username "$REGISTRY_USERNAME" --password-stdin "$registry"
unset REGISTRY_TOKEN
./scripts/tests/p05-swarm-health-service-image.test.sh
docker push "$SWARM_HEALTH_TAG"
SWARM_HEALTH_IMAGE=$(docker image inspect "$SWARM_HEALTH_TAG" --format '{{index .RepoDigests 0}}')
export SWARM_HEALTH_IMAGE
export SWARM_HEALTH_EVIDENCE="$DOCKER_CONFIG/pull-evidence.json"
# Pull by immutable digest, then repeat the SAME offline fixture/restart proof.
./scripts/tests/p05-swarm-health-service-image.test.sh
printf 'PUBLISHED_AND_VERIFIED=%s\nSOURCE_REVISION=%s\n' "$SWARM_HEALTH_IMAGE" "$GITHUB_SHA"

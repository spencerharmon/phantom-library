#!/usr/bin/env bash
# Run a test command with real PostgreSQL; never silently skip database tests.
# A supplied DSN must name a disposable test database (tests create/drop schemas).
set -euo pipefail
if [[ $# == 0 ]]; then
    echo "usage: with-postgres.sh <command> [args...]" >&2
    exit 2
fi
if [[ -n "${PHANTOM_TEST_POSTGRES_DSN:-}" ]]; then
    exec "$@"
fi

root="$(realpath "$(dirname "${BASH_SOURCE[0]}")/../..")"
mkdir -p "$root/.p0-test-runtime"
podman() { command podman --root "$root/.p0-test-runtime/storage" --runroot "$root/.p0-test-runtime/run" "$@"; }
pg="phantom-test-${RANDOM}-$$"
cleanup() {
    local status=$?
    if ! podman rm -f "$pg"; then
        [[ "$status" != 0 ]] || status=1
    fi
    exit "$status"
}
trap cleanup EXIT
podman run -d --name "$pg" -p 127.0.0.1::5432 \
    -e POSTGRES_PASSWORD=regression -e POSTGRES_DB=regression docker.io/library/postgres:16-alpine
port="$(podman port "$pg" 5432/tcp)"
export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=${port##*:};Username=postgres;Password=regression;Database=regression"
for attempt in {1..30}; do
    if podman exec "$pg" pg_isready -h 127.0.0.1 -U postgres -d regression; then break; fi
    sleep 1
done
podman exec "$pg" pg_isready -h 127.0.0.1 -U postgres -d regression
"$@"

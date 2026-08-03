#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/gitea-cutover.test.sh
#
# Regression harness for the CI cutover to self-hosted Gitea Actions
# (task gitea-cutover). Asserts that the OLD CI surfaces are gone and the
# Gitea Actions surface fully covers what they used to do:
#
#   - GitHub Actions is retired: no `.github/workflows/build.yml` /
#     `release.yml` (and no GHA workflow yml survives at all).
#   - The obsolete Zuul config is gone: `zuul.d/` and `playbooks/` removed,
#     and the Zuul-only `tools/ci/verify-zuul-config.py` removed.
#   - Gitea Actions covers `build.yml` — `.gitea/workflows/nonrig-gate.yaml`
#     exists, parses as YAML, and drives the build/test via the shared
#     `tools/ci/nonrig-build-test.sh`.
#   - Gitea Actions covers `release.yml` — `.gitea/workflows/release.yaml`
#     exists, parses as YAML, is tag-driven (`on: push: tags: v*`), and
#     packages via `jprm plugin build`.
#   - CI docs are repointed: the obsolete `docs/ci-zuul.md` is gone,
#     `docs/ci-gitea-actions.md` exists, and no doc still claims the GHA
#     workflows are "left in place".
#
# Needs only bash + python3 (PyYAML optional). No .NET / container / network.
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_(){ printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal(){ printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v python3 >/dev/null 2>&1 || fatal "python3 not available; cannot YAML-lint workflows"

yaml_parses() {
  python3 - "$1" <<'PY'
import sys
try:
    import yaml
except ImportError:
    # No PyYAML: structural sanity fallback (mapping doc, no tabs).
    with open(sys.argv[1]) as f:
        txt = f.read()
    sys.exit(0 if (":" in txt and "\t" not in txt) else 1)
with open(sys.argv[1]) as f:
    try:
        yaml.safe_load(f)
    except Exception as e:
        print(f"YAML parse error: {e}", file=sys.stderr)
        sys.exit(1)
sys.exit(0)
PY
}

# ---------------------------------------------------------------------------
head_ "GitHub Actions retired"
GHA_DIR="$REPO_ROOT/.github/workflows"
for f in build.yml release.yml; do
  if [[ -e "$GHA_DIR/$f" ]]; then
    bad ".github/workflows/$f still present (GHA not retired)"
  else
    ok ".github/workflows/$f removed"
  fi
done
# no GHA workflow yml of any name survives
if [[ -d "$GHA_DIR" ]] && find "$GHA_DIR" -maxdepth 1 -type f \( -name '*.yml' -o -name '*.yaml' \) -print -quit | grep -q .; then
  bad ".github/workflows still contains workflow file(s)"
else
  ok "no GitHub Actions workflow files remain"
fi

# ---------------------------------------------------------------------------
head_ "obsolete Zuul config removed"
for p in zuul.d playbooks tools/ci/verify-zuul-config.py; do
  if [[ -e "$REPO_ROOT/$p" ]]; then
    bad "$p still present (obsolete Zuul config not removed)"
  else
    ok "$p removed"
  fi
done

# ---------------------------------------------------------------------------
head_ "Gitea Actions covers build.yml (non-rig gate)"
NONRIG="$REPO_ROOT/.gitea/workflows/nonrig-gate.yaml"
if [[ -f "$NONRIG" ]]; then
  ok ".gitea/workflows/nonrig-gate.yaml exists"
  if yaml_parses "$NONRIG"; then ok "nonrig-gate.yaml parses as YAML"; else bad "nonrig-gate.yaml is not valid YAML"; fi
  if grep -qE 'tools/ci/nonrig-build-test\.sh' "$NONRIG"; then
    ok "nonrig-gate.yaml drives build/test via the shared script"
  else
    bad "nonrig-gate.yaml does not invoke tools/ci/nonrig-build-test.sh"
  fi
else
  bad ".gitea/workflows/nonrig-gate.yaml missing (build coverage gone)"
fi

# ---------------------------------------------------------------------------
head_ "Gitea Actions covers release.yml (tag-driven release)"
RELEASE="$REPO_ROOT/.gitea/workflows/release.yaml"
if [[ -f "$RELEASE" ]]; then
  ok ".gitea/workflows/release.yaml exists"
  if yaml_parses "$RELEASE"; then ok "release.yaml parses as YAML"; else bad "release.yaml is not valid YAML"; fi
  # tag-driven: python-inspect the `on:` trigger for a tags filter.
  if python3 - "$RELEASE" <<'PY'
import sys
try:
    import yaml
except ImportError:
    txt = open(sys.argv[1]).read()
    sys.exit(0 if ("tags" in txt and "v*" in txt) else 1)
d = yaml.safe_load(open(sys.argv[1]))
on = d.get(True, d.get("on"))   # PyYAML maps bare `on:` to True
tags = []
if isinstance(on, dict):
    push = on.get("push") or {}
    if isinstance(push, dict):
        tags = push.get("tags") or []
sys.exit(0 if any(str(t).startswith("v") for t in tags) else 1)
PY
  then
    ok "release.yaml is tag-driven (on: push: tags: v*)"
  else
    bad "release.yaml is not tag-driven on v* tags"
  fi
  if grep -qE 'jprm[[:space:]]+(--[^[:space:]]+[[:space:]]+)*plugin[[:space:]]+build' "$RELEASE"; then
    ok "release.yaml packages via jprm plugin build"
  else
    bad "release.yaml does not invoke jprm plugin build (packaging missing)"
  fi
else
  bad ".gitea/workflows/release.yaml missing (release coverage gone)"
fi

# ---------------------------------------------------------------------------
head_ "CI docs repointed"
if [[ -e "$REPO_ROOT/docs/ci-zuul.md" ]]; then
  bad "docs/ci-zuul.md still present (should be retired)"
else
  ok "docs/ci-zuul.md removed"
fi
if [[ -f "$REPO_ROOT/docs/ci-gitea-actions.md" ]]; then
  ok "docs/ci-gitea-actions.md exists"
else
  bad "docs/ci-gitea-actions.md missing (CI overview not repointed)"
fi
# No doc must still claim the GHA workflows are "left in place".
if grep -rIlE 'left (in place|untouched)' "$REPO_ROOT/docs" 2>/dev/null | grep -q .; then
  # allow historical mentions only if they do NOT reference .github/GHA
  if grep -rInE 'left (in place|untouched)' "$REPO_ROOT/docs" 2>/dev/null | grep -iE 'gha|github|\.github' | grep -q .; then
    bad "a doc still says the GitHub Actions workflows are left in place"
    grep -rInE 'left (in place|untouched)' "$REPO_ROOT/docs" 2>/dev/null | grep -iE 'gha|github|\.github' | sed 's/^/    /' >&2
  else
    ok "no doc claims the GHA workflows are left in place"
  fi
else
  ok "no doc claims the GHA workflows are left in place"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]

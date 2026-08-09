# Guards — phantom-library

Diff-scoped mutation guards for the phantom-library target (beehive's mutation-guard
primitive; see `docs/mutation-guards-spec.md` in the beehive submodule). The runner
fires a guard only when a Work pass's committed diff touches a `Protects:` glob,
judges it from the merge-base baseline (so an in-pass edit to guard code is inert),
and enforces the guard's exit code (0 = allow, non-zero = refuse + fix-forward).

Guard-code changes themselves are protected WITHOUT a hard block (which would forbid
legitimate guard authoring): the merge-base tamper anchor makes a bee's edit to a
guard inert for its own pass, and the change still lands via NEEDS-REVIEW where a
reviewer sees the guard-code diff.

## infra-identifier-isolation
Refuse a honeybee change that INTRODUCES a deployment-specific infrastructure
identifier into the environment-AGNOSTIC plugin source or Helm chart. This is the
runner-enforced half of the `infra-identifier-isolation` gate in the beehive-layer
`GATES.md`, and encodes the beehive-wide AGENTS.md "Code <-> infrastructure dividing
line" rule as an enforced guard: real site identifiers (the deployment domain
`polyfam.studio` and any host under it; RFC1918 / k3s cluster IPs — `192.168.x`,
`10.42.x`/`10.43.x`, `172.16-31.x`) live ONLY on the infrastructure side (the Flux
HelmReleases / config / Secrets) and are supplied at runtime — never committed into
this shared repo. AUTHORITY is the change's own committed diff: the guard inspects
only the ADDED lines of the protected files, so removing an identifier or a
pre-existing one on an unchanged line is never flagged. Placeholder-safe (RFC2606
`example.com`/`.net`, RFC5737 `192.0.2/198.51.100/203.0.113` are NOT matched) and
does not touch generic non-site naming (e.g. the chart's color-agnostic
`jellyfin_prod`/`phantom_prod` default DB names). Fails closed if the pass patch is
unavailable. Proof: `guards/tests/infra-identifier-isolation.test.sh`.
Protects: src/**, deploy/helm/**
Command: guards/infra-identifier-isolation.sh

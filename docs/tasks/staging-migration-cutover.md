# P3 Stage 4 — staging validation + cutover runbook (operator-gated)

Task: `staging-migration-cutover`. This is the **operator runbook**, not a code
deliverable: the task culminates in a `NEEDS-HUMAN` carrying the staging URL for
hand-validation, and the prod CNAME flip is an operator-only action. There is no
new script here — it wires the ALREADY-SHIPPED five-stage methodology in
`scripts/phantom-migrate-jellyfindb-to-postgres.sh` (P4 Stage A;
`docs/tasks/p4-mysql-migration-impl.md`) into the concrete blue/green cutover
sequence, under the **REVISED (operator, 2026-08-03) shared-data topology**.

## REVISED topology: blue and green share ALL data

The earlier plan assumed **independent per-color storage** (an idle color would
be provisioned via a CSI `VolumeSnapshot` clone of the active color's PVC before
migration). **That model is superseded.** Blue and green now share:

- the **same PVC(s)** (`ReadWriteMany`, per `values.yaml`'s "so a blue/green flip
  never copies it (identical across colors)" comment — see
  `deploy/helm/phantom-library/values.yaml:56`), and
- the **same PostgreSQL database** (the shared `jellyfin_prod` / `phantom_prod`
  logical DBs that `phantom-migrate-jellyfindb-to-postgres.sh` writes in its
  stage-5 prod commit).

Consequences for this task:

- **No CSI `VolumeSnapshot` / snapshot-backend dependency.** There is no
  per-color volume to provision from a snapshot, so the former Piraeus (LINSTOR
  CSI) storage prerequisite this task previously parked behind a `flux:` sentinel
  is **no longer applicable**. Do not reintroduce it as a dependency.
- **The inactive color is a second consumer of the SAME store, not a clone.**
  Deploying the vM plugin on the inactive color attaches it directly to the
  shared PVC(s) and the shared PostgreSQL DB that prod already uses — there is
  nothing to snapshot or restore.
- **Schema changes on the shared DB must follow expand/contract**, not a
  per-color migrate-then-flip: since both colors read the SAME database
  concurrently during the cutover window, a breaking schema change would break
  whichever color is still active. Additive changes are safe to deploy once;
  breaking changes MUST go through expand → flip → contract (flux
  `docs/phantom-library-schema-change-expand-contract.md`) so the still-active
  color never observes a shape it does not understand.

## Runbook

1. **Confirm prerequisites are actually DONE, not merely believed done.**
   `migration-rig` (in-repo migration regression rig), `p4-phantomdb-postgres-backend`
   (config-gated Postgres provider for `PhantomDb`, `p4-phantomdb-postgres-provider`
   per the CHANGELOG "Config-gated PostgreSQL backend" entry), and the two
   `flux:` cross-deps (`phantom-library-postgres-consumer-wire`,
   `phantom-library-postgres-wire-follow-flip`, both owned by the `flux`
   submodule) wire the vM plugin to actually consume the shared Postgres store
   and keep following the active/inactive flip. This runbook assumes those are
   live; it does not re-implement them.

2. **Deploy the vM plugin build on the INACTIVE color.** The inactive color's
   Jellyfin/phantom pod set is pointed (via the flux-owned wiring from step 1)
   at the SAME shared PVC(s) and the SAME PostgreSQL connection info
   (`PG_STAGING_*` / prod host/db) as the active color — no new volume, no new
   logical DB. This is exactly the "inactive color / dev logical DB" target
   `phantom-migrate-jellyfindb-to-postgres.sh` already models in its stage-3
   rehearsal (`PG_STAGING_DB` default `jellyfin_inactive` / `phantom_dev`).

3. **Run the migration under expand/contract discipline.** For an ADDITIVE
   schema change, run the migration once against the shared DB (no snapshot,
   no separate volume clone):

   ```
   beehive submodule worktree exec phantom-library bee-staging-migration-cutover -- \
     ./scripts/phantom-migrate-jellyfindb-to-postgres.sh --source jellyfin --stage
   beehive submodule worktree exec phantom-library bee-staging-migration-cutover -- \
     ./scripts/phantom-migrate-jellyfindb-to-postgres.sh --source phantom --stage
   ```

   review the predicted-vs-actual report (stage 4, operator hand-validation
   gate baked into the script itself), then:

   ```
   beehive submodule worktree exec phantom-library bee-staging-migration-cutover -- \
     ./scripts/phantom-migrate-jellyfindb-to-postgres.sh --source jellyfin --commit
   beehive submodule worktree exec phantom-library bee-staging-migration-cutover -- \
     ./scripts/phantom-migrate-jellyfindb-to-postgres.sh --source phantom --commit
   ```

   For a BREAKING schema change, do NOT run `--commit` directly against the
   shared DB from this step — follow the flux expand/contract sequence instead
   (expand additively first, flip which color is active, only then contract);
   this runbook's `--commit` step applies to the additive case only.

4. **Point `dev.jellyfin.polyfam.studio` at the inactive color and leave it
   up.** This DNS/ingress-routing flip is owned by the flux/GitOps layer (the
   two `flux:` cross-deps above), not by a script in this submodule — it is
   listed here only so the operator knows what to expect live at the URL
   named in step 5. No prod traffic moves; `jellyfin.polyfam.studio` keeps
   pointing at the still-active color throughout.

5. **Raise `NEEDS-HUMAN` carrying the staging URL.** The swarm cannot judge
   whether the migrated library/user data, watch-state, and per-user
   show/hide state look CORRECT from a real operator's point of view — that is
   an inherently subjective, human-only judgment (per-user opaque data), which
   is exactly what `--category external-permission` is for here: validating
   `https://dev.jellyfin.polyfam.studio` is an action outside the swarm's
   control (a human eyeball on a live UI), not a decision the swarm could make
   itself. This task's own honeybee pass performs this step.

6. **Operator flips the prod CNAME** (`jellyfin.polyfam.studio` -> the
   validated color) only after hand-validation passes. **This is NOT part of
   this task and is NEVER performed by the swarm** — the ROI is explicit that
   the operator, not this agent, owns the prod flip. A failed validation is a
   no-op: the operator simply does not flip, and the old color stays
   authoritative.

## Accepted tradeoff (operator-approved)

A user who stays connected to the draining OLD color across the cutover window
may see a bounded write-loss window (their in-flight watch-state/write lands on
the color being drained rather than the newly-active one). This is an accepted
operator tradeoff, not a defect this task fixes — noted here for the next
reader who might otherwise treat it as a bug.

## Why this task carries `check=none`

There is no swarm-observable in-sandbox effect to assert: the deliverable is
(a) this runbook reconciling the task card's revised shared-data model into a
concrete operator sequence, referencing the already-tested
`phantom-migrate-jellyfindb-to-postgres.sh` (which carries its own regression
test, `scripts/tests/phantom-migrate-jellyfindb-to-postgres.test.sh`), and
(b) the `NEEDS-HUMAN` escalation itself. Both the staging hand-validation and
the prod CNAME flip are operator-owned live judgments no sandbox check can
assert.

## Files changed

- `docs/tasks/staging-migration-cutover.md` — this runbook (new).
- `CHANGELOG.md` — Unreleased doc note.

Beehive layer: `PLAN.md` `staging-migration-cutover` stays as the runner
transitions it (this task ends in `NEEDS-HUMAN`, not `NEEDS-REVIEW`); the
change doc at `submodules/phantom-library/docs/bee-staging-migration-cutover-staging-migration-cutover.md`.

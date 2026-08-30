# p7-contract-phase-and-drift-check

ROI Priority 7 (blue/green-safe additive schema), items 3+4: the CONTRACT-phase
template and the cross-color schema drift check. Mirrors
`docs/tasks/p7-additive-idempotent-expand-migrations.md` and
`src/Jellyfin.Plugin.PhantomLibrary/State/Db/SchemaExpandMigrator.cs` — read
those first for the expand half of this story.

## What this ships

- `SchemaContractMigrator` (`State/Db/SchemaContractMigrator.cs`) — the
  CONTRACT-phase mirror of `SchemaExpandMigrator`. Runs DROP/retire DDL,
  serialized via the same `pg_advisory_xact_lock` pattern and recorded in its
  own `phantom_contract_migrations` bookkeeping table so a repeat/peer-color
  call is a no-op. The one addition versus expand: every `ApplyAsync` call is
  gated by `EnsurePreflightAsync`, which REFUSES unless:
  1. `CutoverFlipRegistry` has a recorded COMPLETED entry for the named
     cutover flip, and
  2. at least the caller's `monitoringWindow` has elapsed since that
     completion (evaluated via an overridable `TimeProvider`, defaulting to
     `TimeProvider.System`, so tests can assert the exact boundary without
     sleeping).
  The preflight runs BEFORE any lock/transaction is opened — a refused
  contract touches nothing.
- `CutoverFlipRegistry` — records/reads a named flip's completion timestamp in
  `phantom_cutover_flips`. This is bookkeeping ONLY: nothing in this file (or
  anywhere in this task) performs the prod flip itself. Per the ROI, the flip
  is always an operator action — this repo's existing NEEDS-HUMAN
  `staging-migration-cutover` task is that action; an operator (or the
  tooling driving that procedure) calls `RecordCompletedAsync` once the flip
  is actually live, and only then can a contract's preflight ever pass.
- `SchemaDriftChecker` (`State/Db/SchemaDriftChecker.cs`) — the cross-color
  drift check ("check cross-color schema drift before each phase"). Compares
  the ACTUAL live schema (`information_schema.columns`/`.tables`) against the
  columns a caller says the OTHER, still-active color explicitly reads, and
  refuses (`SchemaDriftDetectedException`) if any are missing. This is a
  positive/subset check, not schema equality — the two colors are expected to
  differ during the overlap window, so only the intersection each color's
  code actually names is asserted present. Call this immediately before EITHER
  an expand or a contract phase runs against a table the peer color might
  still touch.

## Why this shape

- **Contract mirrors expand's structure** (advisory lock, idempotency-record
  table, static-then-dynamic gating) so the two phases stay recognizably one
  family rather than diverging designs, per the task card's "mirrors the
  expand helper" instruction.
- **The preflight is the only thing contract adds over expand** — expand
  gates on "is this statement additive"; contract instead gates on "has the
  human-owned cutover actually completed and soaked." Both are refuse-before-
  mutate postures; neither performs the prod flip.
- **`TimeProvider` is injectable** so the boundary condition (window just
  under vs. just over the required soak) is deterministically testable
  without `Thread.Sleep`, matching how the rest of this codebase avoids
  wall-clock-dependent tests.
- **Drift check is a separate class from both migrators** because it is
  orthogonal to which phase is running — the same check gates an expand OR a
  contract equally; folding it into either migrator would make the other
  phase's callers reach into the wrong class for it.

## Non-goals (explicitly out of scope for this task)

- No concrete contract migration ships here — this is the template a future
  task instantiates with its own DROP statements once real retired structure
  exists and its cutover has actually completed.
- No code here performs the prod flip. `CutoverFlipRegistry.RecordCompletedAsync`
  only records that an operator-completed flip happened; it is never called by
  automated tooling to *declare* a flip complete on its own initiative.

## Evidence

Real-Postgres integration tests (gated on `PHANTOM_TEST_POSTGRES_DSN`, exactly
like `SchemaExpandMigratorPostgresTests` — a plain `dotnet test` with no
Postgres server stays green because every gated test returns immediately):

- `tests/.../SchemaContractMigratorPostgresTests.cs` — preflight refuses with
  no recorded flip; refuses with an unelapsed monitoring window; proceeds only
  once both a completed flip AND an elapsed window hold; `ApplyAsync` refuses
  the drop entirely (statement never runs) when preflight fails; a permitted
  drop applies once and is idempotent on a second call; movie- and
  episode-shaped table parity; `CutoverFlipRegistry` re-recording updates the
  timestamp (operator-correction path).
- `tests/.../SchemaDriftCheckerPostgresTests.cs` — detects a would-break-a-
  running-color drift (a required column absent) and refuses; passes a
  genuinely-safe drift (extra columns present, all required columns present);
  movie- and episode-shaped table parity; `GetActualColumnsAsync` /
  `TableExistsAsync` read the real live schema; a no-required-columns call is
  a no-op even against a nonexistent table.

Ran against a real, disposable Postgres 16 container:

```
podman run -d --name phantom-pg-test-p7 -p 15433:5432 \
  -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD=phantom -e POSTGRES_DB=phantom_test \
  docker.io/library/postgres:16-alpine

PHANTOM_TEST_POSTGRES_DSN="Host=localhost;Port=15433;Username=phantom;Password=phantom;Database=phantom_test" \
  MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false \
  --filter "FullyQualifiedName~SchemaContractMigrator|FullyQualifiedName~SchemaDriftChecker" \
  tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj

# Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14
```

Full-suite regression (`dotnet test` — the task's declared `Check:`), no
Postgres DSN set:

```
MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false phantom-library.sln
# Passed! - Failed: 0, Passed: 610, Skipped: 0, Total: 610, Duration: 1m 6s
```

(610 = the pre-existing suite plus the 14 new gated tests above, which no-op
without `PHANTOM_TEST_POSTGRES_DSN`.)

## Environment note for future agents

At the time this task ran, the `jellyfin/` nested submodule was NOT checked
out in the pass's fresh code worktree (`git submodule status` showed a `-`
prefix), which made the whole solution fail to build (`Jellyfin.Plugin.PhantomLibrary.csproj`
references `../../jellyfin/MediaBrowser.Controller/...`). This is an
environment/worktree-provisioning gap, not a code defect: `git submodule
update --init jellyfin` (pointed at a local sibling clone via
`git config submodule.jellyfin.url <local-path>` to avoid a slow/impossible
GitHub clone from this sandbox, then unset) fixed it, and the patches in
`scripts/jellyfin-patches/` were already applied on that submodule commit. If
this recurs, the fix is the same: initialize `jellyfin/` (and `gostream/` if a
task needs it) before building/testing.

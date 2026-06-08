# AGENTS.md

Guidance for AI coding agents working in this repo. Humans:
read `README.md`, `PLAN.md`, `CHANGELOG.md` first — those are
authoritative project docs. This file translates them into
conventions an agent needs to operate session-to-session
without re-deriving them.

## No database migrations until v1.0 (always, no exceptions)

**Pre-v1.0, this project does not ship database migrations.** Not
for `phantom.db`, not for `jellyfin.db`, not for any persistent
state the plugin owns. If the schema changes — a new column, a
new table, a renamed field, a rekeyed primary key, a change to
what any existing column stores — the upgrade path is **wipe and
rebuild**, not migrate.

What "wipe and rebuild" means concretely:

- Operator stops Jellyfin.
- Phantom state is deleted (`phantom.db` removed, phantom-tracked
  `BaseItems` removed from `jellyfin.db`, stub directories
  emptied). The repo's existing `/tmp/phantom-wipe.sh`-style
  one-off scripts are the template for this.
- Operator restarts Jellyfin.
- Plugin recreates schema via `PhantomDb.EnsureSchemaAsync` and
  starts from zero state.
- `SuggestionsRefreshTask` repopulates from TMDB on its next tick
  (or operator triggers it manually).

Forbidden patterns under this rule:

- Shipping an `IHostedService` migration that mutates operator
  state on plugin startup. (Already independently forbidden per
  the *Single-operator deployment* section, for race-condition
  reasons. This rule additionally forbids it for state-evolution
  reasons.)
- Shipping a bash script in the repo that migrates between two
  pre-v1.0 schema versions. One-off recovery scripts for a
  specific botched upgrade are fine (they live in `/tmp/`, not
  the repo), but no general-purpose migration tooling.
- Adding an `ALTER TABLE` or `ADD COLUMN` branch keyed on a
  detected old schema version. Bump the schema, expect a wipe.
- Writing code that reads old-format rows and rewrites them on
  the fly. Old-format rows do not exist after a wipe.
- Calling something a "non-destructive schema upgrade" because it
  only adds columns / tables. Every assumed-non-destructive
  upgrade has had a non-obvious failure mode in this codebase
  (see the v0.2.0.0 in-plugin `StubLayoutMigration` post-mortem
  in `PLAN.md`).

What to do instead when you change schema or persistent format:

1. Bump `PhantomDb`'s schema version constant.
2. Update `EnsureSchemaAsync` to produce the new schema from
   scratch.
3. Update the relevant on-disk layout / naming if needed.
4. Add a `CHANGELOG.md` entry under Unreleased with a
   **"BREAKING: requires wipe"** prefix and an inline pointer
   to the wipe procedure.
5. Add the wipe procedure to the operator handoff at the end of
   the PR message. The operator runs the wipe before installing
   the new plugin DLL.
6. **Do not** write a migration to bridge between old and new.

Why this rule exists:

- The project has zero external users. Wipe-and-rebuild has no
  cost to a user base because there is no user base.
- The operator's repopulation cost is one TMDB refresh tick. No
  irreplaceable state lives in `phantom.db` — user-visible state
  (favourites, watched, watch history) is in `jellyfin.db`'s
  `UserDatas` table, keyed on `BaseItems.Id`. Wiping phantom
  state does not touch user data.
- Migration testing requires reproducing the operator's actual
  data shape in a sandbox, validating every schema-version pair,
  and trusting the migration to behave the same on dirty real
  data as on clean test data. The v0.2.0.0 attempt failed this
  bar four separate ways (race with scanner, wrong column
  assumption, wrong SQL dialect assumption, wrong row-target
  assumption). The cost of "just test the migration" has
  empirically been higher than the cost of "wipe and rebuild."
- A clean wipe also resets accumulated cruft: orphan rows,
  collision artifacts, stale `phantom_items.stub_path = NULL`
  rows from older plugin versions, half-materialised state from
  crashed operations. Migrations preserve all of this, often
  invisibly. Wipes do not.

**At v1.0**, this rule lifts: real migrations become required
because the project will then have a stable on-disk format that
operators reasonably expect to upgrade in place. Until then,
schema evolution = wipe.

## Canonical phantom stub naming scheme (always, no exceptions)

The canonical on-disk naming scheme for phantom stubs is Jellyfin's
native path-token form:

- Movies: `<root>/movies/<DisplayTitle> (<Year>) [tmdbid-<id>].<ext>`
- Shows:  `<root>/shows/<DisplayTitle> (<Year>) [tmdbid-<id>]/Season 01/<DisplayTitle> (<Year>) S01E01.<ext>`

**Never reintroduce the legacy `__phantom_tmdb<id>` filename
sentinel** in any code path that creates new stubs. The legacy
scheme is retained ONLY for back-compat parsing (the
`PhantomStubManager.Sentinel` constant + the
`PhantomPathUtilities.IsLegacyStubPath` helper) so a one-off
migration script can recognise and rename old stubs. Any new
feature, refactor, or test fixture must emit the bracketed
`[tmdbid-<id>]` form.

Why this rule exists: with the legacy scheme, the Jellyfin library
scanner derived `BaseItem.Name` from the sanitized filename stem
(`Word_Word__phantom_tmdb1234`), forcing the plugin into a fragile
`IsLocked = true` + re-stamp + heal dance to recover the real title.
The bracketed token is what Jellyfin's `MovieResolver` /
`SeriesResolver` parses natively; the resolver-derived Name is the
real title by construction. The scanner-race symptoms (underscored
names, missing posters, broken healing) all stem from the legacy
scheme. See `PLAN.md` § "Spike — Jellyfin-native `[tmdbid-<id>]`
stub layout" for the full rationale.

Forbidden patterns:

- Building new filenames that contain `__phantom_tmdb` anywhere.
- Adding new `Contains("__phantom_tmdb")` checks in feature code
  (use `PhantomPathUtilities.IsPhantomStubPath` which recognises
  both forms).
- Writing test fixtures whose synthetic paths use the legacy
  sentinel — use `[tmdbid-99000001]` or similar bracketed form.
- Reverting any caller's `int? year` parameter back to a year-less
  overload of `PhantomStubManager.CreateAsync`. Year is required
  for the bracketed scheme to disambiguate same-titled releases.

If you find historical references to the legacy scheme in design
docs (`PLAN.md` M10/M11/M13 sections, `docs/plans/M12-*.md`,
older `CHANGELOG.md` entries), they are kept for historical
context. Each such section carries a `⚠ DEPRECATED naming` banner
pointing here. Do not propagate the legacy form from those
contexts into new code.

## Production database safety (always, no exceptions)

The operator's production databases — `phantom.db`, `jellyfin.db`,
and anything under `/var/lib/jellyfin/` — are not a test rig. Before
you tell the operator to run any `DELETE`, `UPDATE`, `ALTER`,
`DROP`, schema migration, or destructive shell command against a
real DB, the script must have been executed end-to-end against a
clone in a sandbox and observed to produce the predicted counts on
the operator's actual data shape.

This applies to:

- SQL snippets pasted into chat for the operator to run by hand.
- Bash scripts that touch `phantom.db` or `jellyfin.db`.
- In-plugin code paths that `DELETE` / `UPDATE` operator state.
- Anything whose failure mode is "delete more rows than intended."

### Forbidden patterns (each one has already bitten this project)

- **Writing SQL from memory based on what you think the schema is.**
  Always probe the schema first:
  ```bash
  sqlite3 /var/lib/jellyfin/data/jellyfin.db '.schema BaseItems'
  sqlite3 /var/lib/jellyfin/data/jellyfin.db '.tables' | tr ' ' '\n' | grep -i provider
  ```
  Schema assumptions that have already been wrong here:
  - `BaseItems.Id` is a BLOB → it is TEXT (hyphenated UUID).
  - `BaseItems.ProviderIds` is a column → it is a separate table
    `BaseItemProviders` with columns `(ItemId, ProviderId, ProviderValue)`.
  - `BaseItems.Id` matches `phantom_items.item_guid` directly →
    join requires `lower(replace(BaseItems.Id, '-', ''))`.
  - SQLite `RAISE(ABORT, ...)` is legal in plain SQL → triggers only.

- **Telling the operator "this is safe, run it" without having
  executed it yourself against a clone first.** The cost of cloning
  a DB to `/tmp/` and running the script is minutes. The cost of
  nuking thousands of rows of operator state is hours of recovery
  and operator trust.

- **Iterative "fix it on the operator's box" debugging.** Every time
  the operator hits an error and pastes it back, that means the
  script was not tested. Tests happen in the sandbox, not the prod
  terminal.

- **In-plugin runtime `DELETE`/`UPDATE` of operator data that has
  not been exercised on a clone with the operator's data shape.**
  See also the in-plugin migration rule in the
  *Single-operator deployment* section.

### What "tested" means

1. Clone the relevant DBs to a sandbox path (`/tmp/recover-test/`
   or the rig at `/tmp/jf-test/` per `docs/agents/testing.md`).
2. If the operator's data shape is unusual (e.g. ~19k phantom rows,
   half orphaned by a previous botched run), **reproduce that shape
   in the sandbox** before testing. A script that works on a
   trivial DB but breaks on the operator's actual data is worse
   than no script at all because it falsely conveys confidence.
3. Execute the script end-to-end against the clone.
4. Verify counts at every phase match the predicted values.
5. Verify idempotency: a second run is a no-op.
6. **Then** paste the script (or the run command) to the operator.

When you need to author destructive SQL or a destructive script,
dispatch the `implementer` agent to author and test it in a worktree
with a sandbox clone. **Do not write destructive SQL in chat and
hand it to the operator.** The operator is not the test rig.

### The failure mode that motivated this rule

During the v0.2.0.0 stub layout migration:

1. An agent shipped an in-plugin `StubLayoutMigration`
   `IHostedService` that raced Jellyfin's live library scanner and
   created ~8500 duplicate `BaseItems`. The migration was not
   tested against the operator's data shape.
2. The same agent then wrote untested SQL using `hex(BaseItems.Id)`
   to clean up colliders, and told the operator it was safe. The
   join returned zero matches (Id is TEXT not BLOB), the `NOT IN`
   therefore matched every row, and **all 17327 rows in
   `phantom_items` were deleted.**
3. Recovery required restoring from a backup the migration script
   had fortunately taken minutes earlier.
4. The recovery script itself then hit two more wrong-schema
   assumptions (`BaseItems.ProviderIds` column doesn't exist;
   `RAISE()` is not legal in plain SQL) because the same author-in-
   chat anti-pattern was repeated under time pressure.

Each of these would have been caught by running the script once
against a clone before handing it to the operator.

**The rule: test against a clone, validate the schema, then ship.
Never the other order.**

## Operator hand-off rule (always)

When finishing a change the operator will install or test, **always
end the message with an explicit, ordered list of operator steps**.
This includes anything not handled by `./install.sh` alone:

- DB migrations or repair scripts (e.g. `sudo /tmp/foo-repair.sh`).
- Stopping or restarting services (`gostream.service`, `jellyfin.service`).
- Triggering scheduled tasks from the dashboard (e.g.
  Suggestions / Refresh Library / Validate Vault).
- Re-running install with non-default flags (`--build`,
  `--no-gostream`).
- Anything that touches `/var/lib/jellyfin`, `/var/gostream`, or
  root podman storage.

The operator should never have to infer whether "run the install
script" is enough or whether they also need to run Suggestions, or
wait for a periodic re-bind cycle, or `chown` something. Make it
explicit, every time, even when the steps feel obvious.

If the change requires *no* operator action beyond the next install,
say so explicitly: "No operator steps needed; `./install.sh` is
sufficient." Silence on this question costs the operator time.

## Single-operator deployment

This project is deployed in a single-operator environment. The
operator owns the box and the Jellyfin process. There are no
external users, no SLA, and no "can we afford an outage" question
to weigh — **stopping `jellyfin.service` for maintenance is always
acceptable.**

The operational consequence: **prefer offline bash scripts (run
with `jellyfin.service` stopped) over in-plugin
`IHostedService` migrations or any runtime data-mutation service
that touches the Jellyfin DB or the on-disk media tree.** With
Jellyfin stopped, the entire DB and filesystem sit still while
the script works; there is no library scanner, no file watcher,
no user playback, nothing to race.

Concrete failure that motivated this rule (do not repeat):
v0.2.0.0 shipped an in-plugin `StubLayoutMigration` IHostedService
that ran on plugin startup while Jellyfin was live. It moved
stub files on disk and called `UpdateItemAsync` to repoint
`BaseItem.Path`. The live library scanner saw old paths vanish
before the `UpdateItemAsync` landed, saw the new-format paths
appear, and created **fresh BaseItems** for them — leaving the
library with duplicate BaseItems and the UI still showing the
legacy scanner-derived names. The fix was to delete the
IHostedService and consolidate the migration into
`scripts/migrate-stub-layout-v1.sh`, which the operator runs
with Jellyfin stopped. **Do not reintroduce a runtime migration
service.**

When a one-shot migration needs to record "this has run" across
plugin restarts, the `plugin_meta` key/value table in `phantom.db`
is the right place. A bash script can read/write it via
`sqlite3` while Jellyfin is stopped, with no concurrency to
worry about. The plugin reads it on startup to decide whether to
emit a "migration not yet run" warning, but does not perform the
migration itself.

## Read first

- `PLAN.md` — milestone tracker, design decisions, the
  source of truth for "what does done mean for this PR."
- `CHANGELOG.md` — what shipped in each release.
- `README.md` — user-facing description, install, usage.
- `docs/agents/testing.md` — **required reading before
  running, or asking the operator to run, any live test.**
  The repo has a dedicated test rig at `/tmp/jf-test/`
  that you drive yourself; do not ask the operator to
  copy DBs, run SQL, or restart Jellyfin.

## Project shape

- C# Jellyfin 10.11.x plugin. Target framework `net9.0`.
- Source: `src/Jellyfin.Plugin.PhantomLibrary/`
- Unit tests: `tests/Jellyfin.Plugin.PhantomLibrary.Tests/`
  — run with `dotnet test`.
- Live integration tests: the rig in `docs/agents/testing.md`.
- Plugin DB: SQLite at
  `/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db`
  on the operator's box, cloned into the rig per test.
- Companion service: `gostream` (separate repo,
  `phantom-library/api-add` and `phantom-library/vault-mode`
  branches). Local instance on `:9080` (API) / `:8090`
  (diagnostics).

## Build

```bash
dotnet build -c Release
```

Output DLL:
`src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll`

## Test

### Unit tests

```bash
dotnet test
```

Always green before opening a PR. If you need to weaken a
test to make a change pass, do not — fix the change.

### Live integration tests

**Read `docs/agents/testing.md` first.** Short version:

- Operator does not want to be in the loop for routine test
  cycles. You have read access to their Jellyfin DBs and a
  rig directory at `/tmp/jf-test/`.
- Spin up your own Jellyfin instance from a clone of the
  prod DB on port `:18096` (production owns `:8096`; do
  not bind it).
- Drive it via REST with a pre-injected API key. Inspect
  the rig's SQLite DBs directly to verify behaviour. Tear
  down. Repeat.
- The process-lifecycle constraint matters: the tool's
  pgroup teardown kills backgrounded Jellyfin between
  bash calls. The entire test
  (start → wait → drive → inspect → kill) must run inside
  a single bash invocation. The doc explains the pattern
  and ships a working `run-test.sh`.

The only legitimate reasons to ask the operator about
testing:

- A required file under `/var/lib/jellyfin/...` is no
  longer world-readable (operator needs to re-`chmod`).
- The `dotnet` runtime or Jellyfin server binary on the
  box has moved or changed version.
- A genuine environmental blocker the doc does not cover
  — and then update the doc once resolved.

Do **not** ask the operator to:

- Copy a DB for you.
- Run a SQL query for you.
- Restart their production Jellyfin.
- Tell you what's in `phantom.db` — read it yourself from
  the rig clone.
- Run `dotnet test` for you.
- Tail a log file for you.

## Coding conventions

- Match the surrounding style. The codebase uses standard
  .NET naming (`PascalCase` types/methods, `_camelCase`
  private fields, `var` where the type is obvious).
- Plugin log categories are `PhantomLibrary` and
  `Phantom.<Subsystem>` (e.g. `Phantom.SeriesIngestor`).
- New scheduled tasks register through Jellyfin's
  `IScheduledTask`; expose IDs as constants so they can be
  triggered via `POST /ScheduledTasks/Running/{id}` from
  the test rig.
- SQLite access for `phantom.db` goes through the existing
  repository abstractions in
  `src/Jellyfin.Plugin.PhantomLibrary/Data/`. Don't open
  raw connections in feature code.
- For Jellyfin DB inspection in tests, raw `sqlite3` CLI
  against the rig clone is fine and expected — you are
  reading, not writing.

## PR hygiene

- Update `CHANGELOG.md` under the unreleased section for
  any user-visible change.
- Update `PLAN.md`'s status table if you complete or move
  a milestone.
- Note tradeoffs and deviations in the PR description per
  the project's `SYSTEM.md` rules (no silent shortcuts,
  no silent stops).
- Bump the plugin version in `manifest.json` and the
  plugin `csproj` together — they must match the directory
  name used by Jellyfin to load the DLL
  (`Jellyfin.Plugin.PhantomLibrary_<version>/`). The test
  rig assumes `0.1.0.0`; bump it everywhere if you change
  it.

## Quick reference

| What | Where |
| --- | --- |
| Testing procedure | `docs/agents/testing.md` |
| Plan + milestones | `PLAN.md` |
| Release notes | `CHANGELOG.md` |
| User docs | `README.md` |
| Plugin source | `src/Jellyfin.Plugin.PhantomLibrary/` |
| Unit tests | `tests/Jellyfin.Plugin.PhantomLibrary.Tests/` |
| Test rig | `/tmp/jf-test/` (ephemeral, rebuild per session) |
| Prod Jellyfin DB | `/var/lib/jellyfin/data/jellyfin.db` (read-only) |
| Prod plugin DB | `/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db` (read-only) |
| gostream config | `/etc/gostream/config.json` (TMDB key lives here) |

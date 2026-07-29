# AGENTS.md

**⚠ ACTIVE IN-FLIGHT ARCHITECTURAL WORK:** if you're picking up this
repo for new development, read
`docs/plans/channel-handoff-onboarding.md` FIRST. It points you at the
current execution plan and what state the codebase is in. Do not start
work on the channel architecture without reading that doc.

Guidance for AI coding agents working in this repo. Humans:
read `README.md`, `PLAN.md`, `CHANGELOG.md` first — those are
authoritative project docs. This file translates them into
conventions an agent needs to operate session-to-session
without re-deriving them.

## Topic → source-of-truth index

Use this file as the routing map. When work touches one of these
areas, read the linked source doc before editing code.

| Topic | Read first | Why |
| --- | --- | --- |
| Current channel-architecture work | `docs/plans/channel-handoff-onboarding.md`, then `docs/plans/channel-handoff.md` | Active execution plan, phase state, known tradeoffs |
| Channel architecture design | `docs/plans/channel-architecture.md` | Channel item model, IDs, materialised state, browse shape |
| Testing / rig usage | `docs/agents/testing.md`, `tools/rig-scenarios/` | Required live Jellyfin rig workflow; unit tests are not enough |
| Movie channel playback/materialise regression coverage | `tools/rig-scenarios/35-channel-e2e-playback.sh` | Movie browse → playback → native-open materialise → stream assertions |
| TV episode playback/materialise regression coverage | `tools/rig-scenarios/36-channel-episode-e2e-playback.sh` | Series → season → episode browse, badge scope, native-open playback |
| Badge state endpoint performance (Home screen) | `tools/rig-scenarios/39-channel-badge-states-perf.sh` | `POST /Plugins/PhantomLibrary/States` must stay sub-second under phantomBadges.js polling on production-shaped data |
| Operator deployment | `docs/operator-deploy.md`, `install.sh` | Patched Jellyfin DLL deployment models, package-manager clobber checks |
| Jellyfin patch maintenance | `scripts/jellyfin-patches/REBASE.md`, `scripts/jellyfin-patches/` | Patch stack, exact upstream tag, rebase workflow |
| Wipe / rebuild validation | `docs/operator-wipe-validation.md`, `scripts/phantom-wipe.sh` | Pre-v1.0 schema-change path, wipe verification |
| Production DB safety | `AGENTS.md` § "Production database safety" | Clone/test/destructive SQL rules; do not improvise SQL in chat |
| Stub naming / legacy sentinel | `AGENTS.md` § "Canonical phantom stub naming scheme" | Native `[tmdbid-<id>]` naming; never create legacy `__phantom_tmdb` paths |
| Historical scanner race / migration failures | `docs/plans/scanner-race-reactor.md`, `docs/plans/M12-investigation-results.md`, `docs/plans/M12-collision-recovery.md` | Why runtime migrations, scanner races, and hand-written SQL are forbidden |
| Release notes / operator-visible changes | `CHANGELOG.md` | Required for user-visible behavior, breaking wipes, deploy notes |
| Milestone status / done definition | `PLAN.md` | Project intent and milestone tracker |
| User-facing behavior/docs | `README.md` | Operator-facing configuration and usage |

If a protocol becomes too detailed for `AGENTS.md`, put the durable
procedure in the relevant design/operator doc above and keep only the
routing rule here.

## No database migrations until v1.0 (always, no exceptions)

**Pre-v1.0, this project does not ship database migrations.** Not
for `phantom.db`, not for `jellyfin.db`, not for any persistent
state the plugin owns. If the schema changes — a new column, a
renamed field, a rekeyed primary key, a change to
what any existing column stores — the upgrade path is **wipe and
rebuild**, not migrate. The sole softened exception is a **purely
additive** delta (new tables / indexes only, touching no existing
table): it MAY *additionally* ship ONE narrow, offline,
`user_version`-guarded, tested `vN→vM` operator script — see
*Softened rule* below. Wipe stays valid and remains the default,
and a runtime / in-plugin migration stays absolutely forbidden.

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
  pre-v1.0 schema versions — **except** the single additive-only
  `vN→vM` operator script permitted by *Softened rule* below. A
  non-additive migration (anything that touches an existing table)
  stays forbidden in the repo; one-off recovery scripts for a
  specific botched upgrade still belong in `/tmp/`, not the repo.
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
6. **Do not** write a migration to bridge between old and new —
   unless the delta is *purely additive*, in which case you MAY
   *additionally* ship the offline `vN→vM` operator script of
   *Softened rule* below (wipe stays a valid path either way).

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

### Softened rule: additive-only deltas may ship an offline `vN→vM` script

A schema bump whose delta is **purely additive** — it only
*creates* new tables and/or indexes and touches **no** existing
table (no `ALTER`, no column change, no re-key, no rewrite of any
existing row) — MAY, in *addition* to the wipe path above, ship one
offline operator migration script in the repo. This is a narrow
carve-out, not a reversal: wipe stays valid and remains the
default, and the runtime / in-plugin migration ban is untouched —
`PhantomDb` still hard-refuses an old-versioned DB at startup rather
than migrating it. Because the delta only *adds* objects, a migrated
DB is byte-for-byte the schema a fresh `EnsureSchemaAsync` produces,
so the script carries none of the old-row-rewriting risk that sank
v0.2.0.0. It is an operator convenience — skip the TMDB
repopulation tick — never a new obligation.

Every such script MUST:

1. **Be offline and out-of-band** — a standalone script the
   operator runs with Jellyfin stopped; never an `IHostedService`,
   startup hook, or anything the plugin invokes.
2. **Guard on `PRAGMA user_version`** — migrate only vN → vM, treat
   an already-vM DB as a verified no-op, and **hard-refuse** any
   other version (directing the operator to wipe) rather than guess.
3. **Be additive-only in SQL** — only `CREATE TABLE` / `CREATE
   INDEX` for the new objects, DDL kept **byte-identical** to
   `PhantomDb`'s schema constant so the result equals a fresh build;
   no statement may touch an existing table.
4. **Default to dry-run**, gate writes behind an explicit `--commit`
   plus a typed confirmation, back the DB up first (with its
   `-wal`/`-shm` sidecars), and apply the DDL + version bump in one
   atomic transaction.
5. **Be idempotent and resumable**, verify the result (version,
   object presence/shape, new tables empty, existing-table counts
   unchanged), and report predicted-before / actual-after counts.
6. **Ship a regression test** that builds a synthetic vN DB, runs
   the script, and asserts the migrated schema matches a real vM
   build — wired into the non-rig CI gate.
7. **Mirror `scripts/phantom-wipe.sh`** in structure and safety
   posture, and be named `phantom-migrate-vN-to-vM.sh`.

The first instance is `scripts/phantom-migrate-v11-to-v12.sh`: v11
→ v12 adds `user_prefs`, `user_hidden_items`, and
`idx_user_hidden_items_user`, and touches no existing table.

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

## Main worktree cleanliness (always, no exceptions)

Do not leave `/home/spencer/git-repos/spencerharmon/phantom-library` dirty at the end of a task. If work is complete enough to build/test, commit it before handing off or switching tasks. If work must remain incomplete, move it to a dedicated worktree or create a WIP commit with an explicit message and status note; do not strand uncommitted edits in main. Never rely on a chat session staying alive to preserve ownership of dirty state.

## Movie/TV parity (always, no exceptions)

TV shows/season/episode flows are first-class. Never implement, optimize, repair, test, document, or declare done for a movie path without auditing the corresponding TV path in the same change. If parity is intentionally impossible, record the reason in code, changelog/PR notes, and tests as an explicit scoped exception; otherwise, TV must ship with equivalent behavior and coverage.

Required parity checklist for any channel/playback/materialise/gostream/badge/availability/cache change:

- Browse: movie list and series → season → episode navigation both work.
- Playback/media sources: movie and episode items both resolve playable/native-open sources.
- Materialise lifecycle: movie and episode state transitions preserve stable IDs and user data.
- gostream/external files: movie files and TV files both appear and play through channels; neither is silently dropped because it is outside `materialised_state`.
- Availability gating: movie and episode/series visibility semantics match unless a TV-specific distinction is documented and tested.
- Cache cleanup/refresh: movie item cache and TV series/season/episode cache are refreshed/pruned with equivalent narrow scoping.
- Badges/UI: movie and episode badges/states behave equivalently; series/season containers remain intentionally badge-free.
- Tests: run/update both `tools/rig-scenarios/35-channel-e2e-playback.sh` and `tools/rig-scenarios/36-channel-episode-e2e-playback.sh` for affected flows. If behavior involves raw gostream/external files, add or run explicit movie + TV external-file rig coverage.

Forbidden shortcut: saying “movies pass” or “unit tests pass” when the analogous TV path was not inspected. That is a bug.

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

## Planning / handoff scope ledger (always)

When the operator asks for planning, plan review, or handoff prompt, create
or update a requirements ledger before producing the final plan/handoff. The
ledger is the scope authority.

Required ledger columns:

- Requirement ID
- Original operator wording or source
- Disposition: `IMPLEMENT`, `EVALUATE`, `DEFER`, or `DROP`
- Acceptance evidence required
- Notes / operator approval link for anything not `IMPLEMENT`

Rules:

- Every operator-requested feature starts as `IMPLEMENT` unless the operator
  explicitly says it is optional, already done, or only needs evaluation.
- Critic/reviewer agents may find contradictions or missing code, but must
  not convert requirements to `DEFER`/`DROP`. They can only mark "code does
  not implement this yet" and recommend either implementation or operator
  disposition.
- Do not rewrite PLAN.md to match incomplete code by silently deferring the
  missing behavior. That is scope laundering.
- Only the operator can approve `DEFER` or `DROP`. Record that approval in
  the ledger; absent approval means still `IMPLEMENT`.
- Handoff prompts must include a traceability check mapping every
  `IMPLEMENT` row to implementation scope, tests, and runtime evidence.
- `EVALUATE` rows are not done until there is written evaluation against the
  current architecture and either an implementation plan or operator-approved
  disposition.
- Final responses for plan/handoff work must state whether every ledger row
  is covered. If not, say `NOT DONE` and list gaps.

## Version handoff / intended-test manifest (always)

When handing off a build for operator testing, include an **INTENDED
TEST TARGET** block. The operator must be able to verify that the DLL,
patched Jellyfin assemblies, schema, and gostream binary they are about
to test are exactly the artifacts you meant them to test. Do not ask the
operator to test from a vague "latest" or "current worktree" state.

Minimum handoff block:

```text
INTENDED TEST TARGET
repo: /home/spencer/git-repos/spencerharmon/phantom-library
branch: <git branch --show-current>
commit: <git rev-parse HEAD>
dirty files: <git status --short, plus git -C gostream status --short>
plugin schema source: <PhantomDb CurrentSchemaVersion>
plugin built sha256: <sha256sum src/.../Jellyfin.Plugin.PhantomLibrary.dll>
plugin deployed sha256: <sha256sum /var/lib/jellyfin/plugins/.../Jellyfin.Plugin.PhantomLibrary.dll>
patched Jellyfin sha256: <MediaBrowser.Controller.dll + MediaBrowser.Model.dll + Jellyfin.Api.dll + Jellyfin.LiveTv.dll>
gostream commit/dirty: <git -C gostream rev-parse HEAD + status>
gostream deployed version/hash: <image id or binary sha, if known>
phantom.db schema: <sqlite PRAGMA user_version>
tests run: <exact commands + pass/fail>
operator steps: <ordered install/restart/test steps>
```

Rules:

- If `plugin built sha256` and `plugin deployed sha256` differ after
  install, say the operator is **not testing the intended plugin**.
  Stop and reinstall; do not debug behavior from a mismatched DLL.
- If `plugin schema source` and `phantom.db schema` differ, call out
  whether this is expected (fresh DB not yet created, or wipe required)
  before restart/testing.
- If gostream behavior changed, include gostream status/hash in the
  handoff and say explicitly whether gostream must be rebuilt/restarted.
- If multiple worktrees are active, name the exact repo path used for
  build/install. Do not say "main" when you mean a worktree or vice
  versa.
- `install.sh` prints a post-install verification block; include it or
  its relevant lines in the handoff when install/deploy happened during
  the turn.

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

## Jellyfin/gostream submodules and patch dependency

`jellyfin/` and `gostream/` are git submodules. A fresh clone must be
installable with:

```bash
git submodule update --init --recursive
./install.sh --build
```

`install.sh --build` may initialise missing submodules, but correctness
must never depend on untracked local checkout state. Keep the submodule
SHA recorded in the phantom-library commit aligned with the patches and
plugin code in that same commit.

This plugin depends on patches applied to Jellyfin core, stored at
`scripts/jellyfin-patches/`. `install.sh --build` applies them at
build time (idempotently — second run reports `already applied`)
against the `jellyfin/` submodule at exact tag v10.11.9 (base SHA
`e83a7e62f2`). The patches add `IChannelItemRefresh` (an opt-in
channel-side interface), `IChannelItemRefreshManager` (a new
service sibling to `IChannelManager`), and a server-advertised
item-action API (`IItemActionProvider` + `/Items/{itemId}/Actions`) —
all purely additive. No existing API is modified.

The plugin DLL alone is **not sufficient** at runtime; the patched
`MediaBrowser.Controller.dll` + `MediaBrowser.Model.dll` +
`Jellyfin.Api.dll` + `Jellyfin.LiveTv.dll` must also be
deployed into the operator's Jellyfin install dir (default
`/usr/lib/jellyfin/`). `install.sh --build` prints the exact
`sudo cp` commands at the end of its output, pre-filled for the
detected install dir. See `docs/operator-deploy.md` for the
operator-facing companion guide (Model A in-place swap; Model B
run-from-build-tree) and the package-manager-clobber detection
procedure.

On Jellyfin upstream updates, the patches may need rebasing.
`install.sh --build` aborts with an actionable error if a patch
fails to apply. **Never ignore, skip, or special-case a patch that
does not apply cleanly.** A broken patch means the repository is not in
an installable state. Fix the patch stack or advance the `jellyfin/`
submodule to the commit the patch stack targets, then verify from a
fresh submodule checkout. Rebase by applying via `git am`, resolving
conflicts, and re-exporting via `git format-patch`. See
`scripts/jellyfin-patches/REBASE.md`.

When changing Jellyfin-dependent behavior:

1. Update `scripts/jellyfin-patches/`.
2. Verify the patch series applies from the recorded `jellyfin/`
   submodule commit with no pre-existing local modifications.
3. Build patched Jellyfin from that submodule.
4. Commit any required `jellyfin/` submodule SHA change together with
   the plugin/patch changes.
5. Leave `jellyfin/` clean enough that `git submodule update --init`
   plus `./install.sh --build` reproduces the intended state.

When changing gostream-dependent behavior, update and commit the
`gostream/` submodule SHA in the same phantom-library commit as the
plugin or install-script change that requires it. Do not rely on a
locally built image or an untracked gostream checkout.

Phase 8 (deferred): upstream PR. Per Jellyfin's LLM/AI
contribution policy, this PR must be operator-authored with the
operator understanding and able to defend every line. See
`docs/plans/channel-handoff.md` § Phase 8 for the upstream
procedure. Agents do **not** author the Meta discussion, the PR
body, or responses to review comments.

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
- Companion service: `gostream/` submodule. Local instance on `:9080`
  (API) / `:8090` (diagnostics).

## Build

```bash
MSBUILDDISABLENODEREUSE=1 dotnet build -c Release -p:UseSharedCompilation=false
```

Output DLL:
`src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll`

## Test

### Build/test process cleanup is mandatory

Agents must not leave `dotnet`, `MSBuild.dll`, `VBCSCompiler`,
`testhost`, or rig Jellyfin processes running after any build,
test, install, or rig scenario. This box is memory-constrained;
orphaned build servers and rig servers have caused OOMs.

Always run builds/tests with reusable build servers disabled unless a
specific command cannot work that way:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet build -c Release -p:UseSharedCompilation=false
MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false
```

Every shell invocation that starts `dotnet build`, `dotnet test`,
`install.sh --build`, or a rig Jellyfin instance must include cleanup
on exit:

```bash
cleanup_dotnet() {
  dotnet build-server shutdown >/dev/null 2>&1 || true
  pkill -u "$USER" -f 'jellyfin.dll --datadir /tmp/jf-test' || true
}
trap cleanup_dotnet EXIT INT TERM
```

At end of task, verify no agent-owned leftovers remain:

```bash
ps -u "$USER" -o pid,ppid,pgid,stat,etime,rss,cmd \
  | grep -E 'dotnet|MSBuild.dll|VBCSCompiler|testhost|jellyfin.dll --datadir /tmp/jf-test' \
  | grep -v grep || true
ss -ltnp | grep ':18096' || true
```

If leftovers exist, clean them before handoff:

```bash
dotnet build-server shutdown || true
pkill -u "$USER" -f 'MSBuild.dll /noautoresponse' || true
pkill -u "$USER" -f VBCSCompiler || true
pkill -u "$USER" -f testhost || true
pkill -u "$USER" -f 'jellyfin.dll --datadir /tmp/jf-test' || true
```

Never kill production Jellyfin (`/usr/bin/jellyfin`, user `jellyfin`,
port `:8096`) as part of agent cleanup. Only clean agent-owned rig
Jellyfin bound to `:18096` / `/tmp/jf-test`.

### Unit tests are necessary but not sufficient

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false
```

Always keep unit tests green. If you need to weaken a test to
make a change pass, do not — fix the change.

**Do not call a channel, playback, install/deploy, Jellyfin patch,
materialisation, badge/UI, gostream-path, scheduled-task, or database-
shape change done based on unit tests alone.** Unit tests have missed
real regressions in this repo because Jellyfin's channel cache,
`PlaybackInfo` / `LiveStreams/Open` flow, static vs dynamic media-source
merging, patched assemblies, browser MutationObserver behaviour, and
SQLite/Jellyfin BaseItems shape only exist in the live server path.

### Live integration tests are mandatory for user-visible flows

**Read `docs/agents/testing.md` first.** Short version:

- Operator does not want to be in the loop for routine test
  cycles. You have read access to their Jellyfin DBs and a
  rig directory at `/tmp/jf-test/`.
- Use the rig scripts in `tools/rig-scenarios/` or add a new
  scenario when existing coverage does not exercise the bug/fix.
  A regression reported from prod should first become a failing
  rig scenario whenever practical, then pass after the fix.
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
  and ships working rig scripts.

Minimum expectations before marking a user-visible change done:

1. `dotnet build -c Release`
2. `dotnet test`
3. Relevant rig scenario(s), for example:
   - `tools/rig-scenarios/35-channel-e2e-playback.sh` for movie
     channel browse/playback/materialise.
   - `tools/rig-scenarios/36-channel-episode-e2e-playback.sh` for TV
     series → season → episode browse/playback/materialise.
4. If no existing rig scenario covers the changed behaviour, add one
   or extend the closest scenario. Do not substitute manual clicking or
   unit tests for rig coverage.
5. When production verification is still useful, use Jellyfin API/log
   APIs yourself after the rig passes. Do not ask the operator to tail
   logs, restart prod for routine checks, or run SQL for you.

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

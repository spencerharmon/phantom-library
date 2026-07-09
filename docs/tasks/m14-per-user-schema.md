# REQ-M14-PER-USER — per-user schema foundation (v12)

Task: `m14-per-user-schema`. Scope: **schema only.** This change adds the two
additive per-user tables `PhantomDb` needs for real per-user semantics, bumps the
schema version to v12, and stops there. It ships **no** read/write accessors, no
controller, no channel/eviction/probe wiring — those land in the dependent
`m14-per-user-backend` (and the surface-specific tasks after it).

Read `docs/tasks/m14-per-user-eval.md` first: it is the evaluation this task
implements. The operator dispositioned REQ-M14-PER-USER as **Branch B — real
per-user semantics** (2026-07-09). This task delivers the storage substrate for
the eval's **Surface 1** (per-user preferences store) and **Surface 3** (per-user
show/hide set). Surfaces 2 (favourite eviction-protection) and 4 (source-probing)
add no new tables — the eval found their per-user qualifier is a threading problem
on existing state, not a storage gap — so they are untouched here.

## What changed

`src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs`:

- `CurrentSchemaVersion` `11 → 12`.
- Two additive `CREATE TABLE IF NOT EXISTS` blocks (+ one index) appended to the
  fresh-schema DDL string. They touch **no** existing table.
- Updated the class-level schema-history doc comment.

`tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs`:

- `FreshDb_CreatesSchemaV11...` → `...V12...`; asserts `user_version == 12` and adds
  `user_prefs` + `user_hidden_items` to the expected-table set.
- `HardRefuse_OldSchemaVersion...` gains `InlineData(10)` + `InlineData(11)` and
  asserts the message names `version 12` — i.e. v10/v11 DBs are now refused too.
- Two new shape tests (`user_prefs` toggles/PK/defaults; `user_hidden_items`
  composite PK) + a `ReadColumnsAsync` `PRAGMA table_info` helper.

`CHANGELOG.md`: a `BREAKING: requires wipe` entry under Unreleased.

## Table shapes and why

### `user_prefs` — one row per user, the toggle set

```sql
user_id            TEXT NOT NULL PRIMARY KEY,   -- Jellyfin user GUID
protect_favourites INTEGER NOT NULL DEFAULT 1 CHECK(protect_favourites IN (0,1)),
show_phantoms      INTEGER NOT NULL DEFAULT 1 CHECK(show_phantoms IN (0,1)),
allow_eager        INTEGER NOT NULL DEFAULT 1 CHECK(allow_eager IN (0,1)),
updated_at         INTEGER NOT NULL
```

- The three toggles are exactly the columns the removed per-user admin page
  (`Configuration/userPrefsPage.html:57-59`, still embedded, still dead) exposed:
  `protectFavourites` / `showPhantoms` / `allowEager`. Reviving that surface is the
  operator's Branch-B intent; this table is where those choices persist.
- **Defaults are `1` (on)** and the columns are `NOT NULL`, so a user who never
  saved a preference reads as "all on" — the backend interprets an **absent row**
  as the default; the schema only ever persists an explicit write. This keeps the
  fresh-install behaviour identical to today's server-wide defaults.
- `CHECK (… IN (0,1))` keeps the toggles a genuine boolean at the storage layer.
- **Favourites are deliberately not a column here.** Per the eval (Surface 2),
  favourite state lives in Jellyfin's own `UserData` and is read live; duplicating
  it into `phantom.db` would create a second source of truth to keep in sync.

### `user_hidden_items` — the per-user hidden set

```sql
user_id   TEXT NOT NULL,
tmdb_id   INTEGER NOT NULL,
type      TEXT NOT NULL CHECK(type IN ('movie','series')),
hidden_at INTEGER NOT NULL,
PRIMARY KEY (user_id, tmdb_id, type)
-- + idx_user_hidden_items_user ON (user_id)
```

- **A separate table from `user_prefs`, on purpose.** Prefs are a single toggle row
  per user (1:1); the hidden set is 0..N titles per user (1:N). Folding an unbounded
  set into the prefs row would force JSON-blob denormalisation and lose per-title
  query/index ability. Different cardinality ⇒ different table.
- **Composite PK `(user_id, tmdb_id, type)`** matches the catalogue's title
  identity, so one user hiding a title never collides with another user hiding the
  same title. `idx_user_hidden_items_user` serves the hot query — "the set of titles
  hidden by user X" — that Surface 3's per-user visibility filter will run.
- `type` is `movie` | `series` (title-level, matching the movie/TV visibility
  queries `ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync`). Episodes are
  not independently hidden — hiding is a title-level operation, consistent with the
  eval's Surface-3 description.

## No migration — wipe and rebuild (per AGENTS.md)

`AGENTS.md` § "No database migrations until v1.0" is absolute and explicitly
forbids treating an additive table change as a "non-destructive upgrade". So even
though the v12 delta only *adds* tables:

- `EnsureSchema` still creates the schema from scratch on a **fresh** DB only.
- Every pre-v12 `user_version` is **hard-refused** with the existing wipe pointer
  (the `version > 0 && version < CurrentSchemaVersion` branch). This task does not
  weaken that refusal.
- Upgrade path for the operator = **wipe** (`scripts/phantom-wipe.sh`,
  `docs/operator-wipe-validation.md`), then restart; the plugin recreates v12 and
  `SuggestionsRefreshTask` repopulates from TMDB. No per-user state is lost because
  none exists yet at the time of this bump.

A no-wipe additive-upgrade path is **out of scope** and tracked separately as
`db-migration-script`, gated by the project's v1.0 migration policy (a
general-purpose in-repo migration script is itself forbidden pre-v1.0 per the same
AGENTS.md section). This task must not pre-empt that decision.

## Out of scope (explicit)

- **Accessors / backend** (`user_prefs` upsert/read, hidden-set add/remove/list,
  default-on-missing-row logic) → `m14-per-user-backend`.
- **Surface wiring**: threading `userId` into eviction (Surface 2), channel
  visibility + cache-key (Surface 3 runtime), and probing (Surface 4).
- **Reviving `userPrefsPage.html`** and its REST endpoints (a later surface task).
- **Renaming the `SchemaV10Sql` constant.** It has been the misnamed home of the
  full fresh-schema DDL since before v11; renaming it is unrelated churn and is left
  as-is to keep this diff schema-focused.
- **Plugin version / `manifest.json` bump.** Unchanged (`0.3.0.0`, unreleased).

## Verification

`dotnet build -c Release` / `dotnet test` require the sibling `jellyfin/` source
checkout (the plugin `ProjectReference`s patched Jellyfin assemblies — see
`AGENTS.md` § "Jellyfin patch dependency"). That checkout is absent in this
worktree, so the C# test run could not be executed here.

The schema itself was validated directly with `sqlite3` by extracting the full
`SchemaV10Sql` DDL and creating a database from it:

- All 15 pre-existing tables + the 2 new tables create cleanly; `PRAGMA
  user_version` reports `12`.
- `user_prefs`: `user_id` is the sole PK; the three toggles are `NOT NULL DEFAULT
  1`; inserting a row with the toggles omitted yields `1/1/1`; a toggle value of `2`
  is rejected by the CHECK.
- `user_hidden_items`: `(user_id, tmdb_id, type)` composite PK (positions 1/2/3);
  `type = 'episode'` is rejected by the CHECK; the same `(tmdb_id, type)` for two
  different users coexists.

The new xUnit shape tests encode exactly these assertions so they run under CI once
a Jellyfin checkout is present. When picking this up with the rig available, run
`dotnet build -c Release` + `dotnet test`; no live rig scenario is needed because
this task adds storage only and changes no channel-visible behaviour.

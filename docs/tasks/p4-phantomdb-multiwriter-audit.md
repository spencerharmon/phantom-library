# P4 Stage A — `phantom.db` multi-replica safety audit

Task: `p4-phantomdb-multiwriter-audit` (EVALUATION only; no code change). This doc
records the finding and a recommended disposition per the P4 direction "N Jellyfin
replicas will share one `jellyfin.db`". Per `AGENTS.md` § "Planning / handoff scope
ledger", an eval agent may only mark "code does not implement this yet" and recommend
a disposition; the operator owns the final `IMPLEMENT`/`DEFER`/`DROP` call.

All `file:line` citations were read against the current channel-architecture tip in the
`bee-p4-phantomdb-multiwriter-audit` worktree (schema v12,
`PhantomDb.CurrentSchemaVersion = 12`).

## Bottom line

`phantom.db` is **NOT multi-writer safe.** Every concurrency guard it has is
**process-local**, not cross-process, and the storage engine is a single-file SQLite DB
whose safe concurrency model is one OS process (one WAL) at a time. Running N Jellyfin
replicas that **share one `phantom.db` file** (a `ReadWriteMany` volume) would produce
data corruption, `SQLITE_BUSY` write failures, and incorrect cross-process sweeps.

**Recommendation, in priority order:**

1. **Preferred / lowest-risk: keep `phantom.db` as a per-replica (non-shared) SQLite
   file** — do NOT put it on the shared `ReadWriteMany` volume that carries the shared
   `jellyfin.db`. This requires deciding what phantom state must be *shared* vs. what can
   be *rebuilt per replica* (see "Blocker for keep-as-is" below); it is not automatically
   correct, but it avoids the SQLite-multi-writer failure mode entirely.
2. **If phantom state MUST be shared across replicas: migrate `phantom.db` to the same
   MySQL used for the shared `jellyfin.db`** (the `p4-mysql-migration-impl` path). This is
   the only option that makes the plugin's writers, dedup lock, and sweepers correct under
   concurrent replicas.
3. **Reject: shared-`ReadWriteMany`-SQLite.** SQLite over a network/shared filesystem with
   multiple writing processes is explicitly unsafe (broken advisory locking on NFS/CIFS,
   WAL requires shared memory that does not work across hosts). Do not ship this.

The choice between (1) and (2) is a real architectural decision that needs operator input
— see "Operator decision required" — because it depends on whether per-replica divergence
of phantom state is acceptable, which the ROI/P4 direction does not state.

## What "shared `jellyfin.db`" implies for `phantom.db`

`phantom.db` is a **separate file from `jellyfin.db`**, opened by the plugin at
`PluginConfigurationsPath/PhantomLibrary/phantom.db`
(`PluginServiceRegistrator.cs:48`). So migrating/sharing `jellyfin.db` does **not** by
itself share or fix `phantom.db`. Two independent questions:

- **Is the plugin-config dir on the same shared volume as `jellyfin.db`?** If the P4
  deployment mounts the whole Jellyfin data/config tree as one shared `ReadWriteMany`
  volume, then `phantom.db` becomes shared *as a side effect* — which is exactly the
  unsafe multi-writer-SQLite case (option 3). This must be checked in the deployment
  manifests, not assumed. If instead each replica gets its own config dir, `phantom.db`
  is already per-replica (option 1) and the only remaining work is the shared-state
  question.
- **Does phantom state NEED to be shared?** See the state inventory below.

## Concurrency model as built (all process-local)

1. **Single writer via a process-wide `SemaphoreSlim`.** `PhantomDb._writeLock =
   new SemaphoreSlim(1,1)` (`PhantomDb.cs:208`) serialises every write; the class doc at
   `PhantomDb.cs:179-181` states "Single writer, serialised via a process-wide
   SemaphoreSlim". This is an **in-process** primitive — it does nothing across separate
   replica processes. Two replicas each hold their own semaphore and will issue concurrent
   writes to the same file.
2. **No `busy_timeout` PRAGMA is set.** `EnsureSchema` sets only `PRAGMA journal_mode=WAL`
   (`PhantomDb.cs:265`); there is no `PRAGMA busy_timeout`. In-process that is fine (the
   semaphore guarantees no write contention), but cross-process it means a second writer
   gets an **immediate `SQLITE_BUSY`** rather than waiting — every cross-replica write race
   throws. The absence of `busy_timeout` is itself evidence the design assumes a single
   writing process.
3. **WAL + `Cache=Shared` + `Pooling=true`** (`PhantomDb.cs:224-230`). WAL's writer
   coordination relies on a shared-memory (`-shm`) file that is **not portable across
   hosts/containers** on a network filesystem, so WAL does not make multi-host writers
   safe; it makes them worse (readers can see a corrupt WAL index).

## State inventory — what would need sharing, what is disposable

Writers/tables in `phantom.db` (schema block `PhantomDb.cs:325-609`), classified for the
"does this need to be shared across replicas?" decision:

- **Pure caches, rebuildable per replica** (safe to keep per-replica; divergence only costs
  a re-fetch): `magnet_cache`, `magnet_failure_cache`, `tmdb_cache`, `tmdb_metadata`,
  `tmdb_episode_cache`, `tmdb_external_ids`, `discovery_cache`. These are TTL-swept caches
  (`DELETE FROM … WHERE …< $now` writers at `PhantomDb.cs:693,786,955,1078` etc.). Per-replica
  copies just warm independently.
- **Scheduler / catalogue state** (`catalogue_items`, `series_expansion_state`,
  `series_episode_catalogue`, `availability_items`, `unavailable_marker`): append-only /
  scheduler state that drives what titles the channel offers. If two replicas each run
  `DiscoveryRefreshTask` (`PluginServiceRegistrator.cs:134`) against their own copy, they
  converge to the same TMDB-derived content, so per-replica is *tolerable* but wasteful
  (N× TMDB traffic). If shared, it must be in a real multi-writer store.
- **Materialisation coordination** (`materialised_state`, `materialise_in_flight`): **this
  is the load-bearing correctness hazard.** `materialise_in_flight`
  (`PhantomDb.cs:436-443`) is used as a **cross-request dedup lock** via
  `INSERT OR IGNORE` (`TryInsertMaterialiseInFlightAsync`, `PhantomDb.cs:2309-2331`) so two
  concurrent requests do not double-materialise the same title. Across replicas with
  **separate** DBs this lock does not span replicas → **two replicas can materialise the
  same title simultaneously** (double work, possible on-disk stub collision — exactly the
  class of collision `docs/plans/M12-collision-recovery.md` exists for). Across replicas
  with a **shared** SQLite DB the lock would span them but the writes race unsafely.
- **The startup sweeper assumes a single process.** `MaterialiseInFlightSweeper`
  (`Materialisation/MaterialiseInFlightSweeper.cs`) purges `materialise_in_flight` rows
  older than `MaterialiseInFlightStaleMinutes` and its own doc comment
  (`MaterialiseInFlightSweeper.cs:22-24`) says rows younger than the threshold "are presumed
  to belong to an actively-running materialise **on this very process**." With multiple
  replicas sharing the DB, replica A's sweeper would purge replica B's genuinely-in-flight
  lock row once it crosses the age threshold — silently breaking B's dedup guard mid-run.
  `PurgeStaleMaterialiseInFlightAsync` (`PhantomDb.cs:2381-2390`) deletes purely by
  `started_at` age with **no owner/host column**, so it cannot distinguish "my crashed row"
  from "another replica's live row."
- **Per-user tables** (`user_prefs`, `user_hidden_items`, `PhantomDb.cs:584-609`, v12
  additive). These carry genuine per-user state (not per-replica) that **must be shared** or
  a user's hidden-set / prefs would depend on which replica served the request. They are the
  foundation for REQ-M14-PER-USER (see `docs/tasks/m14-per-user-eval.md`). This is the one
  category where per-replica copies are user-visibly *wrong*, not merely wasteful — so if
  per-user prefs ship and replicas are real, this state has to live in a shared store.
  (Note: favourites/watched are already in `jellyfin.db`'s `UserData`, not here — see
  `PhantomDb.cs:193-194`.)
- **`plugin_meta`** (`PhantomDb.cs:527`): migration/one-shot bookkeeping; per-replica is
  fine, or shared is harmless.

## Blocker for "keep-as-is" (per-replica) — the dedup lock

Even the preferred per-replica option (1) has a correctness gap: `materialise_in_flight`
stops being a cross-replica materialisation lock. Two replicas can be asked to materialise
the same title concurrently and neither sees the other's in-flight row. Mitigations, in
order of preference, all deferrable to `p4-mysql-migration-impl` or a dedicated follow-up:

- Route materialisation through a **single owner** (leader replica / the
  `MaterialisationQueue` on one node), so only one process ever writes materialise state.
- OR move just `materialise_in_flight` + `materialised_state` into the shared MySQL store
  (partial migration) while keeping the caches per-replica.
- OR make materialisation idempotent + collision-safe at the filesystem layer so a double
  materialise is harmless (heavier; relies on M12 collision recovery already in place).

This gap is why "keep-as-is" is **not** a no-op: it is the lowest-risk *storage* choice but
it still needs an explicit materialisation-ownership decision.

## Operator decision required (not auto-resolvable by this eval)

The eval cannot self-approve the disposition (scope-ledger rule). The operator must decide:

- **Will phantom-config (`PluginConfigurationsPath`) be a per-replica volume or the shared
  `ReadWriteMany` volume?** If shared, option 3 (unsafe) is being taken by accident and must
  be changed. This is a deployment-manifest fact to confirm.
- **Must per-user prefs / hidden-items be consistent across replicas?** If yes (real
  multi-user, ≥2 users), the per-user tables force a shared store → option 2 (MySQL). If the
  deployment stays effectively single-user (per `AGENTS.md` § "Single-operator deployment"),
  per-replica is behaviourally identical and option 1 suffices.
- **Is double-materialisation acceptable, or must materialisation be single-owner/shared?**

## Disposition recommended to the operator

- **Default recommendation: option 1 (per-replica `phantom.db`) + single-owner
  materialisation**, because most of `phantom.db` is rebuildable cache and the project's
  stated deployment is single-operator. This is the smallest, safest change and does not
  require a MySQL port of the plugin store.
- **Escalate to option 2 (migrate `phantom.db` to MySQL alongside `jellyfin.db`) IF** the
  operator confirms real multi-user per-user state must be shared across replicas, OR that
  materialisation cannot be constrained to a single owner. `p4-mysql-migration-impl` (which
  depends on this audit) is the vehicle for that.
- **Never option 3 (shared SQLite on `ReadWriteMany`).** Record this as a hard "do not do."

This finding does not change runtime behavior (`check=none`); it feeds the disposition for
`p4-mysql-migration-impl` and any follow-up materialisation-ownership task.

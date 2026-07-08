# M14 per-user evaluation — REQ-M14-PER-USER (channel-arch state)

Date: 2026-07-08. Task: `m14-per-user-eval` (evaluation only; implementation is
`m14-per-user-impl`). Companion to `docs/plans/m14-ledger-evaluation.md`.

Ledger row (PLAN.md:64): "Per-user preferences / favourite eviction protection /
show-hide / source-probing controls must be implemented **or re-evaluated with
operator** after channel refactor." Disposition IMPLEMENT; acceptance evidence =
"API/UI/tests for per-user behavior, **or operator-approved disposition change**."

This doc does NOT convert scope to DEFER/DROP (an agent may not self-approve that).
It records the finding and hands `m14-per-user-impl` a concrete testable scope plus
a disposition recommendation.

## Bottom line

The channel-refactor plugin has **zero persisted per-user state**. Of the four
named surfaces: favourite eviction-protection and source-probing exist but are
**server-wide / admin-only**, never per-user; per-user show/hide does not exist;
the per-user preferences store (`user_prefs` table) and its admin sub-page were
**removed in "Stage 2.2"** and only a dead, still-embedded HTML shell remains.
Reviving true per-user semantics is net-new, multi-user machinery.

## Deployment context (load-bearing for disposition)

`AGENTS.md` § "Single-operator deployment": *"This project is deployed in a
single-operator environment. The operator owns the box and the Jellyfin process.
There are no external users …"* Under one Jellyfin user, "per-user X" is
functionally identical to the existing server-wide toggle for **every** surface
below. Per-user granularity only produces different behavior when ≥2 users hold
divergent favourite/visibility/probe preferences — a condition this deployment
does not have. That does not authorize a DEFER (operator-only), but it is the
central fact the operator must weigh.

---

## Surface 1 — Per-user preferences store

- **User-visible behavior (now):** none. No plugin setting is keyed to a Jellyfin
  user. All knobs are server-wide plugin config.
- **Code entry points:**
  - `Configuration/PluginConfiguration.cs` — every field is global (server-wide
    XML config). The class remark (`PluginConfiguration.cs:11-15`) still claims
    per-user toggles "live in a separate per-user table inside `PhantomDb`" —
    this is **stale/false** post-Stage-2.2; no such table exists.
  - `Api/PhantomLibraryController.cs:20-24` (remark): *"The per-user-preferences
    endpoints were removed in Stage 2.2; the underlying `user_prefs` table is gone
    with the file-on-disk architecture. Channel-arch may re-introduce per-user
    controls in a later stage."*
  - `State/PhantomDb.cs` — schema (all tables keyed by `tmdb_id`/`type`/`season`/
    `episode`); no `user_id`/`user_guid` column anywhere.
- **Still relevant post-refactor?** Only if the operator wants multi-user
  divergence. Single-user ⇒ inert.
- **Risk if implemented:** a per-user table is a **schema change** ⇒ triggers the
  no-runtime-migration rule (bump `PhantomDb` schema version, wipe + rebuild,
  CHANGELOG "BREAKING: requires wipe"). Not free.
- **Exact tests if implemented:** `PhantomDbTests` round-trip for the new
  per-user table (upsert/read/delete keyed by user GUID; default-on-missing-row);
  schema-version bump asserted; controller auth test (per-user writes require the
  acting user or elevation).

## Surface 2 — Favourite eviction-protection

- **User-visible behavior (now):** a materialised file is protected from idle
  eviction if **any** user has favourited it (or `ProtectFavourites=false`
  disables protection globally). There is no way for user A to protect only their
  own view, nor to evict something user B favourited.
- **Code entry points:**
  - `Materialisation/EvictionSweeper.cs:195-388` `RunOnceAsync`. It loads all users
    (`_userManager.GetUsers()`, `:208`), and per candidate row iterates every user
    (`foreach (var user in users)`, `:283`) aggregating `IsFavorite`/`LastPlayedDate`
    into a single global decision: `if (ud.IsFavorite) isFav = true;` (`:299-302`),
    then `if (protectFavourites && isFav) { skip }` (`:305-312`). Header comment
    `:25-27` confirms "checks per-user LastPlayedDate / IsFavorite **across all
    users**".
  - Config: `PluginConfiguration.cs:177` `ProtectFavourites` (global). Doc `:172-176`
    is explicit: *"materialised items with **at least one favouriting user** are
    protected"* — i.e. any-user, not per-user.
- **Still relevant post-refactor?** The protection itself is relevant and working.
  The **per-user** qualifier is inert single-user; multi-user it changes who can
  pin/evict a shared file.
- **Risk if implemented:** eviction is a shared, global resource decision — a
  materialised file is one file on disk shared by all users. "Per-user protection"
  is semantically ambiguous: does one user's favourite still pin the shared file
  for everyone (yes, physically it must)? Then per-user protection only means
  "stop pinning once the LAST favouriting user un-favourites" — which the current
  any-user aggregation **already** implements. So there is little genuine per-user
  behavior to add here beyond the current logic.
- **Exact tests if implemented:** extend `EvictionSweeperTests` with a multi-user
  case: users {A fav, B not} ⇒ protected; {A un-fav, B not, both idle} ⇒ evicts;
  assert the "last favouriting user" boundary. (Current coverage:
  `EvictionSweeperTests.FavouriteProtected_NoEviction:200-224` uses a single user
  and asserts any-favourite protects — no cross-user case exists.)

## Surface 3 — Show/hide (per-user visibility)

- **User-visible behavior (now):** every user sees the identical channel set.
  Visibility is gated purely by availability/materialised state, server-wide.
- **Code entry points:**
  - `State/PhantomDb.cs`: `ListVisibleMovieRowsAsync` (`:1652`, `WHERE m.type='movie'
    AND (ms.tmdb_id IS NOT NULL OR a.status='available')`), `ListVisibleSeriesRowsAsync`
    (`:1681`), `IsSeriesVisibleAsync`, `IsEpisodeVisibleAsync` — none take a userId.
  - `Channels/PhantomMoviesChannel.cs:86` / `Channels/PhantomShowsChannel.cs:122`:
    `IsEnabledFor(string userId) => true` — userId ignored. `GetChannelItems` keys
    off `query.FolderId` only; `GetLatestMedia` (`PhantomMoviesChannel.cs:257`)
    "carries UserId only" and does not filter by it.
- **Still relevant post-refactor?** Only for multi-user. Single-user ⇒ inert.
- **Risk if implemented:** **high Jellyfin-plumbing risk.** Jellyfin's `IChannel`
  contract passes `folderId` for browse, and the channel-item **cache** is not
  keyed per user; injecting per-user filtering risks cache cross-contamination
  (one user's hidden set leaking into another's browse). Would likely need a
  per-user hidden-set table + cache-key/DataVersion changes. Must preserve
  movie/TV parity (35/36 rig).
- **Exact tests if implemented:** unit test on the visibility query with a userId
  + hidden-set; **mandatory rig** coverage (extend `35-channel-e2e-playback.sh` /
  `36-channel-episode-e2e-playback.sh`, or a new scenario) proving user A's hidden
  item is absent for A but present for B in a live browse — unit tests alone are
  insufficient for channel/cache behavior.

## Surface 4 — Source-probing controls (per-user)

- **User-visible behavior (now):** source probing is background + admin-only; a
  user cannot trigger, scope, or gate probing by their own interactions.
- **Code entry points:**
  - `Sources/MagnetSelector.cs:133` `ProbeAsync(...)` — no userId; ranks by global
    config (`cfg.QualityPreset`, `MinSeeders`, …). Indexer enablement is global
    (`Clients/ProwlarrClient.cs`, `Clients/TorrentioClient.cs`).
  - `Scheduled/AvailabilityProbeWorker.cs` — background loop gated on
    `cfg.AvailabilityProbeEnabled`; claims work with a **machine-scoped** lease
    owner, not a user.
  - `Api/PhantomLibraryController.cs` — `Items/{externalId}/Sources` +
    `RejectCurrent` + `MaterialiseCandidate` are `[Authorize(Policy =
    "RequiresElevation")]` (admin-only), no userId.
  - The removed per-user slice = the `allowEager` "Allow background source probing
    based on this user's interactions" toggle (PLAN.md:570).
- **Still relevant post-refactor?** Only if the operator wants probing driven per
  user. Note `UserDataSavedListener` already turns a user's favourite/play into a
  **global** materialise trigger (`UserDataSavedListener.cs:81`), so "probe based
  on my interactions" is largely already achieved globally.
- **Risk if implemented:** per-user probing multiplies probe/indexer load per user
  and has **no seam** — userId would have to be threaded from the interaction
  hooks through `MagnetSelector.ProbeAsync`, the availability lease/queue in
  `PhantomDb`, and possibly gostream. High blast radius on a background subsystem.
- **Exact tests if implemented:** unit tests threading userId into the probe
  trigger + a per-user enable gate; assert a disabled user's interaction does not
  enqueue a probe; queue/lease tests in `PhantomDbTests`.

---

## The removed admin sub-page (explicit ROI Priority-1 item; NOT an approved deferral)

This is the item the ledger flags as **not yet an approved deferral** (PLAN.md:
186-189). Concrete artifacts:

- **`Configuration/userPrefsPage.html`** (111 lines) — a per-user admin form with a
  table of users × three toggles: `protectFavourites` / `showPhantoms` / `allowEager`
  (`:57-59`). It loads via `GET Plugins/PhantomLibrary/UserPrefs` (`:68`) and saves
  via `POST Plugins/PhantomLibrary/UserPrefs/{uid}` (`:94`).
- **Wiring state — orphaned:** the page is still declared
  `<EmbeddedResource Include="Configuration\userPrefsPage.html" />`
  (`Jellyfin.Plugin.PhantomLibrary.csproj:73`), so it is **compiled into the DLL**,
  but it is **NOT** returned by `Plugin.GetPages()` (`Plugin.cs:54-96` registers only
  `configPage.html`, `phantomKebab.js`, `phantomBadges.js`). So it is unreachable
  from the dashboard, and its two REST endpoints no longer exist
  (`PhantomLibraryController.cs:20-24`) — both calls would 404.
- **Vestigial backend:** `PhantomLibraryController` still constructor-injects
  `IUserManager _userManager` and `PhantomDb _db` used only as discards
  (`_ = _userManager; _ = _db;` around `:150-151`) — dead weight left from the
  removed `UserPrefs` endpoints.
- **Risk of leaving as-is:** a config page shipped in the DLL that POSTs to 404
  endpoints is a latent correctness/operator-confusion smell; it also keeps the
  false `PluginConfiguration.cs:11-15` "per-user table" doc alive. Low severity,
  but it is exactly the "documented partial" the operator must dispose of.

---

## Disposition recommendation

**Recommendation: this is a genuinely operator-only disposition decision.**
`m14-per-user-impl` should **escalate** via
`beehive task human phantom-library m14-per-user-impl --reason "..."` presenting
the two paths below. Rationale (honest, not a self-approved DEFER):

1. In the documented single-operator deployment, per-user granularity on all four
   surfaces is functionally inert — it produces identical behavior to the existing
   server-wide toggles until a second user with divergent preferences exists.
2. The "implement per-user" path is high-cost multi-user machinery: a new per-user
   table (schema bump ⇒ **wipe**), threading userId through a background eviction
   service, the channel visibility/cache path (Jellyfin-plumbing risk), and the
   probe subsystem — for zero behavioral change in the actual deployment.
3. Whether the current server-wide behavior is the **accepted final contract** vs.
   real per-user semantics are wanted is a user-visible contract decision. Per the
   scope-ledger rule only the operator may approve treating server-wide as the
   disposition; an eval/impl agent may not.

### Concrete, testable scope handed to `m14-per-user-impl` (both branches)

**Branch A — operator accepts server-wide behavior as the M14 disposition**
(record the approval in the ledger). Residual impl work is **alignment/cleanup**,
fully testable, no schema change:
1. Delete `Configuration/userPrefsPage.html` and its
   `Jellyfin.Plugin.PhantomLibrary.csproj:73` `<EmbeddedResource>` line.
2. Remove the vestigial `IUserManager`/`PhantomDb` discards from
   `PhantomLibraryController` (drop the injections if now unused, or keep `_db`
   if still needed by other endpoints — verify usages first).
3. Fix the stale per-user-table claim in `PluginConfiguration.cs:11-15`.
4. Tests: a markup/resource test asserting `userPrefsPage.html` is no longer an
   embedded resource and `GetPages()` returns exactly the 3 live pages; `dotnet
   build -c Release` + `dotnet test` green. No rig needed (no user-visible channel
   change). No wipe.

**Branch B — operator wants real per-user semantics.** Implement per surface with
the "exact tests" listed above, obeying: no-runtime-migration (schema bump ⇒ wipe
+ CHANGELOG BREAKING), movie/TV parity (35/36 rig) for any channel-visible flow,
mandatory rig coverage for show/hide, and cleanup build env. This is a large,
multi-part change and should itself be split.

### Why not have the eval self-select a branch

Both branches change user-visible/operator-visible contract or ship a wipe; the
ledger reserves that approval to the operator. The eval's job is done: the finding
exists and `m14-per-user-impl` has an unambiguous next action (escalate with these
two scoped branches).

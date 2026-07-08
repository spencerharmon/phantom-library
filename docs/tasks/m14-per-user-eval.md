# REQ-M14-PER-USER — per-user evaluation against the channel-refactor code

Task: `m14-per-user-eval` (evaluation only; implementation is the dependent
`m14-per-user-impl`). This doc records the finding; it does **not** change behavior
and does **not** self-approve a scope deferral (per `AGENTS.md` §
"Planning / handoff scope ledger", only the operator may approve `DEFER`/`DROP`; an
eval agent may only mark "code does not implement this yet" and recommend
implementation or an operator disposition).

Requirement wording (from the task card): *"Per-user preferences / favourite
eviction-protection / show-hide / source-probing controls must be implemented or
re-evaluated with operator after the channel refactor,"* plus the explicitly
flagged **removed admin sub-page wiring (NOT an approved deferral yet)**.

All `file:line` citations below were read against the current channel-refactor tip
(`repo` at `d670220`).

## Bottom line

The channel-refactor plugin persists **zero per-user state**. Of the four named
surfaces:

- **Favourite eviction-protection** and **source-probing** exist and work, but are
  **server-wide / admin-only** — never keyed to a Jellyfin user.
- **Per-user show/hide** does not exist at all; every user sees an identical channel
  set gated only by global availability.
- The **per-user preferences store** (`user_prefs` table) and its REST endpoints
  were **removed in "Stage 2.2"**; only a dead, still-embedded admin HTML page
  (`userPrefsPage.html`) and vestigial controller injections remain.

Reviving true per-user semantics is net-new multi-user machinery, not a wiring fix.

## Deployment context (load-bearing for the disposition)

`AGENTS.md` § "Single-operator deployment": *"This project is deployed in a
single-operator environment. The operator owns the box and the Jellyfin process.
There are no external users …"* Under a single Jellyfin user, "per-user X" is
behaviorally identical to the existing server-wide toggle for **every** surface
below — the two only diverge once ≥2 users hold conflicting favourite / visibility
/ probe preferences, which this deployment does not have. This does **not**
authorize a deferral (operator-only), but it is the central fact the operator must
weigh.

---

## Surface 1 — Per-user preferences store

- **Behavior now:** none. No plugin setting is keyed to a Jellyfin user; every knob
  is server-wide plugin config.
- **Code entry points:**
  - `Configuration/PluginConfiguration.cs` — all fields are global XML config. The
    class remark (`PluginConfiguration.cs:11-15`) still claims per-user toggles
    "live in a separate per-user table inside `PhantomDb`" — this is **stale/false**;
    no such table exists post-Stage-2.2.
  - `Api/PhantomLibraryController.cs:20-24` (remark): *"The per-user-preferences
    endpoints were removed in Stage 2.2; the underlying `user_prefs` table is gone
    with the file-on-disk architecture."*
  - `State/PhantomDb.cs` — schema is keyed by `tmdb_id`/`type`/`season`/`episode`;
    a full-file search finds **no** `user_id` / `user_guid` / `user_prefs` column
    anywhere.
- **Still relevant post-refactor?** Only if the operator wants multi-user
  divergence. Single-user ⇒ inert.
- **Risk if implemented:** a per-user table is a **schema change**, which triggers
  the no-runtime-migration rule (`AGENTS.md`): bump `PhantomDb` schema version,
  wipe + rebuild, `CHANGELOG` "BREAKING: requires wipe". Not free.
- **Exact tests if implemented:** `PhantomDbTests` round-trip for the new table
  (upsert/read/delete keyed by user GUID; default-on-missing-row); assert the
  schema-version bump; a controller auth test (per-user writes require the acting
  user or elevation).

## Surface 2 — Favourite eviction-protection

- **Behavior now:** a materialised file is protected from idle eviction if **any**
  user has favourited it (or `ProtectFavourites=false` disables protection
  globally). No user can protect only their own view, nor evict something another
  user favourited.
- **Code entry points:**
  - `Materialisation/EvictionSweeper.cs` `RunOnceAsync`: header comment
    (`:25`) says it "checks per-user LastPlayedDate / IsFavorite **across all
    users**"; it reads `protectFavourites = cfg.ProtectFavourites` (`:199`), loads
    all users via `_userManager.GetUsers()` (`:208`), and per candidate row
    (`foreach … allRows`, `:216`) iterates every user (`foreach (var user in
    users)`, `:283`) aggregating into one global decision — `if (ud.IsFavorite)
    { isFav = true; }` (`:299-301`), then `if (protectFavourites && isFav) { skip }`
    (`:305`).
  - Config: `PluginConfiguration.cs:177` `ProtectFavourites` (global; default `true`
    at `:42`).
- **Still relevant post-refactor?** The protection itself works and is relevant.
  The **per-user** qualifier is inert single-user.
- **Risk if implemented:** eviction is a decision about a **shared** on-disk file —
  one file serves all users. "Per-user protection" is semantically ambiguous: one
  user's favourite must still physically pin the shared file for everyone, so
  per-user protection can only mean "stop pinning once the LAST favouriting user
  un-favourites" — which the current any-user aggregation **already** implements.
  There is little genuine per-user behavior to add here.
- **Exact tests if implemented:** extend `EvictionSweeperTests` with a multi-user
  case — {A fav, B not} ⇒ protected; {A un-fav, B not, both idle} ⇒ evicts — and
  assert the "last favouriting user" boundary. Current coverage
  (`EvictionSweeperTests.FavouriteProtected_NoEviction:201`) is single-user only
  (`IsFavorite = true` at `:214`); no cross-user case exists.

## Surface 3 — Show/hide (per-user visibility)

- **Behavior now:** every user sees the identical channel set. Visibility is gated
  purely by availability / materialised state, server-wide.
- **Code entry points:**
  - `State/PhantomDb.cs`: `ListVisibleMovieRowsAsync(ct)` (`:1652`),
    `ListVisibleSeriesRowsAsync(minAvailableEpisodes, ct)` (`:1681`),
    `IsSeriesVisibleAsync` (`:1864`), `IsEpisodeVisibleAsync` (`:1900`) — **none**
    take a userId.
  - `Channels/PhantomMoviesChannel.cs:86` and `Channels/PhantomShowsChannel.cs:122`:
    `IsEnabledFor(string userId) => true` — the userId argument is ignored.
    `GetLatestMedia` (`PhantomMoviesChannel.cs:254-257`) "carries UserId only" and
    does not filter by it.
- **Still relevant post-refactor?** Only for multi-user. Single-user ⇒ inert.
- **Risk if implemented:** **high Jellyfin-plumbing risk.** Jellyfin's `IChannel`
  browse contract passes `folderId`, and the channel-item cache is **not** keyed per
  user — injecting per-user filtering risks cache cross-contamination (one user's
  hidden set leaking into another's browse). Would likely need a per-user hidden-set
  table plus cache-key / `DataVersion` changes, and must preserve movie/TV parity
  (`AGENTS.md` § "Movie/TV parity"; rig scenarios 35/36).
- **Exact tests if implemented:** unit test on the visibility query with a userId +
  hidden-set, **plus mandatory rig** coverage (extend
  `tools/rig-scenarios/35-channel-e2e-playback.sh` /
  `36-channel-episode-e2e-playback.sh`, or a new scenario) proving user A's hidden
  item is absent for A but present for B in a live browse. Unit tests alone are
  insufficient for channel/cache behavior (`AGENTS.md` § Test).

## Surface 4 — Source-probing controls (per-user)

- **Behavior now:** source probing is background + admin-only; a user cannot
  trigger, scope, or gate probing by their own interactions.
- **Code entry points:**
  - `Sources/MagnetSelector.cs:133` `ProbeAsync(tmdbId, imdbId, type, season,
    episode, title, year, ct)` — no userId; ranks by global config
    (`cfg.QualityPreset`, `MinSeeders`, …). Indexer enablement is global
    (`Clients/ProwlarrClient.cs`, `Clients/TorrentioClient.cs`).
  - `Scheduled/AvailabilityProbeWorker.cs` — background loop gated on
    `cfg.AvailabilityProbeEnabled` (`:106`); claims work with a **machine-scoped**
    lease owner (`_owner = availability-<MachineName>-<Guid>`, `:34`;
    `ClaimDueAvailabilityAsync(_owner, …)`, `:169`), not a user.
  - `Api/PhantomLibraryController.cs` — `Items/{externalId}/Sources` +
    `.../RejectCurrent` + `.../MaterialiseCandidate` are
    `[Authorize(Policy = "RequiresElevation")]` (admin-only, `:27`), no userId.
  - The removed per-user slice was the `allowEager` "allow background source
    probing based on this user's interactions" toggle (see the dead
    `userPrefsPage.html` column).
- **Still relevant post-refactor?** Only if the operator wants probing driven
  per-user. Note `Materialisation/UserDataSavedListener.cs` already turns a user's
  favourite into a **global** materialise trigger — `HandleSavedUserData(item,
  userData, userId)` (`:81`) → on `userData.IsFavorite` (`:91`) →
  `TryTriggerFavouriteMaterialise(item)` (`:93`, note: `item` only, userId not
  threaded) → `MaterialiseAsync(…, MaterialiseTrigger.Favourite, …)` (`:144-149`).
  So "probe based on my interactions" is already achieved, globally.
- **Risk if implemented:** per-user probing multiplies probe/indexer load per user
  and has **no existing seam** — userId would have to be threaded from the
  interaction hooks through `MagnetSelector.ProbeAsync`, the availability
  lease/queue in `PhantomDb`, and possibly gostream. High blast radius on a
  background subsystem.
- **Exact tests if implemented:** unit tests threading userId into the probe
  trigger + a per-user enable gate; assert a disabled user's interaction does not
  enqueue a probe; queue/lease tests in `PhantomDbTests`.

---

## The removed admin sub-page (the ROI item flagged NOT an approved deferral)

Concrete artifacts of the removed wiring:

- **`Configuration/userPrefsPage.html`** (111 lines) — a per-user admin form: a
  table of users × three toggles `protectFavourites` / `showPhantoms` / `allowEager`
  (`:57-59`). It loads via `GET Plugins/PhantomLibrary/UserPrefs` (`:68`) and saves
  via `POST Plugins/PhantomLibrary/UserPrefs/{uid}` (`:94`).
- **Wiring state — orphaned:** the page is still declared
  `<EmbeddedResource Include="Configuration\userPrefsPage.html" />`
  (`Jellyfin.Plugin.PhantomLibrary.csproj:73`), so it is **compiled into the DLL**,
  but it is **NOT** returned by `Plugin.GetPages()` (`Plugin.cs:54-96` registers
  exactly `configPage.html`, `PhantomKebab`, `PhantomBadges`). The
  `PluginPageInfo` for `userPrefsPage.html` was removed by commit `92e5b84`
  ("Harden materialise and badge controls"). The page is therefore unreachable from
  the dashboard, and its two REST endpoints no longer exist
  (`PhantomLibraryController.cs:20-24`) — both AJAX calls would 404.
- **Vestigial backend:** `PhantomLibraryController` still constructor-injects
  `IUserManager _userManager` (`:36`) and `PhantomDb _db` (`:37`), used only as
  discards `_ = _userManager; _ = _db;` inside the `Status` endpoint (`:150-151`) —
  dead weight left behind by the removed `UserPrefs` endpoints.
- **Risk of leaving as-is:** a config page shipped in the DLL that POSTs to 404
  endpoints is a latent correctness / operator-confusion smell; it also keeps the
  false `PluginConfiguration.cs:11-15` "per-user table" doc alive. Low severity, but
  it is exactly the "documented partial" the operator must dispose of.

---

## Disposition recommendation

**This is a genuinely operator-only disposition decision; the eval does not (and
may not) self-select a branch.** `m14-per-user-impl` should **escalate** via
`beehive task human phantom-library m14-per-user-impl --reason "…"` presenting the
two branches below. Rationale (honest, not a self-approved deferral):

1. In the documented single-operator deployment, per-user granularity on all four
   surfaces is functionally inert — identical to the existing server-wide toggles
   until a second user with divergent preferences exists.
2. The "implement per-user" path is high-cost multi-user machinery: a new per-user
   table (schema bump ⇒ **wipe**), threading userId through the background eviction
   service, the channel visibility/cache path (Jellyfin-plumbing risk), and the
   probe subsystem — for zero behavioral change in the actual deployment.
3. Whether current server-wide behavior is the **accepted final contract** vs. real
   per-user semantics are wanted is a user-visible contract decision. Per the
   scope-ledger rule, only the operator may approve treating server-wide as the
   disposition.

### Concrete, testable scope handed to `m14-per-user-impl`

**Branch A — operator accepts server-wide behavior as the M14 disposition**
(record the approval in the ledger). Residual work is alignment/cleanup, fully
testable, **no schema change, no wipe**:

1. Delete `Configuration/userPrefsPage.html` and its
   `Jellyfin.Plugin.PhantomLibrary.csproj:73` `<EmbeddedResource>` line.
2. Remove the vestigial `IUserManager` / `PhantomDb` discards from
   `PhantomLibraryController` — drop the `IUserManager` injection if now unused;
   verify `_db` usages first and keep it only if another endpoint needs it.
3. Fix the stale per-user-table claim in `PluginConfiguration.cs:11-15`.
4. Tests: a markup/resource test asserting `userPrefsPage.html` is no longer an
   embedded resource and `GetPages()` returns exactly the three live pages;
   `dotnet build -c Release` + `dotnet test` green. No rig needed (no user-visible
   channel change).

**Branch B — operator wants real per-user semantics.** Implement per surface with
the "exact tests" listed above, obeying: no-runtime-migration (schema bump ⇒ wipe +
`CHANGELOG` BREAKING), movie/TV parity (35/36 rig) for any channel-visible flow,
mandatory rig coverage for show/hide, and the build/test conventions in `AGENTS.md`.
This is a large, multi-part change and should be split.

### Why not have the eval self-select a branch

Both branches change a user/operator-visible contract or ship a wipe; the
scope-ledger reserves that approval to the operator. The eval's job is complete:
the finding exists and `m14-per-user-impl` has an unambiguous next action —
escalate with these two scoped branches.

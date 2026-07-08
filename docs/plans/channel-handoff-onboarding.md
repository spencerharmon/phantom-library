# Channel architecture handoff — agent onboarding

> **⚠ Historical onboarding document — SUPERSEDED. The M14 IChannel
> migration described here has SHIPPED; it is NOT unstarted work.** This
> doc was written to onboard an agent BEFORE the migration was built, so
> its "state at handoff" / "start at Phase 0" framing no longer reflects
> reality — treat every present-tense claim below as historical. It is
> retained for design history only. For the authoritative record of what
> M14 implemented, read `docs/plans/m14-ledger-evaluation.md` (the
> ledger). The splash-as-playback design referenced here was superseded
> by native `RequiresOpening` playback (splash is legacy/support only per
> the ledger, REQ-M14-SPLASH). Canonical stub naming is `[tmdbid-<id>]`;
> `__phantom_tmdb<id>` is deprecated (AGENTS.md § "Canonical phantom stub
> naming scheme"). Do not treat this as a live work assignment.

**You are picking up the IChannel migration work for the phantom-library
plugin.** The architectural plan is `docs/plans/channel-handoff.md`
(2334 lines, committed at `9357c5c`). Before doing anything, read this
onboarding doc, then read AGENTS.md, then read the plan.

This document is the entry point. Read it in full before touching any
code, dispatching any subagent, or contacting the operator.

---

## State at handoff (historical — superseded; see banner)

- **[Historical] At the time this doc was written, no code in the plan
  had been implemented yet** and the reader was to start at Phase 0
  (pre-flight). The plan was execution-ready per the most recent critic
  verdict. This is no longer true: the M14 IChannel migration has since
  been built and SHIPPED — see `docs/plans/m14-ledger-evaluation.md` for
  the as-shipped disposition of each requirement.
- The plan went through **4 design iterations + 3 critic passes**.
  The critic verdict on v4 was "fix-and-ship"; the round-4 findings
  were patched and committed. No architectural blockers remain.
  Spec-completeness gaps that surface during implementation are
  yours to fix inline (see "What if you find the plan is wrong"
  below).
- The wipe script (`scripts/phantom-wipe.sh`) is committed and was
  sandbox-validated against the operator's actual production DB
  shape during earlier recovery work.
- The operator's production box currently runs the v0.2.0.0 plugin
  with the file-on-disk architecture. Phase 7 wipes that state. Do
  not deploy any Phase 1-6 work to the operator's box until Phase 7
  is ready and the operator signs off.

## Required reading, in order

1. **`SYSTEM.md`** — the no-shortcuts rule and "stopping is also a
   shortcut" criteria. This governs everything you do.
2. **`AGENTS.md`** — project conventions. Four hard-rule sections at
   the top:
   - "No database migrations until v1.0"
   - "Canonical phantom stub naming scheme" (becomes obsolete when
     this plan ships, but governs how you interpret historical refs
     during implementation)
   - "Production database safety"
   - "Single-operator deployment"
3. **`docs/plans/channel-handoff.md`** — the plan you're executing.
   Read end-to-end at least once before starting. Re-read the phase
   you're currently working on each time you commit a stage.
4. **`docs/agents/testing.md`** — how the rig works. You will be
   running it heavily.

Optional context (helpful but not blocking):

5. `docs/plans/channel-architecture.md` — superseded v1/v2 of the
   plan. Explains the design evolution and why some shapes were
   rejected. Don't implement from this; the current plan is
   `channel-handoff.md`.
6. `docs/plans/scanner-race-reactor.md` — superseded alternative
   architecture (file-on-disk + reactor pattern). Explains why
   IChannel won.
7. `docs/plans/M12-*.md` — historical investigations with deprecation
   banners. Read only if you're debugging something related.

## Operator-accepted regressions — do NOT re-litigate

The operator has already weighed in on each of these. Do not ask them
again.

- Loss of `CollectionType.movies` Home rows ("Latest Movies",
  "Continue Watching Movies") for gostream content. Replaced by
  channel-specific "Latest in Phantom Movies" rows.
- Loss of Dashboard → Libraries management for the gostream/phantom
  surfaces. Centralised in Dashboard → Plugins → Phantom Library →
  Settings.
- UserData on existing gostream-bound BaseItems is wiped **one-time
  at wipe**. The single-external-id scheme preserves UserData across
  the phantom → materialised transition thereafter.
- Per-user channel access via `User.Policy.EnabledChannels` instead
  of `EnabledFolders`.
- Channel-flavored library icon vs movies/tvshows icon.
- Pre-existing gostream files (~131) appear with raw filename names
  until the operator enables `EnrichOrphanGostreamItemsViaTmdbSearch`
  in plugin config.
- Channel display names are **hardcoded** ("Phantom Movies",
  "Phantom Shows"). No operator setting exposes them; renaming
  would invalidate `BaseItem.Id` derivation and wipe UserData.
- Eager pre-resolution (`EagerResolver`) is dropped with no
  replacement. First-play latency for never-seen items increases by
  the gostream materialise duration.

## Operator-rejected designs — do NOT propose

These were explicitly considered and rejected. If you find yourself
sketching them out, stop.

- Reverting to the file-on-disk reactor architecture (see
  `scanner-race-reactor.md`).
- Pure IChannel with phantoms-only-no-real-files (operator: "What
  about gostream files that are not phantoms? Those would not be
  visible in the channel").
- JS-shim-only architecture (operator requires mobile/TV support).
- Upstreaming the Jellyfin patch in this PR (Phase 8 is operator-
  driven and deferred per Jellyfin's LLM/AI contribution policy).
- Mutating `IChannelManager` directly (must stay additive via sibling
  interface `IChannelItemRefreshManager`).
- Channel rename as a settings field.
- In-plugin migration `IHostedService` of any kind (forbidden by
  AGENTS.md "Single-operator deployment" section).

## What you can decide on your own

- Implementation details that don't affect the spec: variable
  naming, internal helper organisation, log message wording, async
  vs sync where both compile, choice of `record` vs `sealed class`
  for internal types.
- Within Stage 4.2.0: which of Option A (extract `MagnetSelector`
  class) or Option B (keep inline in `BuildGostreamRequest`) to use.
  Plan recommends A; either is operator-accepted.
- Test organisation: file naming, mock library choice (existing
  project uses xunit + Moq), fixture sharing.
- Commit boundaries within a phase. Aim for one commit per logical
  step within a stage, but you can squash trivially-related work.
- Whether to dispatch the `critic` agent for review at any
  intermediate point (operator's standing instruction is "critic
  whenever you're uncertain").
- How to spell error messages, log structured-args, configurable
  defaults.

## What requires operator input

- Any deviation from the plan that touches:
  - The Jellyfin patch shape (must remain additive sibling interface)
  - The schema shape (additions require a new schema-version bump +
    wipe per AGENTS.md "No database migrations until v1.0")
  - The wipe procedure
  - The UX (e.g. if you discover Home rows DO work for channels
    somehow, ask before changing course)
- Any concrete blocker per SYSTEM.md's "Stopping is also a shortcut"
  criteria.
- Any time the critic flags a finding that requires picking between
  architecturally different fixes.
- Anything that would require modifying files outside the plan's
  spec (e.g. `gostream/` integration changes, new REST endpoints
  not in the plan, dependencies added to `csproj`).
- Anything affecting the operator's production box directly
  (deployment is Phase 7; even then operator drives the actual
  install).

## Communication contract

- **One commit per Phase X.Y stage**, with the per-stage acceptance
  criterion met before commit. The acceptance gate (defined in the
  plan) MUST pass.
- **Per-phase summary report back to operator**: what landed, test
  counts, rig-validation results, any deviations from the plan
  (with rationale), what's next.
- If you hit a real blocker (per SYSTEM.md): stop and report. Don't
  ship a workaround.
- If you find a plan error during implementation (typo, missed
  dependency, etc.): fix it inline AND note the fix in your
  per-phase report.
- Be terse. Match the operator's caveman comms style. They've sat
  through many critic rounds; they don't need pleasantries.

## First action

Run Phase 0 (pre-flight checks). See plan §"Pre-flight (Phase 0)".

- 0.1 Source clone is current
- 0.2 Plugin builds clean against unpatched Jellyfin
- 0.3 Rig is functional
- 0.4 Operator-DB snapshot for sandbox testing
- 0.5 Gostream service health
- 0.6 Verify no plugin-side or web-shim references to legacy phantom-stub paths in unrelated code

Report counts back to the operator. Do not begin Phase 1 work until
pre-flight is complete and reported, and the operator has signed off.

## Phase progression

| Phase | What | Approx scope |
|---|---|---|
| 0 | Pre-flight | 30 min, read-only |
| 1 | Jellyfin patch (additive sibling interface + tests) | 1 day |
| 2 | Plugin foundation (deletions, schema v7, ChannelItemId, channel skeletons, splash init) | 1.5 days |
| 3 | Movies channel + discovery refresh + gostream enumerator | 1.5 days |
| 4 | Materialise flow (tuple Materialiser, badge controller, MagnetSelector if Option A) | 2 days |
| 5 | Shows channel + autopilot rewrite | 1.5 days |
| 6 | Eviction + lifecycle hygiene | 0.5 day |
| 7 | Wipe + install + operator deployment | 1 day |
| 8 | Upstream PR (DEFERRED, operator-driven) | n/a for you |

Total estimate: ~9 days of focused work, assuming no architectural
surprises. If you find architectural surprises, stop and report —
don't burn 3 days on a workaround.

## Critic involvement

Per AGENTS.md and the four critic rounds the plan went through:
dispatch the `critic` agent for review at any phase acceptance gate
where you're uncertain, or when the operator requests it.

The critic has read the plan three times; their pattern is to verify
against Jellyfin source. Reference their prior findings (in the
plan's header changelog at the top) to avoid re-doing closed work.

Specifically: critic round 2's findings about IChannel internals
(`ChannelManager.cs:944-1180`, the 5-minute cache, the forceUpdate
gate, the probe-pin guard, the channel-name-in-id-derivation
constraint) are the load-bearing knowledge for the Jellyfin patch.
If the critic flags something that disagrees with these, re-read
those lines of `ChannelManager.cs` directly before acting.

## Tools and rig

- **Test rig:** `/tmp/jf-test/` per `docs/agents/testing.md`. Existing
  rig harness is functional.
- **Operator-DB snapshot:** create in Phase 0 stage 0.4. After
  creation, lives at `/tmp/operator-snapshot/`. Used for
  sandbox-validating Phase 7 wipe + reseed.
- **Jellyfin source clone:** `jellyfin/` subdir. The patch is
  applied against this clone via `install.sh`. Do NOT modify files
  in `jellyfin/` directly without exporting the diff as a `.patch`
  file in `scripts/jellyfin-patches/` per Phase 1.3.
- **Plugin tests:** `dotnet test` in repo root.
- **Build:** `dotnet build -c Release` in repo root for plugin;
  `cd jellyfin && dotnet build` for patched Jellyfin (after applying
  patches).

## Boundaries (DO NOT)

- Do NOT upstream the Jellyfin patch (Phase 8 is deferred and
  operator-driven per the LLM/AI policy at
  jellyfin.org/docs/general/contributing/llm-policies/).
- Do NOT add migration tooling beyond what's specified (AGENTS.md
  "No database migrations until v1.0").
- Do NOT mutate `IChannelManager` (must stay additive; see plan
  §"Patch design correctness").
- Do NOT introduce `SourceType.Channel` branches in `DtoService.cs`
  beyond what already exists in upstream.
- Do NOT write untested SQL/scripts and hand them to the operator
  (AGENTS.md "Production database safety").
- Do NOT use `install.sh` as a packaging tool (operator confirmed
  it's dev-machine convenience only).
- Do NOT bump the plugin version mid-phase. Version bump goes in
  Phase 7 as part of operator deployment prep.
- Do NOT touch files outside the plan's scope without operator
  approval. Specifically: `gostream/` patches are out of scope; the
  plan only depends on `IGostreamClient`'s existing surface.

## How to know you're done with a phase

Each phase has an "Acceptance gates per phase" table entry in the
plan §"Acceptance gates per phase". Match against it. If green:
commit + report + ask the operator to confirm before starting the
next phase. **Do NOT blast through phases without operator gating.**

The operator pattern through this conversation has been: read the
report, sometimes ask follow-up questions, sometimes critic, then
say "proceed" or pivot. Wait for that signal at each gate.

## What if you find the plan is wrong

1. **Typo or trivial spec gap:** fix inline + note in your per-phase
   report.
2. **Design issue that affects more than one stage:** STOP, dispatch
   the critic for a focused review of the specific design question,
   report back with the critic's findings + your recommendation.
3. **You're unsure which:** dispatch the critic. The plan went
   through 4 rounds for a reason; design issues caught late are
   expensive.

## Git state at handoff

- Branch: `main`
- Last commit (plan): `9357c5c` "docs(plans): channel-handoff v3+
  addressing critic round 3 + round 4 findings"
- Worktrees: several stale implementer worktrees exist in
  `.cave/worktrees/`. Ignore unless instructed; they're from earlier
  failed migration work. Do not pull commits from them.
- Important previous commits to know about (no need to revert; for
  context only):
  - `787066a` "docs(agents): hard rule — no database migrations until
    v1.0" — added an AGENTS.md hard rule that this plan complies with
  - `3c55d30` "docs: deprecate legacy __phantom_tmdb<id> naming
    scheme" — adds the deprecation banners on M10/M11/M13
  - `a931379` "merge spike: Jellyfin-native [tmdbid-N] stub layout" —
    the v0.2.0.0 spike that introduced the current production state;
    Phase 7 wipes the on-disk artifacts this commit deployed
  - `2a2f1c5` "feat(ui): phantomBadges shim + supporting plugin
    changes" — committed the kebab + badges JS shims this plan
    extends

## Files that will be DELETED by the plan

For your awareness; deletions happen in Phase 2.1. Do not
pre-emptively work on these files in Phase 0/1:

- `Library/PhantomStubManager.cs`
- `Library/PhantomCollectionFolderBinder.cs`
- `Library/PhantomPathUtilities.cs`
- `Library/SeriesIngestor.cs`
- `Library/SuggestionsContributor.cs`
- `Library/VirtualItemFactory.cs`
- `Library/VirtualLibraryRoot.cs`
- `PhantomBootstrapService.cs`
- `Providers/PhantomImageProvider.cs`
- `Playback/PhantomMediaSourceProvider.cs`
- `Playback/PhantomStatusDecorator.cs`
- `Materialisation/EagerHintSink.cs`
- `Materialisation/EagerResolver.cs`
- `Scheduled/SuggestionsRefreshTask.cs`
- All tests for the above

## Files that survive the plan

For your awareness; these are kept:

- `Library/CachedTmdbReader.cs` (used by channel item synthesis)
- `Playback/SplashStream.cs` (channels emit splash MediaSource)
- `Playback/SplashStreamMetadata.cs`
- `Materialisation/UserDataSavedListener.cs` (refactor for channel item ids)
- `Materialisation/PlaybackTriggerListener.cs` (refactor for channel item ids + splash guard)
- `Materialisation/MaterialisationQueue.cs`
- `Materialisation/Materialiser.cs` (heavy refactor; tuple signature added)
- `Materialisation/QualityScorer.cs` (used by `MagnetSelector` Option A)
- `Materialisation/EvictionSweeper.cs` (rewritten per plan §6.1)
- `Materialisation/SeriesAutopilot.cs` (rewritten per plan §5.2)
- `State/PhantomDb.cs` (schema v7 rewrite)
- `Api/PhantomLibraryController.cs` (trimmed)
- `Api/PhantomLibraryBadgesController.cs` (state lookup rewritten)
- `Api/SourcePickerController.cs` (KEPT; independent feature)
- `Sources/SourcePickerService.cs` (KEPT; independent feature)
- `Clients/IGostreamClient.cs` + `GostreamClient.cs` (unchanged)
- `Clients/ITmdbClient.cs` + `TmdbClient.cs` (unchanged)
- `Clients/IIndexerClient.cs`, `ProwlarrClient.cs`, `TorrentioClient.cs` (unchanged)
- `Configuration/phantomKebab.js` (minor edit for channel item id format)
- `Configuration/phantomBadges.js` (minor edit for state value mapping)
- `Configuration/PluginConfiguration.cs` (extended)
- `Configuration/configPage.html` (rewritten)
- `PluginServiceRegistrator.cs` (register channels + new services; deregister deletions)

## Closing

The architecture is sound, the patch is small and additive, the
operator has signed off on all UX tradeoffs. Your job is to execute
the plan stage by stage, validate at each gate, and report cleanly.
If you find anything that surprises you, stop and ask. Don't ship
workarounds.

Operator's communication preference: terse, technical, fragments
fine, no pleasantries. They will appreciate brevity over completeness
in status reports.

Good luck.

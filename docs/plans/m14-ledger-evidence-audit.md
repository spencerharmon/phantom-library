# M14 ledger evidence audit — already-IMPLEMENT rows

Date: 2026-07-08

This document cites the concrete acceptance evidence for the three M14 ledger rows already marked
`IMPLEMENT` with implementation believed complete (`REQ-M14-SOURCE-API`, `REQ-M14-SOURCE-UI`,
`REQ-M14-FAV-MATERIALISE`), per the ledger's own rule that "any PLAN text that conflicts with this
ledger is stale and must be fixed before handoff." No behavior change; this is a read-only audit plus
prose corrections (see "Stale-prose fix" below).

All code/tests below were implemented in a single commit, `0d5caa2` ("WIP M14 source management ledger
work", 2026-06-22), and are unmodified since (confirmed via `git log --oneline -- <path>` at audit
time, HEAD `14bf5c4`). `dotnet build`/`dotnet test` could not be executed in this environment: both the
plugin and test `.csproj` reference a sibling patched Jellyfin source checkout at `../../jellyfin/*`
(see `Jellyfin.Plugin.PhantomLibrary.csproj:41-56`, and `AGENTS.md` § "Jellyfin patch dependency"), which
is not present here — `dotnet build` fails with CS0246 "type not found" for Jellyfin SDK types only,
not for any type introduced by the audited change. Verdicts below rest on direct code/test reading, not
an executed run.

## REQ-M14-SOURCE-API

> Acceptance evidence required: API tests + `file:line` citations for `GET .../Sources`,
> `POST .../RejectCurrent`, `POST .../MaterialiseCandidate`.

**Routes** — `src/Jellyfin.Plugin.PhantomLibrary/Api/PhantomLibraryController.cs`:
- `GET Items/{externalId}/Sources` — attribute `:110`, handler `:113-117`.
- `POST Items/{externalId}/Sources/RejectCurrent` — attribute `:119`, handler `:125-129`.
- `POST Items/{externalId}/Sources/MaterialiseCandidate` — attribute `:131`, handler `:136-143`.
- Status-code mapping — `ToActionResult`, `:171-179`.

**Business logic** — `src/Jellyfin.Plugin.PhantomLibrary/Sources/PhantomSourceManager.cs`:
- `GetSourcesAsync` `:177-230`; `RejectCurrentAsync` `:232-290`; `MaterialiseCandidateAsync` `:292-364`.
- Non-Phantom/unparseable-id gate (shared by all 3 routes): `TryResolveKey` `:555-593` (`default: return
  false` at `:586-588` rejects series/season/anything not movie|episode).
- Shared-gostream-hash safety guard: `DeleteCurrentStateAndMaybeRemoveAsync` `:388-402` (counts other
  `materialised_state` references before calling `_gostream.RemoveAsync`).

**Tests** — `tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomLibrarySourceControllerTests.cs` (same
commit as the implementation):
| Scenario | Test |
|---|---|
| Unparseable/non-Phantom externalId → 404 | `Sources_BadExternalId_Returns404` `:198-206` |
| No current source → 409 | `RejectCurrent_NoCurrent_Returns409` `:209-220` |
| Already in flight → 409, gostream never called | `RejectCurrent_InFlight_Returns409WithoutRemoving` `:223-237` |
| No alternate candidate → 422, current deleted + rejected + gostream removed (sole reference) | `RejectCurrent_NoAlternate_RejectsDeletesAndRemovesCurrent` `:240-261` |
| Selected-candidate success (exact magnet) | `MaterialiseCandidate_SelectedSuccess_UsesExactMagnet` `:264-305` |
| Selected candidate absent from a stale/empty ranked probe but full metadata supplied in the request | `MaterialiseCandidate_RequestMetadataBypassesStaleRankedList` `:308-348` |
| Shared stub/hash across two items → reject does NOT call gostream remove, other item's row survives | `RejectCurrent_SharedSource_DoesNotRemoveGostreamStub` `:351-381` |

**Live rig evidence (reject → next ranked candidate → real playback)** —
`tools/rig-scenarios/35-channel-e2e-playback.sh:355-397` (step `[8]`): seeds a second ranked candidate
into `availability_items`, asserts it is exposed pre-reject via `GET .../Sources` (`:371-378`), calls
`POST .../RejectCurrent` (`:380`), asserts the response actually materialised (`Code=='materialised'`,
`:385-388`), asserts the on-disk `stub_path` changed to a genuinely different candidate (`:390-392`),
asserts the old magnet is recorded `operator_rejected` (`:393-394`), and re-asserts real playback through
the new FUSE path (`:395-397`). This satisfies both the "Live rig scenario proving reject → next ranked
source → playback" implementation-contract requirement and `REQ-M14-SOURCE-SAFETY`'s rig-proof bullet
(tracked separately by `m14-source-safety-rig`; cited here only as corroborating evidence).

**Verdict: evidence exists, current, cited above — row stands as `IMPLEMENT`/done.**

Minor, non-blocking completeness notes for future maintainers (not "missing acceptance evidence" — the
ledger's required evidence type exists for all three routes and every named scenario, incl. safety):
- `RejectCurrent`/`MaterialiseCandidate` don't have their own dedicated bad-externalId unit test (only
  `Sources` does); all three share `TryResolveKey`, so risk is low.
- No dedicated happy-path test asserting the full `GetSourcesAsync` response shape (candidates array +
  `isCurrent`/`isRejected` flags + `canRejectCurrent`/`canMaterialiseSelected`) in one assertion — the
  underlying logic is exercised piecemeal through the `RejectCurrent`/`MaterialiseCandidate` tests.
- No dedicated test for `MaterialiseCandidate` targeting an already-rejected candidate without
  `overrideRejected` (the `CandidateNotFound` branch at `PhantomSourceManager.cs:335-338`).

## REQ-M14-SOURCE-UI

> Acceptance evidence required: UI/JS tests or DOM evidence showing controls for Phantom items and
> absence/disabled state for non-Phantom items.

**Implementation** — `src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomKebab.js` (served at
`PhantomLibraryController.cs:62-77`):
- Phantom movie/episode gate: `parsePhantomExternalId` `:73-82`, `getPlayablePhantomItem` `:84-92`
  (also rejects non-`Movie`/`Episode` `item.Type` at `:89`).
- API calls: `fetchSources` `:151-164`; `fireMaterialiseCandidate` `:214-234`; `fireRejectCurrent`
  `:236-255`.
- Details-panel "Phantom Source" section: `renderSourceSection` `:356-453` — heading/aria-label
  `:368-376`, candidate `<select>` `:383-410`, "Materialise selected source" button `:415-429`,
  "Reject current source" button disabled unless `canRejectState(state)` `:435`.
- **Absence for non-Phantom items**: `refreshSourceSection` `:468-484` removes the section entirely
  (`removeSourceSection`, `:455-458`) whenever `getPlayablePhantomItem()` resolves `null`.
- Kebab action-sheet gating (Reject/Materialise entries): `injectIntoSheet` `:501-539`.
- Mobile/touch sizing: `ensureStyles` `:279-295` (`min-height:44px`, `@media (max-width: 600px)`,
  `touch-action:manipulation`).

**Tests** — `tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomKebabScriptTests.cs`:
| Scenario | Test |
|---|---|
| Fetch keys off stable `ExternalId`, not Jellyfin GUID | `SourceControls_FetchByStableExternalId_NotJellyfinGuid` `:8-18` |
| Details-panel section + candidate dropdown + both buttons + mobile touch sizing present | `SourceControls_RenderDetailsSectionAndTouchSizedControls` `:21-33` |
| Gating regexes target movie/episode only; `KindSeries`/`KindSeason` absent from source-control gating | `SourceControls_GateToPhantomMoviesAndEpisodesOnly` `:36-45` |
| Action-sheet shows Reject only when materialised+rejectable, Materialise only when materialisable | `ActionSheet_ShowsRejectForMaterialisedAndMaterialiseForUnmaterialised` `:61-71` |

**Verdict: evidence exists, current, cited above — row stands as `IMPLEMENT`/done.**

Caveat worth recording (not a blocking gap): these are static string-presence assertions against the
raw JS source text (`File.ReadAllText` + `Assert.Contains`/`Assert.DoesNotContain`), not executed-DOM or
headless-browser tests — the test project has no jsdom/Playwright/Selenium dependency, and no rig
scenario drives the web UI (`tools/rig-scenarios/*.sh` are all REST/SQLite, no browser automation). This
is the *same* rigor level this codebase already accepts for its only other embedded-web-asset test class
(`ConfigPageMarkupTests.cs`, cited for `REQ-M14-RETENTION` in `docs/plans/m14-ledger-evaluation.md`), so
it is not a novel weak spot introduced for this row — but it means "controls are absent for non-Phantom
items" is proven by source-level gating logic + regression-locked strings, not by an actually-rendered
DOM. Mobile-specific live verification is separately in scope for `m14-mobile-source-mgmt`
(`REQ-M14-MOBILE`), not this row.

## REQ-M14-FAV-MATERIALISE

> Acceptance evidence required: UserData/favourite tests showing materialise/prewarm trigger, or
> operator-approved disposition change.

**Implementation** — `src/Jellyfin.Plugin.PhantomLibrary/Materialisation/UserDataSavedListener.cs`:
- Dispatch on favourite: `HandleSavedUserData:91-94` (`if (userData.IsFavorite) {
  TryTriggerFavouriteMaterialise(item); }`).
- Trigger body: `TryTriggerFavouriteMaterialise:134-162` — movie case `:143-151`, episode case
  `:152-160`, both call `_materialiser.MaterialiseAsync(..., MaterialiseTrigger.Favourite, ...)`;
  series/season fall through the switch with no case (containers are never materialised).
- `MaterialiseTrigger.Favourite` enum member: `IMaterialiser.cs:8-15`.

**Tests** — `tests/Jellyfin.Plugin.PhantomLibrary.Tests/UserDataSavedListenerTests.cs`:
| Scenario | Test |
|---|---|
| Favourited movie → `MaterialiseAsync(42, "movie", null, null, Favourite, …)` exactly once | `FavouriteMovie_TriggersMaterialiseByExternalId` `:19-39` |
| Favourited episode → `MaterialiseAsync(200, "episode", 1, 2, Favourite, …)` exactly once | `FavouriteEpisode_TriggersMaterialiseByExternalId` `:42-62` |
| Favourited series (container) → `MaterialiseAsync` never called | `FavouriteSeries_DoesNotMaterialiseContainer` `:65-85` |

All three exercise the real `HandleSavedUserData` production method (not a re-implemented shim) via a
mocked `IMaterialiser`.

**Corroborating docs**: `docs/plans/m14-ledger-evaluation.md:11` ("Favourite-driven materialisation
implemented…") and `CHANGELOG.md` Unreleased/Changed ("Favourite saves on Phantom movie/episode channel
items now trigger materialisation/prewarm using the existing materialiser pipeline.") both already
reflect this as shipped.

**Verdict: evidence exists, current, cited above — row stands as `IMPLEMENT`/done.**

### Stale-prose fix (done as part of this audit)

`git blame` showed `PLAN.md` carried three sentences from commit `92e5b841` (2026-06-21) — one day
*before* both the ledger (`de433e4`, 2026-06-22 11:55) and the favourite-materialise fix itself
(`0d5caa2`, 2026-06-22 19:12) — still describing favourite-triggered materialisation as deferred/not
wired, directly contradicting this row's own evidence:
- "Movie sequel autopilot and favourite-triggered prewarming are deferred." (§ Resolved Design
  Decisions, item 3)
- "Favourite-to-materialise is not wired in the current M14 slice." (§ Item lifecycle, "User actions")
- "Movie sequel autopilot and favourite-triggered materialisation are deferred." (§ Item lifecycle,
  "Series autopilot")

Per the ledger's own rule ("Any PLAN text that conflicts with this ledger is stale and must be fixed
before handoff"), corrected in this change to state favourite-triggered materialisation is implemented
while keeping the still-accurate "movie sequel autopilot is deferred" clause intact.

## Summary

| Row | Verdict |
|---|---|
| REQ-M14-SOURCE-API | Evidence current and cited; row stands as done. Minor completeness notes recorded above for future maintainers, not blocking. |
| REQ-M14-SOURCE-UI | Evidence current and cited; row stands as done. String-match test caveat recorded above (consistent with this codebase's existing embedded-web-asset test convention). |
| REQ-M14-FAV-MATERIALISE | Evidence current and cited; row stands as done. Stale contradicting PLAN.md prose fixed as part of this audit. |

No follow-up task filed: no row's required acceptance evidence was found missing or stale — the code,
tests, and (for SOURCE-API) live rig evidence are current, coherent, and satisfy each row's own stated
evidence bar. `m14-ledger-done` may treat these three rows as closed on this basis.

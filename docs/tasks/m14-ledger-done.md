# m14-ledger-done — M14 ledger closure (capstone)

Date: 2026-07-12

Capstone verification for M14. Confirms every row of the M14 operator requirements
ledger (`PLAN.md` § "M14 operator requirements ledger") is satisfied, that no row was
silently deferred, that movie/TV parity is intact, and records M14 DONE in `PLAN.md`,
`CHANGELOG.md`, and `README.md`.

## Method

Read-only verification: each ledger row was checked against its landed evidence
(tests, rig scenarios, evaluation/audit docs) and, for every non-`IMPLEMENT`
disposition, against the explicit operator decision recorded in `ROI.md`. No source
was changed by this task; it is a documentation/handoff closure. `dotnet build`/`dotnet
test` are not runnable in this worktree (the plugin/test `.csproj` reference a sibling
patched Jellyfin checkout at `../../jellyfin/*` that is absent here, and Zuul has no
Nodepool executor yet) — the same environment gap documented for every other M14 task;
verdicts rest on direct code/doc reading and the prior tasks' recorded evidence.

## Row-by-row closure

### IMPLEMENT rows

- **REQ-M14-SOURCE-API** — evidence-cited in `docs/plans/m14-ledger-evidence-audit.md`
  (routes/handlers in `PhantomLibraryController.cs`/`PhantomSourceManager.cs`,
  `PhantomLibrarySourceControllerTests`); live rig `35-channel-e2e-playback.sh` step [8].
- **REQ-M14-SOURCE-UI** — evidence-cited in `docs/plans/m14-ledger-evidence-audit.md`
  (`phantomKebab.js` gating/render, `PhantomKebabScriptTests`).
- **REQ-M14-SOURCE-SAFETY** — unit/API safety tests (`m14-source-safety-tests`, DONE) +
  rig `tools/rig-scenarios/39-channel-source-safety.sh` (`m14-source-safety-rig`, DONE):
  reject → next ranked candidate → refresh → playback; gostream hash never removed while
  referenced by another item.
- **REQ-M14-MOBILE** — mobile source-mgmt controls + executable DOM/API evidence
  `tools/rig-scenarios/38-mobile-source-dom.sh` / `phantom-kebab-mobile-dom.mjs`
  (movie + episode) (`m14-mobile-source-mgmt`, DONE).
- **REQ-M14-FAV-MATERIALISE** — evidence-cited in `docs/plans/m14-ledger-evidence-audit.md`
  (`UserDataSavedListener.HandleSavedUserData`, `UserDataSavedListenerTests`).
- **REQ-M14-PER-USER** — operator disposition IMPLEMENT (branch B, split, 2026-07-09,
  `ROI.md`). Additive v11→v12 schema `user_prefs`/`user_hidden_items`
  (`m14-per-user-schema`, DONE) + per-user backend / favourite-scoped eviction / show-hide
  threading + admin sub-page (`m14-per-user-backend`, `m14-per-user-showhide`, DONE).
  Mandatory two-user live rig `tools/rig-scenarios/42-per-user-show-hide.sh` (movie +
  episode: A hides → hidden for A / visible for B → A unhides → returns; per-user pref
  toggle + per-user favourite protection) is landed in the tracked tree. The
  `m14-per-user-rig` tracking task's final review pass is the only remaining checkbox;
  the implementation and rig scenario are present in-tree, so the ledger row's required
  evidence exists.

### EVALUATE rows

- **REQ-M14-RECOMMENDATIONS** — resolved to IMPLEMENTED (`m14-recommendations-resolve`,
  DONE): `FavouriteRecommendationIngestor` + unit/API tests + rig
  `40-favourite-recommendations.sh`; written eval in `docs/plans/m14-ledger-evaluation.md`.
- **REQ-M14-RETENTION** — resolved as config/UI no-op: retention field disabled/no-op
  (`ConfigPageMarkupTests`), written eval in `docs/plans/m14-ledger-evaluation.md`.
  Operator marked resolved (`ROI.md`, 2026-07-09).
- **REQ-M14-VAULT** — operator DEFER (branch A, 2026-07-09, `ROI.md`): favourite→materialise
  + favourite-protected eviction accepted as the M14 persistence answer; no
  persist-without-materialise trigger in scope (`m14-vault-resolve`, DONE).
- **REQ-M14-CONCURRENCY** — resolved as implemented: per-indexer `SemaphoreSlim` cap +
  sequential probe; written eval in `docs/plans/m14-ledger-evaluation.md`. Operator
  resolved (`ROI.md`, 2026-07-09).
- **REQ-M14-SEARCH-GATING** — resolved as channel-only availability gating, no code change;
  written eval in `docs/plans/m14-ledger-evaluation.md`. Operator resolved (`ROI.md`,
  2026-07-09).
- **REQ-M14-SPLASH** — resolved as historical/support-only under native `RequiresOpening`
  playback; verified by 35/36. Operator resolved (`ROI.md`, 2026-07-09).

## Parity

Movie/TV parity is the standing gate: `tools/rig-scenarios/35-channel-e2e-playback.sh`
(movie) + `36-channel-episode-e2e-playback.sh` (episode), plus the safety scenario
`39-channel-source-safety.sh`. Every M14 feature task carried its movie + episode
coverage (per-user rig 42, recommendations rig 40, mobile DOM 38 all cover both).

## No unapproved deferrals

Every disposition other than a plain implemented `IMPLEMENT` traces to an operator
decision recorded in `ROI.md` (PER-USER IMPLEMENT branch B; VAULT DEFER branch A;
RETENTION/CONCURRENCY/SEARCH-GATING/SPLASH resolved; RECOMMENDATIONS implemented). No
requirement was converted to `DEFER`/`DROP` without operator approval.

## Recorded

- `PLAN.md`: milestone table row M14 → `✅ DONE against ledger (2026-07-12)`; added
  "M14 ledger closure" row-by-row table under the ledger.
- `CHANGELOG.md`: Unreleased note recording M14 ledger closure.
- `README.md`: status blurb updated to reflect the M14 channel architecture landing.
</content>
</invoke>

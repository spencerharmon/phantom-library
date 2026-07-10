# REQ-M14-PER-USER — per-user live rig (m14-per-user-rig)

Task: `m14-per-user-rig`. Scope: the **mandatory user-visible live rig** for the
per-user surface (REQ-M14-PER-USER, 4/4). Depends on `m14-per-user-showhide`
(the channel-browse wiring + `IHasCacheKey` cache-key fix landed at `c6bdac4`).

Read `docs/tasks/m14-per-user-eval.md` and `docs/tasks/m14-per-user-schema.md`
first for the surface taxonomy this rig exercises.

## What this delivers

`tools/rig-scenarios/42-per-user-show-hide.sh` — a self-contained, operator-run
live scenario that drives the real plugin over HTTP against the patched rig
Jellyfin on `:18096` (never prod), with **two distinct non-admin users A and
B**, and a `trap`-based cleanup that removes the two rig-only users (and their
`user_prefs` / `user_hidden_items` rows) on any exit.

It follows the same shape as the other live scenarios (30, 40): preflight →
drive endpoints/tasks → assert HTTP responses + `phantom.db` state, `set -u`,
per-scenario log under `/tmp/jf-rig/logs/`, `FAIL:`/`PASS:` markers.

## Why two real users (not a param)

The per-user surface resolves the acting user from the `Jellyfin-UserId` claim
Jellyfin stamps on an authenticated request — never a route/body parameter (see
`PhantomLibraryUserController`). The admin rig API key carries **no** such claim,
so `User/Prefs`/`User/Hidden` answer 401 for it. The scenario therefore:

1. creates users `phantom-rig-a` / `phantom-rig-b` via `POST /Users/New` +
   `POST /Users/{id}/Password` (idempotent — reuses them if present),
2. `POST /Users/AuthenticateByName` to obtain each user's **own** access token,
3. issues every hide/unhide/prefs call, and every channel browse, under the
   respective user token — so isolation is exercised through the real claim
   resolution and the real per-user channel-item cache key
   (`IHasCacheKey.GetCacheKey(userId)`).

## What is asserted (movie AND series/episode — Movie/TV parity)

**Surface 3 — per-user show/hide, user-visible:**
- baseline: phantom movie `99000001` and phantom series `99100001` (tmdb-mock
  discover fixtures, warmed by `DiscoveryRefreshTask`) are visible to BOTH A and
  B in their channel browse.
- A hides the title → it vanishes from A's OWN `/Channels/{id}/Items` browse but
  stays in B's; `GET User/Hidden/{type}/{tmdb}` reads `true` for A and `false`
  for B; `user_hidden_items` carries exactly A's row.
- hiding the **series** also removes its whole subtree from A's browse — the
  scenario drills the series folder (`?FolderId=`) and asserts A gets 0 children
  while B still expands to ≥1 season/episode (the **episode** dimension).
- A unhides → the title returns to A; both read `false`; A's row is gone.
- browse matching keys on `ProviderIds.Tmdb` so it is robust to display-name
  changes.

**Surface 1 — per-user prefs toggle end to end:**
- a fresh user reads all-on defaults; A `POST`s `protectFavourites=false`; A then
  reads `False` while B still reads `True`; `user_prefs` carries exactly A's row
  with `protect_favourites=0`. A is restored to defaults for clean re-runs.

**Surface 2 — per-user favourite protection (the sweeper's live input):**
- `EvictionSweeper.RunOnceAsync` reads `GetUserPrefsAsync(userId).ProtectFavourites`
  per favouriting user. The scenario proves that per-user input is wired and
  isolated live (A off, B on). The eviction **decision** itself has **no**
  on-demand HTTP trigger — `EvictionSweeper` is a cron `IHostedService`, not an
  `IScheduledTask` — so its full decision matrix (favourite pins a shared file
  only while ≥1 favouriting user keeps protect on; opt-out; movie/TV parity) is
  covered exhaustively by `EvictionSweeperTests`, not driven live here. This
  boundary is called out in both the scenario header and this doc rather than
  faked green.

## Unit-test companions (kept green)

`PhantomLibraryUserControllerTests`, `PhantomMoviesChannelTests`,
`PhantomShowsChannelTests`, `EvictionSweeperTests`, `PhantomDbTests`,
`PhantomLibraryUserPrefsAdminTests` — the in-memory equivalents of every
invariant the rig drives live.

## Reuse

The scenario is the live half consumed by the later `zuul-live-rig-job` and the
P3 `migration-rig` work (per ROI Priority 1); it does not itself register a Zuul
job (that job is a separate task and has no Nodepool node label yet).

## Operator steps

No production action. To run: `tools/rig-scenarios/rig-up.sh --reset` (builds +
starts the patched rig Jellyfin on `:18096`), then
`tools/rig-scenarios/42-per-user-show-hide.sh`. The scenario cleans up its own
test users on exit; `tools/rig-scenarios/rig-down.sh` tears the rig down.

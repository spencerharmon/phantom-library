# ttfb-reduction-011 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-010`. `check=none` — no fix code
ships in the analysis doc commit; the enqueued fix task's own design doc
lives in the beehive hive layer per the `-009`/`-010` precedent.

## 1. The push fix landed AND a subsequent real run succeeded end-to-end

`ttfb-loadtime-push-exposition-newline-fix` merged at `2026-09-21T19:01:38Z`
(commit `46157cfeaef622bd58771d827f7f3031fb7638ad`). The daily CronJob's
DEPLOYED wrapper (`tools/ci/loadtime-daily-run.sh`) checks out `REPO_REF:
main` fresh into an `emptyDir` at pod start, so it always runs the live
`main` tip of `tools/rig-scenarios/47-loadtime-flows.sh` — no image rebuild
was required for the fix to take effect.

Live confirmation, queried directly from Mimir
(`kubectl -n monitoring port-forward svc/mimir 18083:8080` +
`curl .../prometheus/api/v1/query?query=push_time_seconds{job="phantom-loadtime"}`):

```
push_time_seconds{job="phantom-loadtime"} = 1790075845.36  ->  2026-09-22T11:17:25Z
```

That timestamp lines up exactly with `phantom-library-loadtime-daily-29834597`'s
start time (`2026-09-22T11:17:00Z`, from `kubectl -n phantom-library get job
... -o jsonpath='{.metadata.creationTimestamp}'`) — the daily job's push DID
land, ~25s after the job started. A `query_range` over the preceding 72h on
`phantom_loadtime_seconds{flow="list_load",item_type="movie"}` shows the
series stepping DOWN through three distinct values as pushes actually
succeeded: `432.610197` (stale, pre-fix) -> `1.129333` (an earlier post-fix
push, ~04:27 UTC) -> `0.148807` (the `-29834597` daily-job push, ~11:17 UTC,
still the current value at analysis time). Two independent successful
pushes since the fix — this is genuinely fresh, authoritative data, not a
one-off.

**Caveat:** the job itself still shows `Failed` / `BackoffLimitExceeded` in
`kubectl -n phantom-library get jobs` (`29834597`, `failed=2`,
`activeDeadlineSeconds=5400` but it failed in ~29s, so NOT a timeout). Since
the push step (`daily-run.sh` step 2) completed successfully — proven by the
`push_time_seconds` timestamp landing exactly at job-start+25s — the failure
is in a LATER step (step 3, the ratcheting regression guard
`tools/perf/loadtime-guard.sh`) and does not invalidate the pushed
measurement batch. Pod logs for `29834597` are already GC'd (no
`ttlSecondsAfterFinished` grace left, no in-cluster log aggregator/Loki
deployed to this monitoring namespace) so the guard-step failure itself
could not be traced this pass; if it recurs, `kubectl -n phantom-library get
events`/pod logs must be captured BEFORE they age out (the daily job runs at
`06:17` UTC, so the next fast window to catch it live is that run).
This is noted as a secondary, non-blocking finding — not this pass's
enqueued fix (see §4).

## 2. New authoritative baseline (supersedes `-009`'s `432.610197s`)

All values below are the `29834597` daily-job push (`2026-09-22T11:17:25Z`),
`job="phantom-loadtime"`, `color="green"`:

| flow | item_type | duration_s | errors |
|---|---|---|---|
| list_load | movie | 0.148807 | 0 |
| list_load | episode | 0.314579 | 0 |
| sort_change | movie | 0.143093 | 0 |
| sort_change | episode | 0.197873 | 0 |
| get_sources | movie | 0.037313 | 0 |
| get_sources | episode | 0.045996 | 0 |
| materialise | movie | 0.039045 | 0 |
| materialise | episode | 0.043308 | 0 |
| play_materialised | movie | 0.038734 | 0 |
| play_materialised | episode | 0.038386 | 0 |
| **info_open** | **movie** | 0.035738 | **1** |
| **info_open** | **episode** | 0.036050 | **1** |

Dominant stage by duration (successful flows only): `list_load{episode}`
(0.314579s) > `list_load{movie}` (0.148807s) > `sort_change{episode}`
(0.197873s) > `sort_change{movie}` (0.143093s) > the four ~0.04s
detail/materialise-family flows. All of these are now roughly three orders
of magnitude below the `-009` baseline (`list_load{movie}=432.610197s`).

## 3. `ttfb-list-load-movie-render-profile` is CONFIRMED to be the cause

The order-of-magnitude drop is not a rig fluke or a shrunk catalogue — it is
exactly the effect `ttfb-list-load-movie-render-profile`'s design doc
predicted. That task (merged `a953127c9e0d77f2768c5134e325cb26f8689f3d`,
reviewed dotnet-test 715/715 green) replaced `BuildFlatMovieItemsAsync`'s
per-orphan-file DB round-trip chain (one `GetGostreamPathTmdbAsync` + one
`GetTmdbMetadataAsync` + one `IsItemHiddenAsync` round trip PER FILE) with
two batched queries total for the whole orphan set. Its own
`Verify-After-Merge` task
(`ttfb-list-load-movie-render-profile-verify-after-merge`, gated on
`phantom_loadtime_seconds{flow="list_load",item_type="movie"} <
432.610197`) is now `DONE` — the runner recorded it failing on the FIRST
attempt (the merge had landed but no fresh push existed yet, per the
`Recovered (runner, lost work)` note on that task) and passing once a fresh
sample existed. `0.148807s < 432.610197s` by nearly three orders of
magnitude — a full, unambiguous confirmation, not a marginal win.

The intermediate value seen in the `query_range` (`1.129333`, ~04:27 UTC,
before the `0.148807` daily-job push) is a SEPARATE earlier successful push
(likely a manual verify run) — also far below the old baseline, corroborating
that this is a durable, reproducible fix effect and not a one-off measurement
artifact.

## 4. The 100%-cold-materialise-flow failure is NOT what it looked like — it is a RIG bug, not a plugin/deploy regression

`-009` and `-010` both observed `materialise`/`get_sources`/`info_open`/
`play_materialised` ALL erroring at `errors_total=1`/`runs_total=1` for both
item types, and both explicitly deferred root-causing it to Mimir-counter
inspection only. This pass traced it with REAL in-cluster reproduction
(per the task's instruction to use plugin/Jellyfin logs, not only Mimir
counters a third time) — and the picture is materially different from what
`-009`/`-010` assumed:

- Port-forwarded directly to the live dev color
  (`kubectl -n phantom-library port-forward svc/phantom-library-green
  18096:8096`), authenticated with the daily rig's own admin token
  (`phantom-loadtime-daily-token` secret), resolved a real Phantom-Movies
  channel item (`Id=564be42864d1d30531418d1ef4e62e67`, `13` (2010)), and
  reproduced BOTH calls the rig makes:
  - `GET /Items/564be42864d1d30531418d1ef4e62e67` (the `info_open` flow) ->
    **`HTTP 400 "Error processing request."`**
  - `GET /Items/564be42864d1d30531418d1ef4e62e67/PlaybackInfo` (the
    `get_sources` flow) -> **`HTTP 200`, full `MediaSources` payload** —
    confirming `get_sources`/`materialise`/`play_materialised` are
    genuinely healthy against this item; `-009`/`-010`'s "all four
    materialise-family flows fail" read was itself imprecise (aggregate
    counters can't distinguish "4 independent per-flow errors" from
    "the rig's outcome classifier marking play/materialise dependent on
    each other" — direct reproduction resolves the ambiguity).
- Live Jellyfin server log for the exact failing request
  (`kubectl -n phantom-library logs phantom-library-green-0`), captured
  immediately after reproducing it:

  ```
  [ERR] Jellyfin.Api.Middleware.ExceptionMiddleware: Error processing request. URL GET /Items/564be42864d1d30531418d1ef4e62e67.
  System.ArgumentException: Guid can't be empty (Parameter 'id')
     at Jellyfin.Server.Implementations.Users.UserManager.GetUserById(Guid id)
     at Jellyfin.Api.Controllers.UserLibraryController.GetItem(Nullable`1 userId, Guid itemId)
  ```

  This is **stock Jellyfin's own `UserLibraryController.GetItem`** (not a
  PhantomLibrary controller — `grep -rln '\[Route("Items' src/` and
  `grep -rln GetItem src/` both come up empty inside the plugin; this
  endpoint is untouched core code). Its signature is
  `GetItem(Guid? userId, Guid itemId)`: when the caller omits the `userId`
  query parameter, the controller does not null-check before calling
  `UserManager.GetUserById(userId.Value)`-equivalent, so a missing `userId`
  throws `ArgumentException("Guid can't be empty")` and the framework
  reports it as `HTTP 400`.
- `tools/rig-scenarios/47-loadtime-flows.sh` calls `info_open` as a bare
  `GET $API/Items/$id` — **`grep -n userId tools/rig-scenarios/47-loadtime-flows.sh`
  returns nothing: the script never resolves or passes a `userId` anywhere**,
  for `info_open` or any other flow. Every real Jellyfin client (web/mobile)
  always calls `GET /Items/{itemId}?userId={userId}` for the item-detail
  page — the rig simply never adopted that contract when `info_open` was
  added.

**Conclusion: the "100%-cold-materialise-flow failure" `-009` and `-010`
both flagged as a possible plugin/deploy regression is, for `info_open`
specifically, a RIG measurement bug** (a missing required query parameter),
not a Jellyfin/PhantomLibrary defect. The other three flows in that group
(`get_sources`, `materialise`, `play_materialised`) are confirmed healthy
(`errors=0` in the new authoritative sample, and reproduced live above for
`get_sources`). The cold-materialise failure rate on the newest sample is
therefore: **1 of 6 flows (`info_open` only) at 100% error rate for both
item types; the other 5 flows are 0% error.** This is a narrower, more
precise, and more encouraging picture than either prior pass could establish
from counters alone.

## 5. Enqueued fix (the single, now fully-diagnosed win)

`ttfb-rig-info-open-userid-fix` (ROI P9) — resolve a real user id once
(`GET /Users` with the rig's already-provisioned admin token, same pattern
already used ad hoc during this diagnosis) near the top of
`47-loadtime-flows.sh`, and append `?userId=$USER_ID` to the `info_open`
flow's `GET $API/Items/$id` call so it matches real client behavior and
stock Jellyfin's documented contract. Add a regression assertion to
`scripts/tests/p8-loadtime-flows.test.sh` proving the emitted `info_open`
curl command line includes a resolved, non-empty `userId` query parameter
(the pre-fix script must FAIL this new assertion; post-fix must PASS).
Design doc: `docs/tasks/ttfb-rig-info-open-userid-fix.md` (beehive layer).
`Check:` uses the `script-test` framework —
`./scripts/tests/p8-loadtime-flows.test.sh`.

## 6. Successor

`ttfb-reduction-012 [TODO]` appended, held `not_before` ~= +24h
(`2026-09-23T18:24:00Z`, daily cadence). It must (1) confirm
`ttfb-rig-info-open-userid-fix` landed and `info_open`'s `errors_total`
dropped to 0 on the next daily/manual sample; (2) re-rank the dominant stage
against that newest sample (now genuinely likely to be `list_load{episode}`
territory, sub-second); (3) if the guard-step job failure noted in §1
recurs, capture its pod logs BEFORE they age out and root-cause it (it is a
process-hygiene/tooling concern, separate from the measurement pipeline
itself). Design doc: `docs/tasks/ttfb-reduction-012.md` (beehive layer).

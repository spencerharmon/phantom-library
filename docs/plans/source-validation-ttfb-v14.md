# Source validation + TTFB hardening plan (schema v14)

Date: 2026-06-24

Scope authority: operator request to fix audio-language correctness, parallel candidate validation, season/series pack sanity, empirical materialise timing evidence, and gostream iowait mitigation. Latest main includes server-advertised Phantom item actions (`PhantomItemActionProvider`) for materialise/reset/reject/candidate selection and Reset clears existing candidate failure rows; SV14 must keep those action paths behaviorally identical to REST+kebab paths and preserve Reset as the operator escape hatch for stale validation/failure state. No requirement in this plan is deferred or dropped. v14 offline migration is operator-approved.

## Requirements ledger

| ID | Requirement | Disposition | Acceptance evidence required | Notes |
|---|---|---|---|---|
| REQ-SV14-AUDIO-ENGLISH | A source file is valid only if English audio is available; when English is available the plugin must select it for Jellyfin playback. | IMPLEMENT | gostream unit tests with Polish-default+English-present validates/returns English index; no-English rejects; plugin/Jellyfin tests prove `DefaultAudioStreamIndex` / playback `AudioStreamIndex` is English even when Polish is container-default. | No fallback to non-English without explicit future operator approval. |
| REQ-SV14-PARALLEL-VALIDATION | Check candidate viability in bounded parallel so the top working candidate can be picked without serially trying every bad candidate. | IMPLEMENT | Unit tests proving bounded parallelism, stop condition, top-ranked-valid selection, and failure caching; rig evidence with multiple bad candidates before a good exact episode candidate. | Parallelism must be bounded to protect disk/network. |
| REQ-SV14-CANDIDATE-PRUNE | Non-working candidates are removed from or clearly disabled in the list/action surfaces after validation failure. | IMPLEMENT | API/UI/item-action tests showing failed candidates get `isRejected=true` / failure reason and are not selectable/advertised unless override path exists; DB assertions for persisted failure/validation state. | Existing `magnet_failure_cache` remains source of hard rejection. |
| REQ-SV14-PACK-SANITY | Rejected season/series packs must be sanity-checked with real file-list evidence; parser must handle common valid pack layouts. | IMPLEMENT | Captured file-list fixtures from rejected Avatar packs; parser tests for Season/Book/Chapter/SxxExx/2x01/201 patterns; rig/source validation evidence showing valid packs accepted and invalid packs rejected with reason. | Do not assume all packs are bad. |
| REQ-SV14-TIMING-EVIDENCE | Produce empirical timing evidence for materialise time-to-first-byte phases before/with optimisations. | IMPLEMENT | Structured plugin + gostream timing logs/metrics; documented run commands; before/after table for indexer probe, validation, gostream add, metadata wait, file selection, audio probe, stub write, FUSE wait, channel refresh, PlaybackInfo/open. | Evidence required before claiming speedup. |
| REQ-SV14-IOWAIT-MITIGATION | Reduce or bound gostream-driven iowait during materialisation and bulk favourite flows. | IMPLEMENT | iostat/pidstat evidence; bounded validation/materialise concurrency; service/cgroup or queue controls tested; no unbounded favourite-season/series fanout. | Mitigation must not silently skip requested materialisations. |
| REQ-SV14-BULK-FAV-THROTTLE | Favourite season/series materialisation must be queued/throttled rather than firing all episodes at once. | IMPLEMENT | Unit tests for queueing all episodes with bounded active materialisations; live/rig evidence bulk favourite does not create unbounded concurrent gostream adds. | All episodes still scheduled; none dropped. |
| REQ-SV14-SOURCE-CANDIDATE-V14 | Persist validation state for source candidates in schema v14 via offline migration. | IMPLEMENT | Clone-tested migration; idempotency; backup creation; `PRAGMA user_version=14`; migration seeds existing v13 `source_candidates` rows; rollback guidance. | Offline migration explicitly approved by operator. |

## Current observed failure modes

1. **Wrong audio selected**: gostream/plugin path selects a video file but Jellyfin playback is left to container default audio. Playback can default to Polish even when English exists; plugin must set Jellyfin audio selection to English automatically.
2. **Candidate validation is serial and expensive**: materialiser attempts candidates one at a time through full `/api/library/add`; bad packs can hold `materialise_in_flight` and delay exact candidates.
3. **Pack rejection may be parser failure**: Avatar season/series packs were rejected with `target_episode_not_found`, `no_valid_files`, and `fuse_path_missing`; several likely contain requested episodes but use naming patterns not parsed today.
4. **Source list / validation state partially durable**: current schema v13 persists candidates and movie runtime minutes, but not validation status/reason/timing or selected pack file hints. Audio-language indexes do not need durable schema; plugin selects preferred audio from Jellyfin-visible media streams at playback/open time.
5. **Bulk favourite can stampede**: season/series favourite currently fires materialisation for every episode; materialiser has per-item in-flight guard but no bulk backpressure at listener level.
6. **Empirical timing incomplete**: we have anecdotal 30+ second source API/materialise waits but no phase timing across plugin+gostream.

## v14 persistence design

No foreign keys. Add validation columns to `source_candidates` rather than separate FK table, because validation state is per `(item,preset,magnet)`.

```sql
ALTER-equivalent v14 create-from-scratch / offline migration target:

source_candidates (
    tmdb_id    INTEGER NOT NULL,
    type       TEXT NOT NULL,
    season     INTEGER NOT NULL DEFAULT -1,
    episode    INTEGER NOT NULL DEFAULT -1,
    preset     TEXT NOT NULL DEFAULT '',
    magnet     TEXT NOT NULL,
    info_hash  TEXT NOT NULL,
    indexer    TEXT NOT NULL,
    title      TEXT NOT NULL,
    seeders    INTEGER,
    size       INTEGER,
    rank       INTEGER NOT NULL,
    source     TEXT NOT NULL,
    fetched_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,

    validation_status TEXT NOT NULL DEFAULT 'unknown', -- unknown|valid|invalid|transient
    validation_reason TEXT,
    validated_at INTEGER,
    validation_expires_at INTEGER,
    validation_duration_ms INTEGER,
    validation_policy_version TEXT NOT NULL DEFAULT 'unknown',

    selected_file_id INTEGER,
    selected_file_path TEXT,
    selected_file_size INTEGER,

    PRIMARY KEY (tmdb_id,type,season,episode,preset,magnet)
)
```

Indexes:

```sql
idx_source_candidates_item_rank(tmdb_id,type,season,episode,preset,rank)
idx_source_candidates_validation(tmdb_id,type,season,episode,preset,validation_status,rank)
idx_source_candidates_expiry(expires_at)
idx_source_candidates_hash(info_hash)
```

Offline migration script `scripts/migrate-source-validation-v14.sh`:

1. Requires Jellyfin stopped for production DB paths.
2. Backs up `phantom.db`, `-wal`, `-shm`.
3. Accepts v13 only; idempotent if already v14 with target columns.
4. Creates new table, copies all existing v13 `source_candidates` rows with validation fields `unknown` and null selected-file fields.
5. Rebuilds indexes.
6. Marks pre-SV14 episode hard failures for pack/file-selection reasons as stale by expiring or deleting matching `magnet_failure_cache` rows so they are revalidated under `SourceValidationPolicyVersion=sv14-parser-audio-v1`.
7. Sets `PRAGMA user_version=14`.
8. Verifies row counts and idempotency against clone before handoff.

Plugin startup/fresh-DB requirements:

- `PhantomDb.CurrentSchemaVersion` must be 14.
- fresh empty DB must create v14 `source_candidates` with validation columns including `validation_policy_version`; no durable audio-index columns are required.
- fresh empty DB must create v14 `magnet_failure_cache.validation_policy_version`.
- fresh empty DB must create `bulk_materialise_requests` and `bulk_materialise_items` with indexes.
- startup on v13 must hard-refuse with explicit instruction to stop Jellyfin and run the v14 offline migration script, not generic wipe guidance.
- tests must cover fresh v14 schema, v13 refusal message with migration-script pointer, migration idempotency, query/write against every new validation column, policy-version stale failure ignore/revalidate behavior, and query/write/recovery against bulk materialise tables.

## gostream API changes

### Audio-selection playback contract

Validation must not stop at “preferred language exists.” It must define and verify how the preferred-language track becomes the actual Jellyfin-selected playback audio. gostream proves available tracks and can recommend the matching stream index; the plugin owns Jellyfin-facing selection and must not rely on container default track order.

Required contract:

1. gostream must identify audio tracks for the selected video file with stable fields:
   - container stream index,
   - codec,
   - language code (`eng`, `en`, `english` normalized to `eng`),
   - title,
   - channel count,
   - default flag if available.
2. gostream must validate that the plugin-requested preferred audio language is present before returning `valid`, and must return the matching preferred-language ffprobe stream index. If Polish and English are both present and Polish is container-default, a request with preferred English still returns the English stream index.
3. Plugin must mirror Jellyfin's native audio-selection rules at playback/open time from the actual file Jellyfin is about to read:
   - preferred-language matching follows Jellyfin `MediaSourceManager.NormalizeLanguage(user.AudioLanguagePreference)` semantics: empty preference means any language; known language names/codes expand through Jellyfin localization to three-letter ISO names unless culture name contains a region; unknown values compare literally,
   - stream choice follows Jellyfin `MediaStreamSelector.GetDefaultAudioStreamIndex`: sort audio streams by language preference score, forced flag, default flag, external-stream support, text-subtitle flag, external flag; if `user.PlayDefaultAudioTrack` is true, choose the first default stream from that sorted list; otherwise choose the first sorted stream,
   - remembered per-item user selection wins only when `user.RememberAudioSelections` is true, item permits remembering, and saved `UserItemData.AudioStreamIndex` exists in current audio streams,
   - after materialise, plugin uses the file path/FUSE path and Jellyfin-visible media streams (or a plugin-side bounded media probe that produces equivalent `MediaStream` fields if streams are not yet populated),
   - `MediaSourceInfo.DefaultAudioStreamIndex` must be set to the Jellyfin-selected `MediaStream.Index`, not a persisted gostream index,
   - if stream indexes differ between ffprobe/gostream and Jellyfin, plugin uses Jellyfin's index for Jellyfin selection,
   - plugin must pass selected audio intent through any `/PlaybackInfo` / `/LiveStreams/Open` path it controls and must not leave default audio to container order.
4. Audio index persistence is explicitly not required for SV14:
   - validation response may include `selected_audio_index` for evidence/logging, but source-of-truth playback selection is the plugin's current file/stream inspection,
   - `source_candidates` does not need `selected_audio_index`, `selected_audio_language`, or `audio_tracks_json`,
   - gostream stream URL `audio_stream_index` support is optional secondary defense, not acceptance criteria for plugin correctness.
5. `/api/library/add` must revalidate preferred-language/file constraints when lease is absent or expired, but it does not need to persist selected audio metadata. Live validation lease is an optimisation, not the only correctness path.
6. Plugin/Jellyfin evidence must prove preferred-language selection end-to-end:
   - gostream validate response shows preferred language present,
   - plugin-created `MediaSourceInfo` has preferred-language `DefaultAudioStreamIndex`,
   - Jellyfin `PlaybackInfo` / `OpenLiveStream` / playback-progress evidence shows `AudioStreamIndex` is preferred language even when another language is container-default.

No implementation may claim REQ-SV14-AUDIO-ENGLISH done by merely detecting English in gostream or by relying on gostream/container defaults without plugin-set Jellyfin audio selection.

### `POST /api/library/validate`

Access-control contract:

- `/api/library/validate`, `/api/library/validate/release`, `/api/library/add`, and `/api/library/remove` must share the same protection.
- SV14 must add `X-Gostream-Token` shared-secret support for these mutation/validation endpoints.
- If token is configured, every request must present the correct token, including loopback clients; loopback does not bypass configured token auth.
- If token is not configured, endpoints must accept only loopback clients (`127.0.0.1` / `::1`) and reject non-loopback with HTTP `403`.
- Plugin config/install must pass token when configured.
- Tests must cover missing token, wrong token, loopback allowed, non-loopback rejected, and validate/release cannot be invoked anonymously from non-loopback.

Input extends existing add request:

```json
{
  "type": "episode",
  "tmdb": 246,
  "series_imdb": "tt0417299",
  "title": "Avatar The Last Airbender",
  "year": 2005,
  "season": 2,
  "episode": 1,
  "magnet": "magnet:?xt=...",
  "required_audio_languages": ["eng", "en", "english"],
  "preferred_audio_language": "eng",
  "validation_session_id": "uuid-or-plugin-generated-token"
}
```

Response:

```json
{
  "status": "valid",
  "reason": null,
  "hash": "...",
  "selected_file": { "id": 12, "path": "...S02E01...mkv", "size": 5121748500 },
  "audio_tracks": [
    { "stream_index": 1, "language": "pol", "title": "Polish", "codec": "aac", "channels": 2 },
    { "stream_index": 2, "language": "eng", "title": "English", "codec": "aac", "channels": 2 }
  ],
  "selected_audio_index": 2,
  "selected_audio_language": "eng",
  "validation_session_id": "same-token",
  "validation_lease_expires_at": "2026-06-24T01:23:45Z",
  "timings_ms": {
    "add_torrent": 120,
    "metadata_wait": 2500,
    "file_select": 4,
    "audio_probe": 900,
    "total": 3524
  }
}
```

Response status/HTTP contract:

- HTTP `200` with `status="valid"`: file selected and preferred English audio proven present; plugin remains responsible for choosing the English track from current Jellyfin-visible streams at playback/open time.
- HTTP `200` with `status="invalid"`: candidate is a hard failure for this item until normal retry/override; includes `reason`.
- HTTP `200` with `status="transient"`: candidate was not proven valid or invalid; includes `reason`. gostream may include `retry_after_hint`, but plugin config is source of truth for persisted retry.
- HTTP `400`: caller/request bug; plugin logs configuration/error and does not mark candidate transient.
- HTTP `401`/`403`: auth/config/security error; plugin logs configuration/error and does not mark candidate transient.
- HTTP `404`: incompatible gostream build/endpoint missing; plugin logs deployment error and does not mark candidate transient.
- HTTP `5xx` or transport timeout: always transient validation failure with retry based on `SourceValidationTransientRetryMinutes`; plugin must ignore any hard-validation-looking body on 5xx.

Hard invalid reasons:

- `target_episode_not_found`
- `no_valid_files`
- `no_english_audio`
- `no_main_english_audio`
- `audio_probe_unsupported_format`

Transient reasons:

- `metadata_timeout`
- `audio_probe_timeout`
- `audio_probe_failed`
- `torrent_engine_busy`
- `validation_cancelled`

Rules:

- English track required for `valid`.
- If English exists, validation should report the matching ffprobe stream index when available for evidence/logging; plugin must not depend on this persisted value and must choose English from current Jellyfin-visible streams for playback.
- Non-English-only files are invalid.
- Transient metadata/indexer/audio failures must not be cached as hard invalid; plugin persists v14 `validation_status='transient'`, `validation_reason`, and `validation_expires_at=now+SourceValidationTransientRetryMinutes`. Plugin ignores gostream retry hints for persistence except for logging.
- Validate must not write a stub and must not leave losing/invalid torrents in an active downloading state.
- Plugin generates `validation_session_id` as a UUID per candidate validation attempt and sends it in `/validate`.
- gostream echoes `validation_session_id` and `validation_lease_expires_at` in `/validate` response.
- Plugin sends the winning `validation_session_id` to `/add` when materialising immediately after live validation; `/add` consumes/releases that winner lease.
- For cached valid candidates whose validation lease expired, plugin sends selected file/audio hints without `validation_session_id`; `/add` must re-check the selected file still exists and still has required English audio before writing stub, but may skip full candidate search/ranking.
- Abandoned validation leases expire after `SourceValidationLeaseMinutes` default `10`, min `1`, max `60`; gostream cleanup releases expired validation-owned torrent priority/state.
- gostream must expose `POST /api/library/validate/release` with `{validation_session_id, hash}` so plugin cancels/de-prioritizes losing validations immediately after winner proof. Release is scoped by validation lease and must follow shared-hash safety checks.
- gostream tracks `(hash, validation_session_id)` leases.
- Cleanup/de-prioritize may affect only torrent state owned by that validation session.
- Before removing or zero-prioritizing a hash, gostream must check active playback, materialised stubs, in-progress `/add`, and other validation sessions for same hash.
- Shared hashes must be left active if referenced outside this validation session.
- Winner validation may remain only if immediately reused by `/add`; `/add` consumes or releases the validation lease.
- Parallel validation cancellation must call `/api/library/validate/release` for losers once a top working candidate is proven, subject to shared-hash lease/refcount safety above.

### `POST /api/library/add`

Extend request to accept validation result hints:

```json
{
  "selected_file_id": 12,
  "selected_file_path": "...",
  "required_audio_languages": ["eng", "en", "english"],
  "preferred_audio_language": "eng",
  "validation_session_id": "winner-token-from-validate"
}
```

Rules:

- Revalidate selected file/audio before writing stub.
- If hint stale, fail with explicit reason or reselect same constraints.
- `/add` must ensure selected file still satisfies preferred-language constraints. Plugin then inspects the materialised file/media streams and sets Jellyfin `MediaSourceInfo.DefaultAudioStreamIndex`; no gostream audio-index persistence is required.

## Plugin validation/materialise algorithm

For movie/episode materialise:

1. Load `source_candidates` and compute effective rank from current scoring policy plus exact-episode specificity; stored rank is only original source order.
2. Exclude hard rejected candidates unless operator override.
3. Split candidates:
   - exact episode candidates
   - season packs
   - series packs
   - weak/ambiguous candidates
4. Enforce deterministic episode group precedence before validation:
   - exact episode releases are validated before season packs;
   - season packs are validated before series packs;
   - series packs are validated before weak/ambiguous candidates;
   - a lower group cannot be selected while any higher group has unvalidated candidates remaining until that higher group is exhausted or hits its per-group timeout.
   - per-group timeout default is `min(remaining absolute timeout, SourceValidationTimeoutSeconds / 2, 30s)`; the absolute `SourceValidationTimeoutSeconds` caps all groups combined.
   - final response must state when a lower-group winner was selected because higher-group candidates were invalid/transient.
5. Validate candidates within the current group with bounded parallelism:
   - default parallelism `2`, configurable,
   - no fixed “first 6 only” cap may prevent reaching a lower-ranked valid candidate,
   - candidates are processed in rank windows; when a window finishes with no valid candidate, advance to next window until valid candidate, hard exhaustion, or operator-configured absolute timeout.
6. Stop condition must prove the selected candidate is the top working candidate within the highest available group under observed results:
   - if rank #2 is valid while rank #1 is still running, wait for #1 until its validation finishes or the absolute timeout classifies it `transient`;
   - if all higher-ranked candidates are `invalid`, selected lower valid candidate may win;
   - if higher-ranked candidates are `transient`, response must state selected winner was chosen over transient higher-ranked candidates and persist transient status with short retry, not hard reject;
   - if timeout expires before any valid candidate and unvalidated candidates remain, materialise returns `validation_timeout` with candidates left unmodified except transient validation state.
7. Persist validation result to v14 columns.
8. Mark invalid hard failures in `magnet_failure_cache` when appropriate. Scope is per item because `MagnetFailureKey` includes `tmdb_id,type,season,episode,preset,magnet`; a pack invalid for S02E01 must remain eligible for S02E02 unless separately failed for that episode. TTL for hard validation failures uses `SourceValidationTtlHours`; operator override can bypass only for the exact item/magnet key. SV14 introduces `SourceValidationPolicyVersion`; pre-SV14 hard failures for `target_episode_not_found`, `no_valid_files`, `fuse_path_missing`, and audio-related reasons must be treated as stale for episode candidates and revalidated under the new parser/audio policy before disabling the candidate.
9. Call `/api/library/add` with selected file/audio hints.
10. Persist materialised state only after FUSE path exists.

Config/time budget contract:

- `SourceValidationParallelism` default `2`, min `1`, max `6`.
- `SourceValidationWindowSize` default `4`, min `1`, max `12`.
- `SourceValidationTimeoutSeconds` default `45`, min `5`, max `300`; absolute timeout for one materialise validation pass.
- `SourceValidationDetailsBudgetSeconds` default `8`, min `1`, max `30`; foreground details-page validation budget before returning cached/unknown state.
- `SourceValidationTtlHours` default `168` (7 days), min `1`, max `720`; sets `validation_expires_at` for valid/invalid validation results.
- `SourceValidationTransientRetryMinutes` default `30`, min `1`, max `1440`; sets retry for transient validation state.
- `SourceValidationLeaseMinutes` default `10`, min `1`, max `60`; gostream validation lease expiry.
- `BulkMaterialiseRunningStaleMinutes` default `30`, min `1`, max `1440`; stale `running` bulk items reset to `retry` on startup.
- `BulkMaterialiseWorkerCount` default `2`, min `1`, max `8`; maximum claimed/running bulk items, still subject to `GostreamHeavyConcurrency` for gostream I/O.
- `SourceValidationPolicyVersion` default `sv14-parser-audio-v1`; persisted with validation state and used to ignore/revalidate stale failures from older parser/audio policies.
- If config fields are absent, these exact defaults apply; tests must prove defaulting and bounds.

Required race tests:

- first validation window all invalid, next window contains exact valid candidate;
- slower higher-ranked valid candidate beats faster lower-ranked valid candidate;
- higher-ranked transient plus lower-ranked valid returns valid with explicit transient-overridden message/state;
- absolute timeout leaves unvalidated candidates retryable, not rejected.

Candidate list API:

- Returns cached validation state immediately only when `validation_expires_at >= now`.
- Expired `valid`, `invalid`, or `transient` validation state is treated as `unknown` for selection/materialise and must be revalidated before use; API may show expired reason as historical diagnostics but must not disable or trust candidate based on expired state.
- Materialise flow must revalidate any candidate whose validation is missing or expired, including previously valid English-audio selections.
- Details page may kick refresh/validation in foreground up to `SourceValidationDetailsBudgetSeconds`; if budget expires, return cached unexpired state plus `validation_pending=true`.
- Hides or disables unexpired invalid candidates with reason; does not silently delete evidence.

## Pack sanity work

Collect fixtures before changing parser:

1. Enumerate actual rejected hashes from production/rig `magnet_failure_cache` joined to `source_candidates` for target episodes.
2. For each selected rejected hash, call gostream file-list/debug endpoint or permanent validate diagnostics to capture raw `[]FileStat` before cache expiry/cleanup.
3. Store sanitized fixtures under gostream tests, e.g. `internal/library/testdata/avatar_pack_*.json`.
4. Add fixture manifest with:
   - info hash,
   - original title,
   - original rejection reason,
   - capture timestamp,
   - sanitized file-list filename,
   - expected parser outcome.
5. Temporary logging is not acceptable as final evidence; any diagnostic added for capture must either be removed before merge or remain as tested/debug endpoint.
6. Add parser cases for observed valid layouts:
   - `S02E01`
   - `2x01`
   - `Season 02/01 - Title.mkv`
   - `Season.02/01 - Title.mkv`
   - `Book 2/Chapter 1 - The Avatar State.mkv`
   - `Book 2/201 - The Avatar State.mkv`
   - `Avatar - 201 - The Avatar State.mkv`
7. Re-run validation fixtures; valid packs must select requested episode file.

## Timing / empirical evidence plan

Phase order is mandatory:

0. **Instrumentation-only build**: add timing logs/metrics without changing validation/materialise behavior.
1. **Baseline capture**: run current problematic cases and store timing/iowait evidence before optimization behavior changes.
2. **Optimization build**: add validate endpoint, parallel validation, audio contract, parser fixes, and throttling.
3. **After capture**: rerun the same cases and compare.

If old code lacks fine phase timings, phase 0 must be installed and used as the baseline before phase 2 changes are claimed. Anecdotal reconstruction is not acceptable.

### Plugin timing log

Emit one structured log line per materialise:

```text
[MaterialiseTiming] external=episode_246_s02e01 total_ms=... cached_candidates_ms=... indexer_probe_ms=... validation_ms=... gostream_add_ms=... fuse_wait_ms=... refresh_ms=... outcome=...
```

### gostream timing log

Emit per validate/add:

```text
[LibraryValidateTiming] hash=... status=valid total_ms=... add_torrent_ms=... metadata_wait_ms=... file_select_ms=... audio_probe_ms=... file_count=...
[LibraryAddTiming] hash=... total_ms=... add_torrent_ms=... metadata_wait_ms=... file_select_ms=... audio_probe_ms=... stub_write_ms=...
```

### Commands

```bash
journalctl -u jellyfin -f | grep MaterialiseTiming
journalctl -u gostream -f | grep -E 'Library(Add|Validate)Timing'
iostat -xz 1
pidstat -d -p $(pgrep -f '/usr/local/bin/gostream') 1
curl -s http://localhost:8096/metrics | grep phantom
curl -s http://127.0.0.1:9080/metrics | grep -E 'gostream|torrent|fuse|library'
```

Acceptance report must include before/after table for at least:

- exact episode candidate
- season pack candidate
- series pack candidate
- no-English candidate
- English-not-default candidate
- bulk favourite season flow

## I/O wait mitigation

Implement all:

1. Add one global plugin-side `GostreamHeavyLimiter` covering every gostream `/validate` and `/add` call from bulk favourite, manual materialise, selected-source materialise, item-action materialise/candidate selection, and reject-current re-materialise. Config key `GostreamHeavyConcurrency` default `2`, min `1`, max `4`. Acquisition boundary is each individual gostream HTTP call, not the whole materialise orchestration; every validate/add helper acquires before HTTP call and releases in `finally`. `SourceValidationParallelism` controls attempted parallel tasks, but actual concurrent gostream calls can never exceed `GostreamHeavyConcurrency`. Tests assert `GostreamHeavyConcurrency=1` has no self-deadlock and combined concurrent validate+add calls never exceed configured cap.
2. Throttle favourite season/series bulk flow through queue; all episodes scheduled durably, but active gostream adds bounded.
3. Prefer exact episode releases before packs to avoid huge pack metadata/file walks unless needed.
4. Cache validation state to avoid repeated metadata/audio probes.
5. Add systemd/podman IO controls to operator deploy docs/install output and test with iowait capture:
   - `IOSchedulingClass=best-effort`
   - `IOSchedulingPriority=7`
   - `IOWeight=100`
6. Define global gostream-heavy concurrency boundary covering validate, add, bulk favourite, manual materialise, selected-source materialise, and server-advertised item actions. Legacy gostream syncer interactions must be measured and documented; if not controllable by plugin, gostream-side limiter must be added.

## Test matrix

### gostream unit tests

- Polish default + English track present → valid, response identifies English stream index.
- Polish only → invalid `no_english_audio`.
- English commentary only → invalid `no_main_english_audio`.
- English untagged but audio title/name contains token `english` or `eng` after splitting on non-letter/digit boundaries → valid and selected; heuristic support is required when container language tag is missing. Bare token `en` is accepted only from a structured language tag field, never from free-text title/name. Substring matches are forbidden: `french`, `poleng`, `denoise`, `Audio en Español`, and `Commentaire en français` must not match English.
- English commentary/descriptive tracks are not valid main audio. Titles/names containing tokens `commentary`, `comment`, `director`, `descriptive`, `description`, `audio description`, or `sdh` are excluded from main English selection. If only English tracks are commentary/descriptive, validation is invalid with `no_main_english_audio`.
- Season pack `Book 2/Chapter 1` → selects S02E01.
- Season pack `201 - Title` → selects S02E01.
- Series pack wrong episode only → invalid `target_episode_not_found`.
- Validate endpoint does not write stub.
- Add endpoint revalidates selected file/audio hint.
- Shared-hash validation cleanup does not remove or zero-prioritize torrent when same hash has active playback.
- Shared-hash validation cleanup does not remove or zero-prioritize torrent when same hash has materialised stub reference.
- Parallel validation sessions for same hash cannot clean each other's leases before expiry.
- Expired validation lease cleanup affects only validation-owned priority/state and preserves add/playback/materialised references.

### plugin unit tests

- v14 migration creates validation columns and preserves existing v13 source candidate rows.
- Fresh DB creates v14 table/columns.
- Startup on v13 refuses with v14 migration-script pointer.
- Candidate API returns cached candidates when indexers down.
- `PhantomItemActionProvider` advertises only validation-eligible candidate actions; rejected/invalid candidates are omitted or disabled consistently with REST+kebab source list.
- `PhantomItemActionProvider.InvokeAsync` materialise/reset/reject/candidate paths use the same validation, failure-cache, stale-in-flight, and gostream limiter paths as REST+kebab operations.
- Reset clears persisted candidate validation/failure state for the item (including SV14 validation failures and legacy candidate failure rows) and returns it to revalidation-eligible base availability.
- Parallel validation picks top valid after higher-ranked invalid.
- Parallel validation waits for slower higher-ranked valid before picking lower-ranked valid.
- Invalid candidates persisted/disabled with reasons.
- English validation failure maps to `magnet_failure_cache` reason.
- Plugin builds materialised movie and episode `MediaSourceInfo` with audio `MediaStreams` and Jellyfin-mirrored `DefaultAudioStreamIndex` from current file/stream inspection, not persisted gostream audio index.
- Plugin mirrors Jellyfin `MediaSourceManager` / `MediaStreamSelector` audio selection, including remembered user selection, `AudioLanguagePreference`, and `PlayDefaultAudioTrack` behavior.
- Plugin uses Jellyfin `MediaStream.Index` for `DefaultAudioStreamIndex` even if gostream/ffprobe reported different indexes during validation.
- Bulk favourite schedules all episodes durably and respects concurrency.
- Bulk favourite pending work survives Jellyfin/plugin restart or is reconciled from persisted favourite season/series intent at startup.
- Completed favourite series/season is periodically reconciled while still favourited: newly discovered/missing episodes are upserted into the same deterministic request and scheduled without requiring unfavourite/refavourite.

### rig/live tests

- Avatar S02E01 exact candidate materialises without trying bad packs first.
- Season pack fixture validates if parser supports actual layout.
- Playback starts with English audio selected by plugin/Jellyfin even when Polish is container-default and English is second audio track.
- Candidate list loads from v14 cache with indexers disabled/down.
- iowait evidence captured before/after.

## Magnet failure policy version

### v14 `magnet_failure_cache` extension

Add column:

```sql
validation_policy_version TEXT NOT NULL DEFAULT 'legacy'
```

Rules:

- New hard validation failures write current `SourceValidationPolicyVersion`.
- Failure lookup ignores rows whose `validation_policy_version` differs from current policy for parser/audio-sensitive reasons (`target_episode_not_found`, `no_valid_files`, `fuse_path_missing`, `no_english_audio`, `no_main_english_audio`, `audio_probe_unsupported_format`).
- Operator rejections (`operator_rejected`) remain valid across policy versions unless operator override is requested.
- v14 migration sets existing rows to `legacy`, then expires/deletes parser/audio-sensitive episode failures so they are revalidated under SV14.

## Durable bulk favourite queue requirement

The current fire-and-forget season/series favourite behavior is not sufficient for SV14. Implement a durable queue.

### v14 table: `bulk_materialise_requests`

```sql
CREATE TABLE bulk_materialise_requests (
    request_id        TEXT PRIMARY KEY,   -- sha256(user_id || ':' || parent_external_id), hex
    user_id           TEXT NOT NULL,
    parent_external_id TEXT NOT NULL,     -- series_<tmdb> or season_<tmdb>_sNN
    parent_kind       TEXT NOT NULL,      -- series|season
    tmdb_id           INTEGER NOT NULL,
    season            INTEGER NOT NULL DEFAULT -1,
    trigger           TEXT NOT NULL,      -- favourite
    status            TEXT NOT NULL,      -- pending|running|done|failed|cancelled
    requested_at      INTEGER NOT NULL,
    updated_at        INTEGER NOT NULL,
    last_error        TEXT,
    last_unfavorited_at INTEGER,
    generation INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_bulk_materialise_requests_status
    ON bulk_materialise_requests(status, updated_at);
CREATE UNIQUE INDEX idx_bulk_materialise_requests_active_parent
    ON bulk_materialise_requests(user_id, parent_external_id)
    WHERE status IN ('pending','running');
```

### v14 table: `bulk_materialise_items`

```sql
CREATE TABLE bulk_materialise_items (
    request_id   TEXT NOT NULL,
    tmdb_id      INTEGER NOT NULL,
    type         TEXT NOT NULL,       -- episode
    season       INTEGER NOT NULL,
    episode      INTEGER NOT NULL,
    status       TEXT NOT NULL,       -- pending|running|done|failed|retry|cancelled
    generation   INTEGER NOT NULL DEFAULT 0,
    claim_token  TEXT,
    attempts     INTEGER NOT NULL DEFAULT 0,
    next_run_at  INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL,
    last_error   TEXT,
    PRIMARY KEY (request_id, tmdb_id, type, season, episode)
);
CREATE INDEX idx_bulk_materialise_items_due
    ON bulk_materialise_items(status, next_run_at);
CREATE INDEX idx_bulk_materialise_items_episode
    ON bulk_materialise_items(tmdb_id, type, season, episode);
```

No FK per operator no-FK direction; request/item relationship is enforced by code and composite keys.

### Required behavior

- `request_id` is deterministic: lowercase hex SHA-256 of `user_id + ':' + parent_external_id`.
- Repeated `UserDataSaved` events with `IsFavorite=true` for the same user/parent upsert the active request and missing child rows; they must not create duplicate active requests.
- If existing request is `done`, `failed`, or `cancelled`, a later `IsFavorite=true` event resets it only when `last_unfavorited_at IS NOT NULL`; reset increments `generation`, clears `last_unfavorited_at`, sets status `pending`, clears `last_error`, and reconciles child rows for the new generation. Repeated `IsFavorite=true` saves after completion are no-ops because `last_unfavorited_at` remains null.
- On `IsFavorite=false`, set `last_unfavorited_at=now` and `updated_at=now`; if status is `pending|running`, apply cancellation behavior below.
- Persist parent request and every expanded episode before starting any materialisation.
- Worker first peeks due item IDs without changing status, then claims one due item with transactional compare-and-set. Rows may be `running` while waiting for per-call gostream limiter, but no gostream I/O happens until validate/add helpers acquire `GostreamHeavyLimiter`:
  ```sql
  UPDATE bulk_materialise_items
  SET status='running', claim_token=$claim, attempts=attempts+1, updated_at=$now
  WHERE request_id=$request AND tmdb_id=$tmdb AND type='episode'
    AND season=$season AND episode=$episode AND generation=$generation
    AND status IN ('pending','retry') AND next_run_at <= $now;
  ```
  Claim succeeds only when affected rows = 1. Completion updates must include `request_id`, item key, `generation`, and `claim_token`; stale workers from a previous generation cannot mutate new-generation rows. Separate test asserts many rows may be claimed only up to `BulkMaterialiseWorkerCount` but gostream HTTP concurrency still never exceeds `GostreamHeavyConcurrency`.
- On plugin startup, resume due items and reconcile `running` items older than stale threshold back to `retry`.
- On startup and on a bounded periodic cadence, scan still-favourited parent series/season items, re-expand metadata, and upsert any missing episodes into the deterministic request even when request status is `done`; when any new child row is inserted or reset to `pending`, parent status becomes `pending` and `updated_at=now` within the same transaction. This handles late metadata/new episodes without requiring unfavourite/refavourite.
- Post-claim transitions:
  - materialise success or duplicate => item `done`, `updated_at=now`;
  - hard invalid/no source => item `failed`, `last_error=reason`;
  - transient/timeout/exception => item `retry`, `next_run_at=now + min(2^attempts minutes, 60 minutes)`, `last_error=reason`;
  - attempts cap `BulkMaterialiseMaxAttempts` default `5`, min `1`, max `20`; exceeding cap => `failed`.
- Parent request aggregation after each child update:
  - if parent status is `cancelled`, keep parent `cancelled` regardless of child terminal states;
  - otherwise all children `done` => parent `done`;
  - otherwise any child `failed` and no `pending|running|retry` remain => parent `failed`;
  - otherwise parent `running` while work remains.
- If user unfavourites parent while work remains, mark request `cancelled`; pending/retry children become `cancelled`, running child is allowed to finish current attempt then becomes `done` or `failed` but parent remains `cancelled` and no retries are scheduled; already materialised episodes remain intact unless separate eviction policy later removes them.
- Never drop queued episodes because Jellyfin/plugin restarts.

### Migration/fresh-schema/test requirements

- v14 offline migration creates both bulk tables and indexes along with source-candidate validation columns.
- Fresh DB creates both bulk tables.
- Idempotency check verifies both bulk tables and source-candidate validation columns.
- Startup validation/query tests cover both bulk tables.
- Unit tests prove restart recovery: `running` stale item returns to `retry`; `pending` item survives DB reopen; two workers racing claim only one row once; worker respects concurrency and eventually schedules all episodes.

## Handoff / migration steps

1. Stop Jellyfin.
2. Run clone-tested `scripts/migrate-source-validation-v14.sh`.
3. Install plugin and gostream build.
4. Restart gostream if gostream changed.
5. Restart Jellyfin.
6. Hard-refresh browser.
7. Run source validation rig + timing capture.

## Rollback procedure

Migration script must force a SQLite checkpoint before backup:

```sql
PRAGMA wal_checkpoint(TRUNCATE);
```

After checkpoint, backup artifact is the main `phantom.db`; `-wal` and `-shm` are not restored across versions. Migration script may copy them for forensics, but rollback uses checkpointed main DB only. Rollback trigger conditions:

- migration exits non-zero;
- post-migration verification row counts mismatch;
- plugin refuses v14 DB on startup;
- source candidate queries fail due missing validation/bulk columns.

Rollback steps:

1. Stop Jellyfin and gostream if started after failed migration.
2. Reinstall previous plugin/gostream build compatible with schema v12.
3. Restore checkpointed backup atomically while Jellyfin is stopped:
   ```bash
   db=/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db
   dir=$(dirname "$db")
   tmp="$dir/.phantom.db.rollback.$$"
   cp -a <backup> "$tmp"
   sync "$tmp"
   rm -f "$db-wal" "$db-shm"
   mv -f "$tmp" "$db"
   sync "$dir"
   ```
4. Verify `PRAGMA user_version=12` on restored DB.
5. Start Jellyfin.

Compatibility rule: once v14 plugin has successfully written validation/bulk rows, rollback requires restoring the pre-v14 backup; v12 plugin must not run against v14 DB.

No requirement above is deferred. If any item cannot be implemented honestly, stop and request operator disposition before marking done.

# Isolated PostgreSQL playback parity

## Entry points

- `./scripts/tests/postgres-playback-parity.test.sh`: mandatory adapter regressions
  **and** both 35/movie and 36/episode flows on PostgreSQL. No skip/dry-run mode.
- `python3 tools/rig-scenarios/isolated-playback-rig.py --backend sqlite`: same
  assertions on SQLite, still using synthetic Jellyfin state.
- Standalone `tools/rig-scenarios/35-channel-e2e-playback.sh` / `36-channel-episode-e2e-playback.sh`
  select one scenario (SQLite by default, `PHANTOM_RIG_BACKEND=postgres` to change).
  `RIG_NO_RESET=1` retains existing callers such as migration scenario 44: they
  own bringing up/cleaning the server. See `playback-scenario.sh` for explicit
  paths/API/token overrides. The isolated check never enters the legacy defaults.

Run worktree-scoped commands via the harness, for example:

```sh
beehive submodule worktree exec phantom-library bee-p0-postgres-playback-parity-rig -- ./scripts/tests/postgres-playback-parity.test.sh
```

## Runtime and build contract

Linux, Python >=3.12, .NET 9 SDK/runtime, `patch`, network access to public
Jellyfin source/FFmpeg and NuGet, and either rootless `podman` or `psql` plus an
explicit disposable PostgreSQL test service. The default builds upstream
Jellyfin **v10.11.9** with **every** repository `scripts/jellyfin-patches/*.patch`
in a unique throwaway build tree, then builds **this checkout's plugin source**
with private dependencies copied locally. Build servers/node reuse are disabled;
only owned process groups are terminated. Jellyfin FFmpeg **7.1.3-1** portable
is fetched for the real media probe/stream path (stock FFprobe is not a substitute
for Jellyfin's probe extensions). No web bundle is needed for REST playback.

To inject an already-built candidate, set `PHANTOM_PARITY_PLUGIN` to its directory
containing `Jellyfin.Plugin.PhantomLibrary.dll` **and its private dependency DLLs**.
`PHANTOM_PARITY_SERVER` selects the directory containing `jellyfin.dll` and the
complete patched server distribution. Set both to avoid a build; omitted artifacts
are built from this checkout. The runner copies the selected candidate into a fresh
plugin directory, compares SHA256, checks `/Plugins` reports it Active, and records
its actual version. The real native-open assertions reject an unpatched server.
`PHANTOM_PARITY_FFMPEG` can select a compatible built FFmpeg. For local iteration,
`PHANTOM_PARITY_KEEP_BUILD=1` retains the build tree; this never skips scenarios.

PostgreSQL defaults to a new `postgres:16-alpine` container per scenario, bound to
an OS-assigned loopback port. Image ID and exact `SELECT version()` are retained.
`PHANTOM_PARITY_POSTGRES_IMAGE` may pin a different tag/digest for an experiment.
For CI, `PHANTOM_PARITY_PGHOST`, `PHANTOM_PARITY_PGPORT` (5432),
`PHANTOM_PARITY_PGUSER` (postgres), and required `PHANTOM_PARITY_PGPASSWORD` select
an **explicit test-only service with CREATEDB permission**. Each flow creates and
drops only its random `parity_<uuid>` database. Never point these test-service
variables at production. Ambient `PG*`, `POSTGRES_*`, `PHANTOM_POSTGRES_*`,
`JELLYFIN_*` and telemetry configuration is not used as a connection default.

## What is proved

`playback_db.py` executes standard-SQL upserts on the explicitly selected backend;
Jellyfin's own metadata inspection remains SQLite. PostgreSQL mode never opens a
Phantom SQLite connection: it rejects any `phantom.db`, checks `current_database()`
against the owned DB name and reads `phantom_schema_meta`. The plugin itself must
create the schema, populate discovery metadata, read seeded candidates, run native
materialisation, and persist `materialised_state` in that same PostgreSQL DB.
A candidate that silently selects SQLite fails before fixtures can mask it.

35/36 now share `playback-scenarios.py`, replacing the SQLite-only shell bodies
rather than leaving a second weaker implementation. Preserved assertions:

| Original subject | Shared rig assertion |
| --- | --- |
| discovery, browse | >=3 movies / >=2 series written by real discovery task |
| existing movie | TMDB Bravo name, no orphan/phantom tag, best HDR variant, probed source, real stream, resume |
| phantom movie/episode | browse runtime, phantom tag, exactly one GUID-shaped source, RequiresOpening, correct OpenToken, no splash path |
| native auto-open | AutoOpenLiveStream, exactly one final File source, real path, probed streams |
| materialisation | PostgreSQL state row; stable Jellyfin item ID; refreshed path/tags; nonzero runtime; real stream; Continue Watching |
| persisted metadata | BaseItems external movie/episode ID, exact real path and nonzero runtime |
| source replacement | alternate exposed by API; RejectCurrent success; new stub; operator_rejected failure; new playable source/bytes |
| TV navigation | native season type/year/overview and 8/1/7 availability summary; 8 native children |
| TV badges | series **and season** omitted; episode Phantom |
| second episode play | final File source, GUID ID, probed streams, nonempty real stream again |

Fixtures additionally populate `source_candidates` for the seeded available
movie/episode: current browse pruning intentionally hides previously-assessed
empty-candidate rows. This is a valid available fixture, not a relaxation of the
browse assertions. No application code or database schema changes are needed.
Deterministic TMDB/gostream mocks replace external discovery/download services,
not the Jellyfin/plugin playback, probe, claim/worker or persistence path.

## Isolation, evidence, CI

Every run owns `artifacts/playback-parity-<random>/`, fresh synthetic startup-wizard
user `a`, distinct ports, process groups, and PostgreSQL databases. No copied user
seed, global `/tmp` filenames, user-systemd units, broad `pkill`, production DB,
image deployment or blue/green flip. SIGINT/SIGTERM and failure paths unwind owned
processes/DBs; successful cleanup writes a receipt. A port collision fails closed,
never stops whoever owns that port. Logs/results are retained; data/config/caches
are removed. Only explicitly retained build trees survive.

Evidence contains build source/patch hashes, FFmpeg identity, loaded plugin/server
versions, candidate/deployed DLL hashes, all server assembly hashes, PostgreSQL
identity **without password**, discovery/native-open/badge/persistence responses,
4096 real stream bytes per assertion, scenario logs/results, and cleanup receipts.
The workflow `.gitea/workflows/postgres-playback-parity.yaml` runs on the Gitea
`k8s` runner with a PostgreSQL service; no optional skip/continue-on-error. It
uploads evidence even on failure and runs for main, PRs, manual dispatch and the
implementation branch. It does not build or flip a production image.

No operator deployment steps are needed: this changes test infrastructure only.

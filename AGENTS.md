# AGENTS.md

Guidance for AI coding agents working in this repo. Humans:
read `README.md`, `PLAN.md`, `CHANGELOG.md` first — those are
authoritative project docs. This file translates them into
conventions an agent needs to operate session-to-session
without re-deriving them.

## Operator hand-off rule (always)

When finishing a change the operator will install or test, **always
end the message with an explicit, ordered list of operator steps**.
This includes anything not handled by `./install.sh` alone:

- DB migrations or repair scripts (e.g. `sudo /tmp/foo-repair.sh`).
- Stopping or restarting services (`gostream.service`, `jellyfin.service`).
- Triggering scheduled tasks from the dashboard (e.g.
  Suggestions / Refresh Library / Validate Vault).
- Re-running install with non-default flags (`--build`,
  `--no-gostream`).
- Anything that touches `/var/lib/jellyfin`, `/var/gostream`, or
  root podman storage.

The operator should never have to infer whether "run the install
script" is enough or whether they also need to run Suggestions, or
wait for a periodic re-bind cycle, or `chown` something. Make it
explicit, every time, even when the steps feel obvious.

If the change requires *no* operator action beyond the next install,
say so explicitly: "No operator steps needed; `./install.sh` is
sufficient." Silence on this question costs the operator time.

## Read first

- `PLAN.md` — milestone tracker, design decisions, the
  source of truth for "what does done mean for this PR."
- `CHANGELOG.md` — what shipped in each release.
- `README.md` — user-facing description, install, usage.
- `docs/agents/testing.md` — **required reading before
  running, or asking the operator to run, any live test.**
  The repo has a dedicated test rig at `/tmp/jf-test/`
  that you drive yourself; do not ask the operator to
  copy DBs, run SQL, or restart Jellyfin.

## Project shape

- C# Jellyfin 10.11.x plugin. Target framework `net9.0`.
- Source: `src/Jellyfin.Plugin.PhantomLibrary/`
- Unit tests: `tests/Jellyfin.Plugin.PhantomLibrary.Tests/`
  — run with `dotnet test`.
- Live integration tests: the rig in `docs/agents/testing.md`.
- Plugin DB: SQLite at
  `/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db`
  on the operator's box, cloned into the rig per test.
- Companion service: `gostream` (separate repo,
  `phantom-library/api-add` and `phantom-library/vault-mode`
  branches). Local instance on `:9080` (API) / `:8090`
  (diagnostics).

## Build

```bash
dotnet build -c Release
```

Output DLL:
`src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll`

## Test

### Unit tests

```bash
dotnet test
```

Always green before opening a PR. If you need to weaken a
test to make a change pass, do not — fix the change.

### Live integration tests

**Read `docs/agents/testing.md` first.** Short version:

- Operator does not want to be in the loop for routine test
  cycles. You have read access to their Jellyfin DBs and a
  rig directory at `/tmp/jf-test/`.
- Spin up your own Jellyfin instance from a clone of the
  prod DB on port `:18096` (production owns `:8096`; do
  not bind it).
- Drive it via REST with a pre-injected API key. Inspect
  the rig's SQLite DBs directly to verify behaviour. Tear
  down. Repeat.
- The process-lifecycle constraint matters: the tool's
  pgroup teardown kills backgrounded Jellyfin between
  bash calls. The entire test
  (start → wait → drive → inspect → kill) must run inside
  a single bash invocation. The doc explains the pattern
  and ships a working `run-test.sh`.

The only legitimate reasons to ask the operator about
testing:

- A required file under `/var/lib/jellyfin/...` is no
  longer world-readable (operator needs to re-`chmod`).
- The `dotnet` runtime or Jellyfin server binary on the
  box has moved or changed version.
- A genuine environmental blocker the doc does not cover
  — and then update the doc once resolved.

Do **not** ask the operator to:

- Copy a DB for you.
- Run a SQL query for you.
- Restart their production Jellyfin.
- Tell you what's in `phantom.db` — read it yourself from
  the rig clone.
- Run `dotnet test` for you.
- Tail a log file for you.

## Coding conventions

- Match the surrounding style. The codebase uses standard
  .NET naming (`PascalCase` types/methods, `_camelCase`
  private fields, `var` where the type is obvious).
- Plugin log categories are `PhantomLibrary` and
  `Phantom.<Subsystem>` (e.g. `Phantom.SeriesIngestor`).
- New scheduled tasks register through Jellyfin's
  `IScheduledTask`; expose IDs as constants so they can be
  triggered via `POST /ScheduledTasks/Running/{id}` from
  the test rig.
- SQLite access for `phantom.db` goes through the existing
  repository abstractions in
  `src/Jellyfin.Plugin.PhantomLibrary/Data/`. Don't open
  raw connections in feature code.
- For Jellyfin DB inspection in tests, raw `sqlite3` CLI
  against the rig clone is fine and expected — you are
  reading, not writing.

## PR hygiene

- Update `CHANGELOG.md` under the unreleased section for
  any user-visible change.
- Update `PLAN.md`'s status table if you complete or move
  a milestone.
- Note tradeoffs and deviations in the PR description per
  the project's `SYSTEM.md` rules (no silent shortcuts,
  no silent stops).
- Bump the plugin version in `manifest.json` and the
  plugin `csproj` together — they must match the directory
  name used by Jellyfin to load the DLL
  (`Jellyfin.Plugin.PhantomLibrary_<version>/`). The test
  rig assumes `0.1.0.0`; bump it everywhere if you change
  it.

## Quick reference

| What | Where |
| --- | --- |
| Testing procedure | `docs/agents/testing.md` |
| Plan + milestones | `PLAN.md` |
| Release notes | `CHANGELOG.md` |
| User docs | `README.md` |
| Plugin source | `src/Jellyfin.Plugin.PhantomLibrary/` |
| Unit tests | `tests/Jellyfin.Plugin.PhantomLibrary.Tests/` |
| Test rig | `/tmp/jf-test/` (ephemeral, rebuild per session) |
| Prod Jellyfin DB | `/var/lib/jellyfin/data/jellyfin.db` (read-only) |
| Prod plugin DB | `/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db` (read-only) |
| gostream config | `/etc/gostream/config.json` (TMDB key lives here) |

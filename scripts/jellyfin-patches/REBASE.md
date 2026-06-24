# Rebasing the Jellyfin patches

The patches in this directory apply against the exact Jellyfin version
installed on the operator box: tag `v10.11.9` (base SHA `e83a7e62f2`,
"Bump version to 10.11.9"). When that source tree drifts (you pulled
upstream, or the operator's clone diverged), `install.sh --build`
aborts with a "does not apply cleanly" error.

**Why v10.11.9 exactly** rather than release-10.11.z head or master:
the operator's Jellyfin runtime is 10.11.9 on net9.0. A patch built
from release-10.11.z head (currently 10.11.11) compiles but crashes at
startup when copied into the 10.11.9 runtime because its assemblies
reference `MediaBrowser.Common, Version=10.11.11.0`. Master targets
net10.0 and is an even larger runtime mismatch. The patched
`MediaBrowser.Controller.dll` and `Jellyfin.LiveTv.dll` must match the
operator's installed runtime assembly version (`10.11.9.0`) so the
plugin's `IChannelItemRefreshManager` reference resolves without
assembly-load failure. If the operator upgrades Jellyfin, rebase the
patches against the exact installed tag and rebuild all patched DLLs.

Local SDK note: this project's `jellyfin/global.json` requires SDK
9.0.0 with `rollForward: latestMinor`. If your machine only has the
10.0.x SDK installed, temporarily overwrite `global.json` to use
`rollForward: latestMajor` (do NOT commit that change to jellyfin/;
it would land in the next exported patch). `install.sh --build`
assumes the operator's box has the matching 9.x SDK from their
Jellyfin install.

To rebase:

1. **Identify the current jellyfin/ HEAD.**

   ```bash
   git -C jellyfin log -1 --oneline
   ```

2. **Reset jellyfin/ to a clean state.** All current changes in the
   working tree get blown away — confirm via `git -C jellyfin status`
   that nothing local matters first.

   ```bash
   git -C jellyfin reset --hard v10.11.9
   git -C jellyfin clean -fd
   ```

3. **Apply the existing patches via `git am`** so conflicts present
   as standard rebase conflicts.

   ```bash
   cd jellyfin
   git am ../scripts/jellyfin-patches/*.patch
   ```

   For each conflict:

   - `git status` shows the conflicted files.
   - Resolve the conflict (inspect the surrounding code in the new
     master; the patch's intent — additive sibling interface +
     wrapper-around-existing-method — should still apply, possibly at
     a different line).
   - `git add <resolved files>` and `git am --continue`.

   If a patch becomes impossible to apply (the affected method was
   refactored out of existence upstream), the rebase is no longer
   mechanical and the patch's design needs re-evaluation. Stop and
   ask the operator.

4. **Re-run the affected tests/builds.**

   ```bash
   cd jellyfin
   dotnet test tests/Jellyfin.LiveTv.Tests/ -p:AllowMissingPrunePackageData=true
   dotnet build -c Release Jellyfin.Server/Jellyfin.Server.csproj
   ```

   All `ChannelManagerRefreshTests.*` cases must pass, and the server
   build must compile the patched item-action API.

5. **Re-export the patches.** Determine how many commits the rebased
   series produced (currently 5 unless you intentionally restructured):

   ```bash
   cd jellyfin
   git format-patch -5 -o ../scripts/jellyfin-patches/
   ```

   The output filenames stay numbered `0001-`/`0002-`/`0003-` —
   `install.sh` applies them in lexicographic order.

6. **Delete the stale `.patch` files** that the new `format-patch`
   replaced (compare `git status` in the repo root to confirm names
   match; if the old patches had identical names they're overwritten).

7. **Commit the rebased patches** in the phantom-library repo, with
   a message identifying the new base SHA:

   ```bash
   git -C /home/spencer/git-repos/spencerharmon/phantom-library add scripts/jellyfin-patches/
   git -C /home/spencer/git-repos/spencerharmon/phantom-library commit \
     -m "build: rebase Jellyfin patches onto $(git -C jellyfin rev-parse --short HEAD)"
   ```

8. **Verify install.sh succeeds end-to-end** on a fresh clone of
   `jellyfin/` (reset to the new base SHA first), confirming the
   rebased patches apply cleanly via the `install.sh --build` path.

## Upstream PR (deferred)

If/when the patches land upstream (Phase 8 of
`docs/plans/channel-handoff.md`), delete this directory entirely and
remove the `patches_dir` block from `install.sh`. See the plan's
Phase 8 § "Stage 8.5 — Post-merge cleanup" for the full cleanup
checklist.

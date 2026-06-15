# Rebasing the Jellyfin patches

The patches in this directory apply against a specific commit of
`jellyfin/jellyfin`. When that source tree drifts (you pulled
upstream, or the operator's clone diverged), `install.sh --build`
aborts with a "does not apply cleanly" error.

To rebase:

1. **Identify the current jellyfin/ HEAD.**

   ```bash
   git -C jellyfin log -1 --oneline
   ```

2. **Reset jellyfin/ to a clean state.** All current changes in the
   working tree get blown away — confirm via `git -C jellyfin status`
   that nothing local matters first.

   ```bash
   git -C jellyfin reset --hard origin/master   # or whatever upstream
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

4. **Re-run the affected test class.**

   ```bash
   cd jellyfin
   dotnet test tests/Jellyfin.LiveTv.Tests/ -p:AllowMissingPrunePackageData=true
   ```

   All eight `ChannelManagerRefreshTests.*` cases must pass.

5. **Re-export the patches.** Determine how many commits the rebased
   series produced (should still be 3 unless you intentionally
   restructured):

   ```bash
   cd jellyfin
   git format-patch -3 -o ../scripts/jellyfin-patches/
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

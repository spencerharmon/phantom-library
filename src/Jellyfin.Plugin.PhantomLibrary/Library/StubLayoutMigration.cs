using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// One-shot migration: renames every Virtual phantom stub on disk from
/// the legacy <c>__phantom_tmdb&lt;id&gt;</c> filename-sentinel scheme to
/// the Jellyfin-native <c>[tmdbid-&lt;id&gt;]</c> path-token scheme, and
/// updates the corresponding BaseItem.Path so Jellyfin keeps pointing at
/// the new location. Records completion in
/// <c>plugin_meta.stub_layout_v1_complete</c> so subsequent startups
/// no-op. Idempotent at the per-row level: a row already on the new
/// format is skipped.
///
/// Runs in the background from <see cref="StartAsync"/> so plugin
/// startup is never blocked. Exceptions are caught and logged; the
/// hosted-service contract is never violated.
/// </summary>
internal sealed class StubLayoutMigration : IHostedService
{
    internal const string MarkerKey = "stub_layout_v1_complete";

    private readonly ILibraryManager _libraryManager;
    private readonly IPhantomStubManager _stubs;
    private readonly PhantomDb _db;
    private readonly ILogger<StubLayoutMigration> _logger;

    public StubLayoutMigration(
        ILibraryManager libraryManager,
        IPhantomStubManager stubs,
        PhantomDb db,
        ILogger<StubLayoutMigration> logger)
    {
        _libraryManager = libraryManager;
        _stubs = stubs;
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => SafeRunAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SafeRunAsync(CancellationToken ct)
    {
        try
        {
            // Wait for the library + stub subsystems to settle (mirrors
            // PhantomBootstrapService's 5s grace period).
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown; fine.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StubLayoutMigration] migration failed");
        }
    }

    /// <summary>
    /// Internal entry point. Public so tests can drive the migration
    /// synchronously without waiting on the StartAsync background task.
    /// </summary>
    internal async Task<MigrationSummary> RunAsync(CancellationToken ct)
    {
        var summary = new MigrationSummary();

        var existing = await _db.GetMetaAsync(MarkerKey, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "[StubLayoutMigration] already complete at {When}; skipping", existing);
            summary.AlreadyComplete = true;
            return summary;
        }

        var rows = await _db.ListItemsByStateAsync(
            PhantomItemState.Virtual.ToString(), ct).ConfigureAwait(false);
        summary.Scanned = rows.Count;
        _logger.LogInformation(
            "[StubLayoutMigration] starting; {N} virtual rows to inspect", rows.Count);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(row.StubPath))
            {
                continue;
            }

            if (!PhantomPathUtilities.IsLegacyStubPath(row.StubPath))
            {
                // Already on new format (or unrecognised). Idempotent skip.
                summary.AlreadyNewFormat++;
                continue;
            }

            try
            {
                await MigrateRowAsync(row, summary, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                summary.Failed++;
                _logger.LogWarning(ex,
                    "[StubLayoutMigration] failed for item {Id} ({Path})",
                    row.ItemGuid, row.StubPath);
            }
        }

        _logger.LogInformation(
            "[StubLayoutMigration] done: scanned={S} migrated={M} alreadyNew={N} skippedConflict={C} skippedNoBaseItem={O} failed={F}",
            summary.Scanned, summary.Migrated, summary.AlreadyNewFormat,
            summary.SkippedConflict, summary.SkippedNoBaseItem, summary.Failed);

        if (summary.Failed == 0)
        {
            // Orphan rows (missing BaseItem) don't block marker write —
            // they will never recover from a re-run. Genuine failures
            // (IOException etc.) do block the marker so the next start
            // retries them.
            var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            await _db.SetMetaAsync(MarkerKey, now, ct).ConfigureAwait(false);
            summary.MarkerSet = true;
        }
        else
        {
            _logger.LogWarning(
                "[StubLayoutMigration] {F} failures; marker NOT set, will retry on next startup",
                summary.Failed);
        }

        return summary;
    }

    private async Task MigrateRowAsync(PhantomItemRow row, MigrationSummary summary, CancellationToken ct)
    {
        var oldPath = row.StubPath!;
        var baseItem = TryGetItem(row.ItemGuid);
        if (baseItem is null)
        {
            _logger.LogInformation(
                "[StubLayoutMigration] no BaseItem for {Id} ({Path}); orphan row, skipping",
                row.ItemGuid, oldPath);
            summary.SkippedNoBaseItem++;
            return;
        }

        // Title: prefer BaseItem.Name if it's not the ugly stem; else
        // reverse-derive from the old filename / dirname.
        var title = ResolveTitle(baseItem, oldPath);
        var year = baseItem.ProductionYear;
        var tmdbId = row.TmdbId
            ?? PhantomPathUtilities.TryParseTmdbId(oldPath)
            ?? throw new InvalidOperationException(
                $"row {row.ItemGuid} has no tmdb id and cannot be parsed from {oldPath}");

        var kind = row.Type.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? PhantomMediaKind.Series
            : PhantomMediaKind.Movie;

        string newStubPath;
        if (kind == PhantomMediaKind.Series)
        {
            var (newSeriesDir, _, _) = _stubs.DeriveSeriesStubPaths(title, year, tmdbId);
            if (string.Equals(oldPath, newSeriesDir, StringComparison.Ordinal))
            {
                summary.AlreadyNewFormat++;
                return;
            }

            if (Directory.Exists(newSeriesDir))
            {
                _logger.LogWarning(
                    "[StubLayoutMigration] destination already exists; skipping {Old} -> {New}",
                    oldPath, newSeriesDir);
                summary.SkippedConflict++;
                return;
            }

            if (Directory.Exists(oldPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newSeriesDir)!);
                Directory.Move(oldPath, newSeriesDir);
            }
            else
            {
                _logger.LogWarning(
                    "[StubLayoutMigration] series dir missing on disk; will still update DB+BaseItem for {Id}",
                    row.ItemGuid);
            }
            newStubPath = newSeriesDir;
        }
        else
        {
            var newFile = ComputeNewMoviePath(oldPath, title, year, tmdbId);
            if (string.Equals(oldPath, newFile, StringComparison.Ordinal))
            {
                summary.AlreadyNewFormat++;
                return;
            }

            if (File.Exists(newFile))
            {
                _logger.LogWarning(
                    "[StubLayoutMigration] destination already exists; skipping {Old} -> {New}",
                    oldPath, newFile);
                summary.SkippedConflict++;
                return;
            }

            if (File.Exists(oldPath) || new FileInfo(oldPath).Exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
                // File.Move handles symlinks (moves the link itself, not the target).
                File.Move(oldPath, newFile);
            }
            else
            {
                _logger.LogWarning(
                    "[StubLayoutMigration] symlink missing on disk; will still update DB+BaseItem for {Id}",
                    row.ItemGuid);
            }
            newStubPath = newFile;
        }

        // Update BaseItem.
        baseItem.Path = newStubPath;
        baseItem.IsLocked = true; // preserve spike cruft
        try
        {
            await _libraryManager.UpdateItemAsync(
                baseItem, baseItem.GetParent(), ItemUpdateType.MetadataImport, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[StubLayoutMigration] UpdateItemAsync failed for {Id}; DB will still be updated",
                row.ItemGuid);
        }

        // Update DB.
        await _db.UpsertPhantomItemAsync(row.ItemGuid, row with { StubPath = newStubPath, LastTouched = DateTimeOffset.UtcNow }, ct)
            .ConfigureAwait(false);

        summary.Migrated++;
        _logger.LogInformation(
            "[StubLayoutMigration] migrated {Id}: {Old} -> {New}",
            row.ItemGuid, oldPath, newStubPath);
    }

    private static string ComputeNewMoviePath(string oldPath, string title, int? year, int tmdbId)
    {
        // Stub manager owns layout; cheat via its derivation by
        // borrowing the splash extension from the old path's extension.
        var ext = (Path.GetExtension(oldPath) ?? string.Empty).TrimStart('.');
        if (string.IsNullOrEmpty(ext)) ext = "mp4";
        var dir = Path.GetDirectoryName(oldPath)!;
        var safe = PhantomStubManager.DisplaySanitize(title);
        if (string.IsNullOrEmpty(safe)) safe = "Untitled";
        var stem = year.HasValue
            ? $"{safe} ({year.Value.ToString(CultureInfo.InvariantCulture)})"
            : safe;
        var fileName = $"{stem} {PhantomStubManager.TmdbIdTokenPrefix}{tmdbId.ToString(CultureInfo.InvariantCulture)}{PhantomStubManager.TmdbIdTokenSuffix}.{ext}";
        return Path.Combine(dir, fileName);
    }

    private static string ResolveTitle(BaseItem item, string oldPath)
    {
        var name = item.Name;
        if (!string.IsNullOrWhiteSpace(name) && !PhantomPathUtilities.IsPhantomStubPath(name))
        {
            return name!;
        }

        // Reverse-derive from the old sanitized filename / dirname stem.
        var leaf = Path.GetFileNameWithoutExtension(
            oldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        // Strip the __phantom_tmdb<id> suffix.
        var idx = leaf.IndexOf(PhantomPathUtilities.LegacySentinel, StringComparison.Ordinal);
        if (idx >= 0) leaf = leaf.Substring(0, idx);
        // Underscores back to spaces.
        return leaf.Replace('_', ' ').Trim();
    }

    private BaseItem? TryGetItem(Guid id)
    {
        try { return _libraryManager.GetItemById(id); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StubLayoutMigration] GetItemById threw for {Id}", id);
            return null;
        }
    }

    internal sealed class MigrationSummary
    {
        public int Scanned { get; set; }
        public int Migrated { get; set; }
        public int AlreadyNewFormat { get; set; }
        public int SkippedConflict { get; set; }
        public int SkippedNoBaseItem { get; set; }
        public int Failed { get; set; }
        public bool MarkerSet { get; set; }
        public bool AlreadyComplete { get; set; }
    }
}

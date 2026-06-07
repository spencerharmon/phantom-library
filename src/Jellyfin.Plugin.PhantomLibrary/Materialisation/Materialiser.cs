using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// End-to-end materialisation pipeline. See PLAN.md §Materialisation flow.
/// </summary>
public sealed class Materialiser : IMaterialiser
{
    private static readonly TimeSpan FuseSettlePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FuseSettleMax = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MagnetCacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnavailableRetryAfter = TimeSpan.FromHours(24);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IEnumerable<IIndexerClient> _indexers;
    private readonly IGostreamClient _gostream;
    private readonly QualityScorer _scorer;
    private readonly PhantomDb _db;
    private readonly Jellyfin.Plugin.PhantomLibrary.Library.IPhantomStubManager _stubs;
    private readonly Jellyfin.Plugin.PhantomLibrary.Clients.ITmdbClient? _tmdb;
    private readonly ILogger<Materialiser> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    /// <summary>Production DI ctor. ITmdbClient is required so the
    /// IMDB-id enrichment fallback can fire when a phantom row lacks Imdb
    /// (Torrentio and several other indexers refuse non-imdb queries).</summary>
    public Materialiser(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IEnumerable<IIndexerClient> indexers,
        IGostreamClient gostream,
        QualityScorer scorer,
        PhantomDb db,
        Jellyfin.Plugin.PhantomLibrary.Library.IPhantomStubManager stubs,
        Jellyfin.Plugin.PhantomLibrary.Clients.ITmdbClient tmdb,
        ILogger<Materialiser> logger)
        : this(libraryManager, providerManager, indexers, gostream, scorer, db, stubs, logger,
               () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
               tmdb)
    {
    }

    /// <summary>Internal ctor for unit tests; tmdb optional so tests
    /// that don't exercise IMDB enrichment don't need to mock it.</summary>
    internal Materialiser(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IEnumerable<IIndexerClient> indexers,
        IGostreamClient gostream,
        QualityScorer scorer,
        PhantomDb db,
        Jellyfin.Plugin.PhantomLibrary.Library.IPhantomStubManager stubs,
        ILogger<Materialiser> logger,
        Func<PluginConfiguration> configProvider,
        Jellyfin.Plugin.PhantomLibrary.Clients.ITmdbClient? tmdb = null)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _indexers = indexers;
        _gostream = gostream;
        _scorer = scorer;
        _db = db;
        _stubs = stubs;
        _tmdb = tmdb;
        _logger = logger;
        _configProvider = configProvider;
    }

    public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;

    private void Fire(Guid id, MaterialisationLifecyclePhase phase, MaterialisationOutcome? outcome = null)
    {
        try
        {
            LifecycleChanged?.Invoke(this, new MaterialisationLifecycleEvent(id, phase, outcome));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LifecycleChanged handler threw for {Id} phase {Phase}", id, phase);
        }
    }

    public async Task<MaterialisationOutcome> MaterialiseAsync(
        Guid jellyfinItemId, MaterialiseTrigger trigger, CancellationToken ct)
    {
        Fire(jellyfinItemId, MaterialisationLifecyclePhase.Started);
        MaterialisationOutcome? outcome = null;
        try
        {
            outcome = await MaterialiseCoreAsync(jellyfinItemId, trigger, ct).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            Fire(jellyfinItemId, MaterialisationLifecyclePhase.Finished, outcome);
        }
    }

    private async Task<MaterialisationOutcome> MaterialiseCoreAsync(
        Guid jellyfinItemId, MaterialiseTrigger trigger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? indexerUsed = null;
        string? infoHashUsed = null;

        try
        {
            var item = _libraryManager.GetItemById(jellyfinItemId);
            if (item is null)
            {
                return await FailAsync(sw, jellyfinItemId, trigger, "item not found", indexerUsed, infoHashUsed, ct)
                    .ConfigureAwait(false);
            }

            // Series-level materialisation is intentionally unsupported. A Series
            // is a container; episodes are the materialisation unit. The autopilot
            // (M8 §5) ensures individual Episodes are pre-materialised.
            if (item is Series)
            {
                const string reason = "Series-level materialisation is not supported; materialise individual Episodes instead (Series is the container).";
                _logger.LogInformation("Materialise {Id}: {Reason}", jellyfinItemId, reason);
                await LogAsync(sw, jellyfinItemId, trigger, "error", reason, indexerUsed, infoHashUsed, ct)
                    .ConfigureAwait(false);
                return new MaterialisationOutcome { Status = MaterialisationStatus.Error, Error = reason };
            }

            if (item is Episode ep0)
            {
                if (ep0.IndexNumber is null || ep0.ParentIndexNumber is null)
                {
                    var reason = "Episode missing IndexNumber/ParentIndexNumber; cannot resolve TV torrent";
                    return await FailAsync(sw, jellyfinItemId, trigger, reason, indexerUsed, infoHashUsed, ct)
                        .ConfigureAwait(false);
                }

                var sImdb = ResolveSeriesImdb(ep0);
                if (string.IsNullOrWhiteSpace(sImdb))
                {
                    var reason = "Series has no IMDB id; cannot resolve TV torrent";
                    _logger.LogWarning("Materialise {Id}: {Reason}", jellyfinItemId, reason);
                    return await FailAsync(sw, jellyfinItemId, trigger, reason, indexerUsed, infoHashUsed, ct)
                        .ConfigureAwait(false);
                }
            }

            // M11 #5 downstream: BaseItem.ProviderIds is often empty
            // after the scanner re-resolves a phantom stub. Fall back
            // to phantom_items.tmdb_id so materialise can still run.
            var resolved = await ResolveProviderIdsAsync(item, ct).ConfigureAwait(false);
            if (resolved.Tmdb is not null)
            {
                item.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!item.ProviderIds.ContainsKey("Tmdb"))
                {
                    item.ProviderIds["Tmdb"] = resolved.Tmdb;
                }
                if (resolved.Imdb is not null && !item.ProviderIds.ContainsKey("Imdb"))
                {
                    item.ProviderIds["Imdb"] = resolved.Imdb;
                }
            }

            if (!TryExtractIdentifiers(item, out var ids))
            {
                if (resolved.Tmdb is null)
                {
                    return await FailAsync(sw, jellyfinItemId, trigger,
                        $"item {jellyfinItemId} has no TMDB id in BaseItem.ProviderIds OR phantom_items row — Suggestions may have lost the id during scan; check phantom.db",
                        indexerUsed, infoHashUsed, ct).ConfigureAwait(false);
                }

                return await FailAsync(sw, jellyfinItemId, trigger,
                    "item lacks TMDB/IMDB provider ids — cannot materialise", indexerUsed, infoHashUsed, ct)
                    .ConfigureAwait(false);
            }

            // Step 2.5: enrich IMDB from TMDB if missing. Torrentio (and
            // some other indexers) require an IMDB id. Phantom rows often
            // only have a Tmdb id because the user discovered them via
            // TMDB trending/discover; resolve to imdb via TMDB external_ids.
            // Cached by tmdb-cache, so this is cheap on repeat plays.
            if (string.IsNullOrWhiteSpace(ids.Imdb) && ids.Tmdb is int tid && _tmdb is not null)
            {
                try
                {
                    string? resolvedImdb = ids.Type switch
                    {
                        "movie" => await _tmdb.GetImdbIdForMovieAsync(tid, ct).ConfigureAwait(false),
                        "series" => await _tmdb.GetImdbIdForSeriesAsync(tid, ct).ConfigureAwait(false),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(resolvedImdb))
                    {
                        ids.Imdb = resolvedImdb;
                        _logger.LogDebug(
                            "Materialise {Id}: enriched IMDB={Imdb} from TMDB={Tmdb}",
                            jellyfinItemId, resolvedImdb, tid);
                        // Also persist back to the BaseItem so subsequent
                        // materialise calls (or other plugins) see it.
                        item.ProviderIds["Imdb"] = resolvedImdb;
                        try
                        {
                            await _libraryManager.UpdateItemAsync(
                                item, item.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                        }
                        catch (Exception ux)
                        {
                            _logger.LogDebug(ux,
                                "Materialise {Id}: UpdateItemAsync for IMDB-stamp failed (non-fatal)", jellyfinItemId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Materialise {Id}: TMDB->IMDB enrichment failed for tmdb={Tmdb}; proceeding without imdb",
                        jellyfinItemId, tid);
                }
            }

            var cfg = _configProvider();

            // Step 3: unavailable marker
            var unavailKey = new UnavailableKey(ids.Tmdb, ids.Imdb, ids.Type, ids.Season, ids.Episode);
            if (await _db.IsMarkedUnavailableAsync(unavailKey, ct).ConfigureAwait(false))
            {
                await LogAsync(sw, jellyfinItemId, trigger, "unavailable", null, indexerUsed, infoHashUsed, ct)
                    .ConfigureAwait(false);
                return new MaterialisationOutcome { Status = MaterialisationStatus.Unavailable };
            }

            // Step 4: magnet cache
            var presetName = cfg.QualityPreset.ToString();
            var cacheKey = new MagnetCacheKey(ids.Tmdb, ids.Imdb, ids.Type, ids.Season, ids.Episode, presetName);
            var cached = await _db.GetCachedMagnetAsync(cacheKey, ct).ConfigureAwait(false);

            string magnet;
            if (cached is not null)
            {
                magnet = cached.Magnet;
                indexerUsed = cached.Indexer;
                infoHashUsed = cached.InfoHash;
                _logger.LogDebug("Materialise {Id}: cache hit from {Indexer}", jellyfinItemId, cached.Indexer);
            }
            else
            {
                // Step 5: indexer chain
                var indexerQuery = new IndexerQuery
                {
                    Type = ids.Type,
                    // For episode searches, the indexer chain needs the *series* IMDB,
                    // not the episode's own (which is usually absent anyway).
                    Imdb = ids.Type == "episode" ? (ids.SeriesImdb ?? ids.Imdb) : ids.Imdb,
                    Tmdb = ids.Tmdb,
                    Title = ids.Title,
                    Year = ids.Year,
                    Season = ids.Season,
                    Episode = ids.Episode,
                    SeriesImdb = ids.SeriesImdb,
                };

                var candidates = new List<IndexerCandidate>();
                var anyEnabled = false;
                foreach (var client in _indexers)
                {
                    if (!client.IsEnabled) continue;
                    anyEnabled = true;
                    try
                    {
                        var batch = await client.SearchAsync(indexerQuery, ct).ConfigureAwait(false);
                        candidates.AddRange(batch);
                        if (batch.Count > 0)
                        {
                            // PLAN: fall back to next indexer only if current returned nothing.
                            break;
                        }
                    }
                    catch (IndexerAuthException ex)
                    {
                        _logger.LogWarning(ex, "Indexer {Name} auth failure", client.Name);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Indexer {Name} threw; continuing to next", client.Name);
                    }
                }

                if (!anyEnabled)
                {
                    return await FailAsync(sw, jellyfinItemId, trigger,
                        "no indexers configured", indexerUsed, infoHashUsed, ct).ConfigureAwait(false);
                }

                // Step 6: scorer
                var best = _scorer.PickBest(candidates, cfg.QualityPreset,
                    cfg.MinSeeders, cfg.MinSizeGb1080p, cfg.MinSizeGb4K);
                if (best is null)
                {
                    await _db.MarkUnavailableAsync(unavailKey, UnavailableRetryAfter, ct).ConfigureAwait(false);
                    await LogAsync(sw, jellyfinItemId, trigger, "unavailable",
                        "no candidate passed quality floors", indexerUsed, infoHashUsed, ct).ConfigureAwait(false);
                    return new MaterialisationOutcome { Status = MaterialisationStatus.Unavailable };
                }

                magnet = best.Magnet;
                indexerUsed = best.IndexerName ?? best.Source;
                infoHashUsed = best.InfoHash;

                // Step 7: cache the winner
                var entry = new MagnetCacheEntry
                {
                    Magnet = best.Magnet,
                    InfoHash = best.InfoHash,
                    Size = best.Size,
                    Seeders = best.Seeders,
                    Indexer = indexerUsed ?? "unknown-indexer",
                    CachedAt = DateTimeOffset.UtcNow,
                    Ttl = MagnetCacheTtl,
                    Source = trigger == MaterialiseTrigger.PreResolve ? "eager" : "user",
                };
                await _db.PutCachedMagnetAsync(cacheKey, entry, ct).ConfigureAwait(false);
            }

            // PreResolve: stop after cache write — never call gostream.
            if (trigger == MaterialiseTrigger.PreResolve)
            {
                await LogAsync(sw, jellyfinItemId, trigger, "success",
                    "pre-resolve: cached magnet only", indexerUsed, infoHashUsed, ct).ConfigureAwait(false);
                return new MaterialisationOutcome { Status = MaterialisationStatus.Success };
            }

            // Step 8: gostream add
            var addReq = new GostreamAddRequest
            {
                Type = ids.Type,
                Imdb = ids.Imdb,
                Tmdb = ids.Tmdb,
                Title = ids.Title,
                Year = ids.Year,
                Season = ids.Season,
                Episode = ids.Episode,
                SeriesImdb = ids.SeriesImdb,
                Magnet = magnet,
            };

            GostreamAddResult addResult;
            try
            {
                addResult = await _gostream.AddAsync(addReq, ct).ConfigureAwait(false);
            }
            catch (GostreamNoValidFilesException ex)
            {
                await _db.MarkUnavailableAsync(unavailKey, UnavailableRetryAfter, ct).ConfigureAwait(false);
                await LogAsync(sw, jellyfinItemId, trigger, "unavailable",
                    ex.Message, indexerUsed, infoHashUsed, ct).ConfigureAwait(false);
                return new MaterialisationOutcome { Status = MaterialisationStatus.Unavailable, Error = ex.Message };
            }
            catch (GostreamTimeoutException ex)
            {
                // Transient — do NOT mark unavailable.
                await LogAsync(sw, jellyfinItemId, trigger, "error",
                    ex.Message, indexerUsed, infoHashUsed, ct).ConfigureAwait(false);
                return new MaterialisationOutcome { Status = MaterialisationStatus.Error, Error = ex.Message };
            }

            // Step 9: poll for FUSE-path settle
            await WaitForFusePathAsync(addResult.FusePath, ct).ConfigureAwait(false);

            // Step 10: promote
            await PromoteItemAsync(item, addResult.FusePath, ct).ConfigureAwait(false);

            // Step 11: log + return
            await LogAsync(sw, jellyfinItemId, trigger,
                addResult.AlreadyExisted ? "duplicate" : "success",
                null, indexerUsed, infoHashUsed, ct).ConfigureAwait(false);

            await _db.UpsertPhantomItemAsync(jellyfinItemId, new PhantomItemRow
            {
                TmdbId = ids.Tmdb,
                ImdbId = ids.Imdb,
                Type = ids.Type,
                State = PhantomItemState.Materialised,
                FirstSeen = DateTimeOffset.UtcNow,
                LastTouched = DateTimeOffset.UtcNow,
                StubPath = addResult.StubPath,
                FusePath = addResult.FusePath,
                MaterialisedAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);

            return new MaterialisationOutcome
            {
                Status = addResult.AlreadyExisted ? MaterialisationStatus.Duplicate : MaterialisationStatus.Success,
                FusePath = addResult.FusePath,
                StubPath = addResult.StubPath,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Materialise {Id} failed unexpectedly", jellyfinItemId);
            await LogAsync(sw, jellyfinItemId, trigger, "error", ex.Message, indexerUsed, infoHashUsed, ct)
                .ConfigureAwait(false);
            return new MaterialisationOutcome { Status = MaterialisationStatus.Error, Error = ex.Message };
        }
    }

    private async Task<MaterialisationOutcome> FailAsync(
        Stopwatch sw, Guid id, MaterialiseTrigger trigger, string error,
        string? indexer, string? hash, CancellationToken ct)
    {
        _logger.LogWarning("Materialise {Id} failed: {Err}", id, error);
        await LogAsync(sw, id, trigger, "error", error, indexer, hash, ct).ConfigureAwait(false);
        return new MaterialisationOutcome { Status = MaterialisationStatus.Error, Error = error };
    }

    private async Task LogAsync(
        Stopwatch sw, Guid id, MaterialiseTrigger trigger, string outcome,
        string? error, string? indexer, string? hash, CancellationToken ct)
    {
        try
        {
            await _db.LogMaterialisationAsync(new MaterialisationLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                ItemGuid = id,
                Trigger = TriggerToString(trigger),
                DurationMs = sw.ElapsedMilliseconds,
                Outcome = outcome,
                Error = error,
                Indexer = indexer,
                InfoHash = hash,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write materialisation_log entry for {Id}", id);
        }
    }

    private static string TriggerToString(MaterialiseTrigger t) => t switch
    {
        MaterialiseTrigger.Favourite => "favourite",
        MaterialiseTrigger.Play => "play",
        MaterialiseTrigger.Autopilot => "autopilot",
        MaterialiseTrigger.PreResolve => "pre-resolve",
        MaterialiseTrigger.Manual => "manual",
        _ => "unknown",
    };

    private async Task WaitForFusePathAsync(string fusePath, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + FuseSettleMax;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(fusePath))
            {
                return;
            }

            try
            {
                await Task.Delay(FuseSettlePollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        _logger.LogWarning(
            "FUSE path {Path} not visible after {Cap}; proceeding (gostream may need a moment)",
            fusePath, FuseSettleMax);
    }

    private async Task PromoteItemAsync(BaseItem item, string fusePath, CancellationToken ct)
    {
        // Capture the existing Path BEFORE mutation. If it points at a
        // phantom stub symlink, we will delete it after a successful
        // in-place update. Defensive sentinel check protects real gostream
        // files (or anything else) from accidental deletion.
        var oldPath = item.Path;

        // Reflectively confirm IsVirtualItem is writable on this Jellyfin
        // build. On 10.10.x it is; the defensive check is per PLAN's hard
        // rule so a downstream API change does not silently break promotion.
        var isVirtualProp = typeof(BaseItem).GetProperty(
            "IsVirtualItem", BindingFlags.Public | BindingFlags.Instance);
        var canMutateInPlace = isVirtualProp is not null && isVirtualProp.CanWrite;

        if (canMutateInPlace)
        {
            item.Path = fusePath;
            isVirtualProp!.SetValue(item, false);
            try
            {
                await _libraryManager.UpdateItemAsync(item, item.GetParent(),
                    ItemUpdateType.MetadataImport, ct).ConfigureAwait(false);
                _logger.LogDebug("Promoted item {Id} via in-place update", item.Id);

                if (IsPhantomStub(oldPath))
                {
                    try { await _stubs.DeleteAsync(oldPath!, ct).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "stub delete failed for {Path}", oldPath); }
                }
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "In-place UpdateItemAsync failed for {Id}; falling back to provider refresh",
                    item.Id);
            }
        }
        else
        {
            _logger.LogInformation(
                "BaseItem.IsVirtualItem not writable on this Jellyfin build; using provider refresh path");
        }

        var dir = new DirectoryService();
        _providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(dir)
        {
            ReplaceAllImages = false,
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
        }, RefreshPriority.High);
    }

    private static bool IsPhantomStub(string? path)
        => !string.IsNullOrEmpty(path)
            && path.Contains(Jellyfin.Plugin.PhantomLibrary.Library.PhantomStubManager.Sentinel, StringComparison.Ordinal);

    // Minimal IDirectoryService stub for MetadataRefreshOptions. Jellyfin
    // does not require a real on-disk service for QueueRefresh — it
    // re-resolves directory contents itself on the refresh thread.
    private sealed class DirectoryService : IDirectoryService
    {
        public FileSystemMetadata[] GetFileSystemEntries(string path) => Array.Empty<FileSystemMetadata>();
        public List<FileSystemMetadata> GetDirectories(string path) => new();
        public List<FileSystemMetadata> GetFiles(string path) => new();
        public FileSystemMetadata? GetFile(string path) => null;
        public FileSystemMetadata? GetDirectory(string path) => null;
        public FileSystemMetadata? GetFileSystemEntry(string path) => null;
        public IReadOnlyList<string> GetFilePaths(string path) => Array.Empty<string>();
        public IReadOnlyList<string> GetFilePaths(string path, bool clearCache, bool sort = false) => Array.Empty<string>();
        public bool IsAccessible(string path) => false;
    }

    private string? ResolveSeriesImdb(Episode ep)
    {
        string? s = null;
        try { s = ep.Series?.ProviderIds?.GetValueOrDefault("Imdb"); }
        catch (Exception ex) { _logger.LogDebug(ex, "ep.Series accessor threw"); }
        if (!string.IsNullOrWhiteSpace(s)) return s;

        try
        {
            var p = ep.GetParent();
            while (p is not null)
            {
                if (p is Series ser)
                {
                    var imdb = ser.ProviderIds?.GetValueOrDefault("Imdb");
                    if (!string.IsNullOrWhiteSpace(imdb)) return imdb;
                    break;
                }

                p = p.GetParent();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "parent walk threw");
        }

        try
        {
            if (ep.SeriesId != Guid.Empty)
            {
                var ser = _libraryManager.GetItemById(ep.SeriesId) as Series;
                return ser?.ProviderIds?.GetValueOrDefault("Imdb");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SeriesId lookup threw");
        }

        return null;
    }

    /// <summary>
    /// Resolves TMDB / IMDB provider ids for an item, falling back to
    /// the phantom_items row when BaseItem.ProviderIds has been stripped
    /// by the scanner (M11 #5 downstream).
    /// </summary>
    internal async Task<(string? Tmdb, string? Imdb, string? Type)> ResolveProviderIdsAsync(
        BaseItem item, CancellationToken ct)
    {
        if (item.ProviderIds is not null
            && item.ProviderIds.TryGetValue("Tmdb", out var tmdb)
            && !string.IsNullOrWhiteSpace(tmdb))
        {
            item.ProviderIds.TryGetValue("Imdb", out var imdb);
            string? kind = item switch
            {
                Movie => "movie",
                Episode => "episode",
                Series => "series",
                _ => null,
            };
            return (tmdb, string.IsNullOrWhiteSpace(imdb) ? null : imdb, kind);
        }

        var row = await _db.GetPhantomItemAsync(item.Id, ct).ConfigureAwait(false);
        if (row is not null && row.TmdbId is not null)
        {
            return (
                row.TmdbId.Value.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(row.ImdbId) ? null : row.ImdbId,
                row.Type);
        }

        return (null, null, null);
    }

    private bool TryExtractIdentifiers(BaseItem item, out ItemIdentifiers ids)
    {
        ids = default;
        string? imdb = null;
        int? tmdb = null;
        if (item.ProviderIds is not null)
        {
            item.ProviderIds.TryGetValue("Imdb", out imdb);
            if (item.ProviderIds.TryGetValue("Tmdb", out var tmdbStr)
                && int.TryParse(tmdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                tmdb = n;
            }
        }

        if (string.IsNullOrWhiteSpace(imdb) && tmdb is null)
        {
            return false;
        }

        string type;
        int? season = null;
        int? episode = null;
        string? seriesImdb = null;

        switch (item)
        {
            case Movie:
                type = "movie";
                break;
            case Episode ep:
                type = "episode";
                season = ep.ParentIndexNumber;
                episode = ep.IndexNumber;
                seriesImdb = ResolveSeriesImdb(ep);
                if (string.IsNullOrWhiteSpace(seriesImdb))
                {
                    // gostream's /api/library/add requires series_imdb for type=episode;
                    // refuse rather than ship a placeholder.
                    return false;
                }

                if (season is null || episode is null)
                {
                    return false;
                }

                break;
            case Series:
                // Series-level materialisation is not supported by design:
                // a Series is a container; episodes are the materialisation
                // unit. Materialiser surfaces this as an Error with the
                // documented reason. See PLAN.md §M8.
                return false;
            default:
                return false;
        }

        ids = new ItemIdentifiers
        {
            Imdb = string.IsNullOrWhiteSpace(imdb) ? null : imdb,
            Tmdb = tmdb,
            Type = type,
            Title = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            Season = season,
            Episode = episode,
            SeriesImdb = seriesImdb,
        };
        return true;
    }

    private struct ItemIdentifiers
    {
        public string? Imdb;
        public int? Tmdb;
        public string Type;
        public string Title;
        public int? Year;
        public int? Season;
        public int? Episode;
        public string? SeriesImdb;
    }
}

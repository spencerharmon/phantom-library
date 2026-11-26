using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.Swarm;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// ROI Priority 0.5 (dht-swarm-health): keeps a REAL DHT swarm measurement for
/// every available item's candidate infohash and publishes it to the browse UI.
///
/// Each tick (<see cref="PluginConfiguration.SwarmHealthIntervalSeconds"/>):
///   1. MEASURE — takes up to <see cref="PluginConfiguration.SwarmHealthBatchSize"/>
///      available items whose candidate has no measurement newer than
///      <see cref="PluginConfiguration.SwarmHealthRecheckHours"/> (movies first, by
///      relevance — bounded by intent, never a catalogue sweep), asks the swarm-health
///      service for each infohash's distinct DHT peers, stores the answers in
///      <c>swarm_health</c>, and immediately writes the just-measured titles' Peers
///      value through to their BaseItems (bounded by that same batch);
///   2. SYNC (p05-baseitem-writeback) — advances ONE bounded step of a resumable,
///      keyset-paged sweep over every persisted phantom movie/series BaseItem
///      (including gostream-only orphans), writing the Peers value
///      (<c>CriticRating</c>, see <see cref="SwarmHealthScore"/>) wherever it differs.
///      Each tick reads at most <see cref="PluginConfiguration.SwarmHealthSyncBatchSize"/>
///      rows through an index seek and resumes from a cursor persisted in
///      <c>plugin_meta</c>, so a restart/cancellation continues where it stopped and
///      the whole catalogue converges to a non-NULL score within one measurement
///      cycle. The sweep is LOCAL score backfill only: it reads <c>swarm_health</c>
///      and never asks the DHT service about anything — measurement admission stays
///      the intent-bounded step 1.
///
/// The sweep cursor is scoped to THIS server's library (<see cref="CursorKeyFor"/>,
/// keyed by the stable Jellyfin server id), because the rows it pages and writes live
/// in this server's own library DB while <c>plugin_meta</c> is shared by every server
/// on the same Phantom database (blue/green). The same per-sweep pass also tallies the
/// movie/series live/dead title counts that feed <c>phantom_swarm_health_titles</c>,
/// persisted with the cursor so a restart republishes the last completed sweep's
/// values without any full-table count.
///
/// Every <c>swarm_health</c> read here is bounded by a batch (per-batch TMDB-id IN
/// lookups); the results also feed <see cref="SwarmHealthCache"/>, which
/// <see cref="SwarmHealthMetadataProvider"/> reads to re-apply the score after a
/// core replace-all refresh — so there is no per-tick whole-table aggregate.
/// A BaseItem that fails to save is counted and skipped; it never stalls the sweep.
/// Disabled entirely while the service URL is empty.
/// </summary>
public sealed class SwarmHealthWorker : IHostedService, IDisposable
{
    /// <summary>
    /// <c>plugin_meta</c> key prefix of the per-library sweep cursor. The full key is
    /// <c>swarm_health_sync_cursor:&lt;target&gt;</c>; the unqualified legacy key
    /// <c>swarm_health_sync_cursor</c> carries no target and is never read — its
    /// continuation could belong to any server's library.
    /// </summary>
    internal const string SyncCursorMetaKeyPrefix = "swarm_health_sync_cursor:";

    /// <summary>A finished sweep restarts no sooner than this many ticks after it began.</summary>
    internal const int MinTicksBetweenSweeps = 10;

    /// <summary>
    /// The sweep's keyset partitions, in order: phantom movies (incl. orphan movies),
    /// phantom series, and orphan (gostream-only) series folders in the Shows channel.
    /// Seasons and episodes are never partitions, so they are never read or written.
    /// </summary>
    internal static readonly IReadOnlyList<ChannelScorePhase> SyncPhases = new[]
    {
        new ChannelScorePhase("movies", ChannelIds.Movies, typeof(Movie).FullName!),
        new ChannelScorePhase("series", ChannelIds.Shows, typeof(Series).FullName!),
        new ChannelScorePhase("orphan-series", ChannelIds.Shows, typeof(Folder).FullName!),
    };

    private static readonly JsonSerializerOptions CursorJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly PhantomDb _db;
    private readonly SwarmHealthClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly IChannelScorePager _pager;
    private readonly ILogger<SwarmHealthWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lazy<string> _syncTarget;
    private bool _titleGaugesRestored;
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;

    public SwarmHealthWorker(
        PhantomDb db,
        SwarmHealthClient client,
        ILibraryManager libraryManager,
        IChannelScorePager pager,
        IServerApplicationHost serverHost,
        ILogger<SwarmHealthWorker> logger)
        : this(
            db,
            client,
            libraryManager,
            pager,
            logger,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => DateTimeOffset.UtcNow,
            () => (serverHost ?? throw new ArgumentNullException(nameof(serverHost))).SystemId)
    {
    }

    internal SwarmHealthWorker(
        PhantomDb db,
        SwarmHealthClient client,
        ILibraryManager libraryManager,
        IChannelScorePager pager,
        ILogger<SwarmHealthWorker> logger,
        Func<PluginConfiguration> configProvider,
        Func<DateTimeOffset> clock,
        Func<string> syncTargetId)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _pager = pager ?? throw new ArgumentNullException(nameof(pager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(syncTargetId);
        _syncTarget = new Lazy<string>(() => NormalizeTarget(syncTargetId()));
    }

    /// <summary>The library this worker's sweep pages and writes (normalized server id).</summary>
    internal string SyncTarget => _syncTarget.Value;

    /// <summary><c>plugin_meta</c> key of the sweep cursor for one target library.</summary>
    internal static string CursorKeyFor(string target) => SyncCursorMetaKeyPrefix + NormalizeTarget(target);

    /// <summary>
    /// Stable, topology-neutral target identity: Jellyfin's server id (persisted once per
    /// data directory, so it survives restarts and differs per library PVC). Guid forms are
    /// canonicalised to 32 lower-case hex digits; any other form is trimmed and upper-cased.
    /// </summary>
    internal static string NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException("Swarm-health score sweep needs a stable server/library id; none is available");
        }

        return Guid.TryParse(target, out var g)
            ? g.ToString("N", CultureInfo.InvariantCulture)
            : target.Trim().ToUpperInvariant();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interval = TimeSpan.FromSeconds(_configProvider().SwarmHealthIntervalSeconds);
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, interval, interval);
        _logger.LogInformation("Swarm-health worker started interval={Interval}s", interval.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stopping?.Cancel();
        if (_currentTick is { } tick)
        {
            await tick.ConfigureAwait(false);
        }

        _logger.LogInformation("Swarm-health worker stopped");
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopping?.Dispose();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var cfg = _configProvider();
            if (string.IsNullOrWhiteSpace(cfg.SwarmHealthServiceUrl))
            {
                SwarmHealthCache.Clear();
                return;
            }

            try
            {
                await MeasureOnceAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A measurement failure must not stall the local score sweep.
                _logger.LogWarning(ex, "Swarm-health measurement step failed");
            }

            await SyncStepAsync(cfg, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Swarm-health tick cancelled; the score sweep resumes from its persisted cursor");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Swarm-health tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Measure one batch of due candidates; returns the number of infohashes stored.</summary>
    internal async Task<int> MeasureOnceAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = _clock();
        var due = await _db.GetSwarmHealthDueItemsAsync(
            cfg.SwarmHealthBatchSize,
            now.AddHours(-cfg.SwarmHealthRecheckHours),
            ct).ConfigureAwait(false);
        if (due.Count == 0)
        {
            return 0;
        }

        // Season packs share one infohash across many episodes; measure each once.
        var byHash = new Dictionary<string, SwarmHealthDueItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in due)
        {
            byHash.TryAdd(item.InfoHash, item);
        }

        IReadOnlyDictionary<string, int> peers;
        try
        {
            peers = await _client.MeasureAsync(cfg.SwarmHealthServiceUrl, byHash.Keys.ToList(), ct).ConfigureAwait(false);
            SwarmHealthMetrics.RecordRequest(ok: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            SwarmHealthMetrics.RecordRequest(ok: false);
            _logger.LogWarning(ex, "Swarm-health service request failed ({Count} infohashes)", byHash.Count);
            return 0;
        }

        var rows = new List<SwarmHealthMeasurement>(peers.Count);
        foreach (var (hash, count) in peers)
        {
            if (byHash.TryGetValue(hash, out var item))
            {
                rows.Add(new SwarmHealthMeasurement(hash, item.TmdbId, item.Type, item.Season, item.Episode, count, now));
            }
        }

        await _db.UpsertSwarmHealthAsync(rows, ct).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(r => r.Type))
        {
            SwarmHealthMetrics.RecordMeasured(group.Key, group.Count(r => r.DhtPeers > 0), group.Count(r => r.DhtPeers <= 0));
        }

        var written = await WriteThroughMeasuredAsync(cfg, rows, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Swarm-health measured {Measured}/{Asked} infohashes; wrote Peers through to {Written} BaseItems",
            rows.Count,
            byHash.Count,
            written);
        return rows.Count;
    }

    /// <summary>
    /// Write the Peers value of the titles a measurement just covered straight onto their
    /// BaseItems (resolved by Jellyfin's deterministic channel item id, a primary-key
    /// lookup), so a fresh observation never waits for the next sweep. Bounded by the
    /// measurement batch.
    /// </summary>
    internal async Task<int> WriteThroughMeasuredAsync(PluginConfiguration cfg, IReadOnlyCollection<SwarmHealthMeasurement> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var now = _clock();
        var fresh = now.AddDays(-cfg.SwarmHealthRankWindowDays);
        var written = 0;
        var failed = 0;
        foreach (var (dbType, channelId, channelName, itemType, encode) in new (string, Guid, string, Type, Func<int, string>)[]
                 {
                     ("movie", ChannelIds.Movies, ChannelIds.MoviesName, typeof(Movie), t => ChannelItemId.ForMovie(t).Encode()),
                     ("episode", ChannelIds.Shows, ChannelIds.ShowsName, typeof(Series), t => ChannelItemId.ForSeries(t).Encode()),
                 })
        {
            var tmdbIds = rows.Where(r => r.Type == dbType).Select(r => r.TmdbId).Distinct().ToList();
            if (tmdbIds.Count == 0)
            {
                continue;
            }

            var peers = await _db.GetSwarmPeersForTmdbIdsAsync(dbType, tmdbIds, fresh, ct).ConfigureAwait(false);
            SwarmHealthCache.Record(series: dbType == "episode", tmdbIds, peers, now);
            var changed = new List<(BaseItem Item, float? Previous)>();
            foreach (var tmdb in tmdbIds)
            {
                var externalId = encode(tmdb);
                var item = _libraryManager.GetItemById(_libraryManager.GetNewItemId(externalId + channelName + "16", itemType));
                if (item is null || item.ChannelId != channelId || !string.Equals(item.ExternalId, externalId, StringComparison.Ordinal))
                {
                    continue; // not persisted yet: the sweep scores it once it exists
                }

                var target = SwarmHealthScore.ToCriticRating(peers.TryGetValue(tmdb, out var p) ? p : null);
                if (!SwarmHealthScore.SameRating(item.CriticRating, target))
                {
                    changed.Add((item, item.CriticRating));
                    item.CriticRating = target;
                }
            }

            var (saved, failedHere) = await SaveAsync(channelId, changed, ct).ConfigureAwait(false);
            written += saved;
            failed += failedHere;
        }

        SwarmHealthMetrics.RecordBaseItemsUpdated(written);
        SwarmHealthMetrics.RecordBaseItemSaveFailures(failed);
        return written;
    }

    /// <summary>
    /// p05-baseitem-writeback: one bounded step of the resumable full-catalogue score
    /// sweep. Reads at most <see cref="PluginConfiguration.SwarmHealthSyncBatchSize"/>
    /// persisted channel rows by keyset (crossing into the next phase within the same
    /// budget), computes each scored title's target from a per-batch indexed
    /// <c>swarm_health</c> lookup, writes only rows whose CriticRating differs, then
    /// persists the cursor. Returns what the step did.
    /// </summary>
    internal async Task<SyncStepResult> SyncStepAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = _clock();
        var cursor = await LoadCursorAsync(ct).ConfigureAwait(false);
        if (!_titleGaugesRestored)
        {
            // After a restart the title gauges come back from the last COMPLETED sweep
            // of this library, persisted with the cursor — no counting query.
            _titleGaugesRestored = true;
            if (cursor.Published is { } published)
            {
                PublishTitles(published);
                _logger.LogInformation(
                    "Swarm-health title gauges restored from sweep {Sweep}: target={Target} movie live={MovieLive} dead={MovieDead} series live={SeriesLive} dead={SeriesDead}",
                    cursor.PublishedSweep,
                    SyncTarget,
                    published.MovieLive,
                    published.MovieDead,
                    published.SeriesLive,
                    published.SeriesDead);
            }
        }

        if (cursor.Done)
        {
            var gap = TimeSpan.FromSeconds((double)cfg.SwarmHealthIntervalSeconds * MinTicksBetweenSweeps);
            if (cursor.StartedAt is { } started && now - DateTimeOffset.FromUnixTimeSeconds(started) < gap)
            {
                return SyncStepResult.Idle;
            }

            cursor = new SyncCursor
            {
                Phase = 0,
                StartedAt = now.ToUnixTimeSeconds(),
                Sweep = cursor.Sweep + 1,
                Published = cursor.Published,
                PublishedSweep = cursor.PublishedSweep,
            };
            _logger.LogInformation(
                "Swarm-health score sweep {Sweep} started: target={Target} batch={Batch} access={Access}",
                cursor.Sweep,
                SyncTarget,
                cfg.SwarmHealthSyncBatchSize,
                _pager.AccessPath);
        }

        var budget = cfg.SwarmHealthSyncBatchSize;
        var fresh = now.AddDays(-cfg.SwarmHealthRankWindowDays);
        var read = 0;
        var written = 0;
        var failed = 0;
        while (budget > 0 && cursor.Phase < SyncPhases.Count)
        {
            ct.ThrowIfCancellationRequested();
            var phase = SyncPhases[cursor.Phase];
            var after = cursor.LastId is { } last ? Guid.Parse(last) : (Guid?)null;
            var requested = budget;
            var page = await _pager.NextPageAsync(phase, after, requested, ct).ConfigureAwait(false);
            read += page.Count;
            budget -= page.Count;

            var measurable = new List<int>(page.Count);
            foreach (var row in page)
            {
                var (kind, tmdb) = SwarmHealthScore.Classify(row.ExternalId);
                if (kind == ScoredTitleKind.Measurable)
                {
                    measurable.Add(tmdb);
                }
            }

            var isMovies = phase.ChannelId == ChannelIds.Movies;
            var dbType = isMovies ? "movie" : "episode";
            var peers = await _db.GetSwarmPeersForTmdbIdsAsync(dbType, measurable, fresh, ct).ConfigureAwait(false);

            // Memoise this batch's lookups for the metadata provider (incremental: the
            // cache is never filled by a whole-table aggregate).
            SwarmHealthCache.Record(series: !isMovies, measurable, peers, now);

            // Title-health tally for phantom_swarm_health_titles: every scored title of
            // this library with a fresh measurement (series = best episode), counted once
            // per sweep because it rides the same keyset batch (committed with the cursor).
            foreach (var tmdb in measurable)
            {
                if (peers.TryGetValue(tmdb, out var p))
                {
                    cursor.Counting.Add(isMovies, live: p > 0);
                }
            }
            var changed = new List<(BaseItem Item, float? Previous)>();
            foreach (var row in page)
            {
                if (SwarmHealthScore.TargetFor(row.ExternalId, peers) is not { } target
                    || SwarmHealthScore.SameRating(row.CriticRating, target))
                {
                    continue;
                }

                if (_libraryManager.GetItemById(row.Id) is not { } item || item.ChannelId != phase.ChannelId)
                {
                    continue; // deleted between the page read and hydration
                }

                changed.Add((item, item.CriticRating));
                item.CriticRating = target;
            }

            // A row that cannot be saved (e.g. core deleting it concurrently) is
            // counted and skipped, never retried forever: the cursor still advances
            // past this batch, and the next sweep re-scores it if it still exists.
            var (saved, failedHere) = await SaveAsync(phase.ChannelId, changed, ct).ConfigureAwait(false);
            written += saved;
            failed += failedHere;
            cursor.Read += page.Count;
            cursor.Written += saved;
            cursor.Failed += failedHere;
            if (page.Count < requested)
            {
                // Short page: this phase is exhausted; continue with the next one.
                cursor.Phase++;
                cursor.LastId = null;
            }
            else
            {
                cursor.LastId = page[^1].Id.ToString("D", CultureInfo.InvariantCulture);
            }

            // The cursor only advances after the batch's writes committed: a crash or
            // cancellation re-reads at most this batch, whose rows are then unchanged.
            await SaveCursorAsync(cursor, ct).ConfigureAwait(false);
        }

        SwarmHealthMetrics.RecordSyncRead(read);
        SwarmHealthMetrics.RecordBaseItemsUpdated(written);
        SwarmHealthMetrics.RecordBaseItemSaveFailures(failed);
        _logger.LogInformation(
            "Swarm-health score sync step: sweep={Sweep} read={Read} written={Written} failed={Failed} phase={Phase} cursor={Cursor}",
            cursor.Sweep,
            read,
            written,
            failed,
            cursor.Phase < SyncPhases.Count ? SyncPhases[cursor.Phase].Name : "done",
            cursor.LastId ?? "-");

        var completed = cursor.Phase >= SyncPhases.Count;
        if (completed)
        {
            cursor.Done = true;
            cursor.CompletedAt = now.ToUnixTimeSeconds();
            cursor.Published = cursor.Counting.Clone();
            cursor.PublishedSweep = cursor.Sweep;
            await SaveCursorAsync(cursor, ct).ConfigureAwait(false);
            PublishTitles(cursor.Published);
            SwarmHealthMetrics.RecordSyncSweepCompleted();
            var took = TimeSpan.FromSeconds(cursor.CompletedAt.Value - (cursor.StartedAt ?? cursor.CompletedAt.Value));
            _logger.LogInformation(
                "Swarm-health score sweep {Sweep} completed: read={Read} written={Written} failed={Failed} took={Took}s cycle={Cycle}h target={Target} titles movie live={MovieLive} dead={MovieDead} series live={SeriesLive} dead={SeriesDead}",
                cursor.Sweep,
                cursor.Read,
                cursor.Written,
                cursor.Failed,
                (long)took.TotalSeconds,
                cfg.SwarmHealthRecheckHours,
                SyncTarget,
                cursor.Published.MovieLive,
                cursor.Published.MovieDead,
                cursor.Published.SeriesLive,
                cursor.Published.SeriesDead);
            if (took > TimeSpan.FromHours(cfg.SwarmHealthRecheckHours / 2.0))
            {
                _logger.LogWarning(
                    "Swarm-health score sweep took {Took} — more than half the {Cycle}h measurement cycle; raise SwarmHealthSyncBatchSize",
                    took,
                    cfg.SwarmHealthRecheckHours);
            }
        }

        return new SyncStepResult(read, written, completed, failed);
    }

    /// <summary>
    /// Saves one batch of re-scored items: one batched write, and — only when that
    /// fails — a per-item fallback so a single unsavable item (concurrent delete:
    /// FK violation) cannot abort the batch or stall the sweep cursor. An item whose
    /// own save fails gets its in-memory rating reverted and is counted as failed.
    /// Cancellation is never swallowed. Returns (saved, failed).
    /// </summary>
    private async Task<(int Saved, int Failed)> SaveAsync(Guid channelId, List<(BaseItem Item, float? Previous)> changed, CancellationToken ct)
    {
        if (changed.Count == 0)
        {
            return (0, 0);
        }

        var parent = _libraryManager.GetItemById(channelId) ?? changed[0].Item.GetParent();
        try
        {
            await _libraryManager.UpdateItemsAsync(changed.ConvertAll(c => c.Item), parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            return (changed.Count, 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Revert(changed);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Swarm-health batch save of {Count} BaseItems failed; retrying item by item", changed.Count);
        }

        var saved = 0;
        var failed = 0;
        for (var i = 0; i < changed.Count; i++)
        {
            var (item, previous) = changed[i];
            try
            {
                ct.ThrowIfCancellationRequested();
                await _libraryManager.UpdateItemsAsync(new[] { item }, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                saved++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Revert(changed.GetRange(i, changed.Count - i));
                throw;
            }
            catch (Exception ex)
            {
                item.CriticRating = previous;
                failed++;
                _logger.LogWarning(ex, "Swarm-health could not write Peers onto {Item}; skipped (re-scored next sweep)", item.Id);
            }
        }

        return (saved, failed);
    }

    private static void Revert(IEnumerable<(BaseItem Item, float? Previous)> unsaved)
    {
        foreach (var (item, previous) in unsaved)
        {
            item.CriticRating = previous;
        }
    }

    private static void PublishTitles(TitleCounts counts)
    {
        SwarmHealthMetrics.SetTitles("movie", counts.MovieLive, counts.MovieDead);
        SwarmHealthMetrics.SetTitles("series", counts.SeriesLive, counts.SeriesDead);
    }

    private async Task<SyncCursor> LoadCursorAsync(CancellationToken ct)
    {
        var raw = await _db.GetMetaAsync(CursorKeyFor(SyncTarget), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new SyncCursor { Done = true };
        }

        try
        {
            var cursor = JsonSerializer.Deserialize<SyncCursor>(raw, CursorJson) ?? new SyncCursor { Done = true };
            if (cursor.Phase < 0 || cursor.Phase > SyncPhases.Count)
            {
                cursor = new SyncCursor { Done = true, Sweep = cursor.Sweep, Published = cursor.Published, PublishedSweep = cursor.PublishedSweep };
            }

            return cursor;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unreadable swarm-health sync cursor; starting a fresh sweep");
            return new SyncCursor { Done = true };
        }
    }

    private Task SaveCursorAsync(SyncCursor cursor, CancellationToken ct)
        => _db.SetMetaAsync(CursorKeyFor(SyncTarget), JsonSerializer.Serialize(cursor, CursorJson), ct);

    /// <summary>Outcome of one <see cref="SyncStepAsync"/> call.</summary>
    internal readonly record struct SyncStepResult(int Read, int Written, bool SweepCompleted, int Failed = 0)
    {
        public static SyncStepResult Idle => new(0, 0, false);
    }

    /// <summary>Movie/series live/dead title counts of one library (phantom_swarm_health_titles).</summary>
    internal sealed class TitleCounts
    {
        public long MovieLive { get; set; }

        public long MovieDead { get; set; }

        public long SeriesLive { get; set; }

        public long SeriesDead { get; set; }

        public void Add(bool movie, bool live)
        {
            if (movie)
            {
                if (live)
                {
                    MovieLive++;
                }
                else
                {
                    MovieDead++;
                }
            }
            else if (live)
            {
                SeriesLive++;
            }
            else
            {
                SeriesDead++;
            }
        }

        public TitleCounts Clone() => new() { MovieLive = MovieLive, MovieDead = MovieDead, SeriesLive = SeriesLive, SeriesDead = SeriesDead };
    }

    /// <summary>Persisted per-library sweep position (JSON in <c>plugin_meta</c>).</summary>
    internal sealed class SyncCursor
    {
        public int Sweep { get; set; }

        public int Phase { get; set; }

        public string? LastId { get; set; }

        public long? StartedAt { get; set; }

        public long? CompletedAt { get; set; }

        public bool Done { get; set; }

        public long Read { get; set; }

        public long Written { get; set; }

        /// <summary>Rows of this sweep whose save failed (skipped; the cursor still advanced).</summary>
        public long Failed { get; set; }

        /// <summary>Title-health tally of the sweep in progress (advances with the cursor).</summary>
        public TitleCounts Counting { get; set; } = new();

        /// <summary>Tally of the last COMPLETED sweep: what the title gauges publish.</summary>
        public TitleCounts? Published { get; set; }

        public int? PublishedSweep { get; set; }
    }
}

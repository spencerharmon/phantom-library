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
/// Disabled entirely while the service URL is empty.
/// </summary>
public sealed class SwarmHealthWorker : IHostedService, IDisposable
{
    /// <summary><c>plugin_meta</c> key of the persisted sweep cursor.</summary>
    internal const string SyncCursorMetaKey = "swarm_health_sync_cursor";

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
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;

    public SwarmHealthWorker(
        PhantomDb db,
        SwarmHealthClient client,
        ILibraryManager libraryManager,
        IChannelScorePager pager,
        ILogger<SwarmHealthWorker> logger)
        : this(db, client, libraryManager, pager, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration(), () => DateTimeOffset.UtcNow)
    {
    }

    internal SwarmHealthWorker(
        PhantomDb db,
        SwarmHealthClient client,
        ILibraryManager libraryManager,
        IChannelScorePager pager,
        ILogger<SwarmHealthWorker> logger,
        Func<PluginConfiguration> configProvider,
        Func<DateTimeOffset> clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _pager = pager ?? throw new ArgumentNullException(nameof(pager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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

        var fresh = _clock().AddDays(-cfg.SwarmHealthRankWindowDays);
        var written = 0;
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
            var changed = new List<BaseItem>();
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
                    item.CriticRating = target;
                    changed.Add(item);
                }
            }

            written += await SaveAsync(channelId, changed, ct).ConfigureAwait(false);
        }

        SwarmHealthMetrics.RecordBaseItemsUpdated(written);
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
        if (cursor.Done)
        {
            var gap = TimeSpan.FromSeconds((double)cfg.SwarmHealthIntervalSeconds * MinTicksBetweenSweeps);
            if (cursor.StartedAt is { } started && now - DateTimeOffset.FromUnixTimeSeconds(started) < gap)
            {
                return SyncStepResult.Idle;
            }

            cursor = new SyncCursor { Phase = 0, StartedAt = now.ToUnixTimeSeconds(), Sweep = cursor.Sweep + 1 };
            _logger.LogInformation(
                "Swarm-health score sweep {Sweep} started: batch={Batch} access={Access}",
                cursor.Sweep,
                cfg.SwarmHealthSyncBatchSize,
                _pager.AccessPath);
        }

        var budget = cfg.SwarmHealthSyncBatchSize;
        var fresh = now.AddDays(-cfg.SwarmHealthRankWindowDays);
        var read = 0;
        var written = 0;
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

            var dbType = phase.ChannelId == ChannelIds.Movies ? "movie" : "episode";
            var peers = await _db.GetSwarmPeersForTmdbIdsAsync(dbType, measurable, fresh, ct).ConfigureAwait(false);
            var changed = new List<BaseItem>();
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

                item.CriticRating = target;
                changed.Add(item);
            }

            var saved = await SaveAsync(phase.ChannelId, changed, ct).ConfigureAwait(false);
            written += saved;
            cursor.Read += page.Count;
            cursor.Written += saved;
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
        _logger.LogInformation(
            "Swarm-health score sync step: sweep={Sweep} read={Read} written={Written} phase={Phase} cursor={Cursor}",
            cursor.Sweep,
            read,
            written,
            cursor.Phase < SyncPhases.Count ? SyncPhases[cursor.Phase].Name : "done",
            cursor.LastId ?? "-");

        var completed = cursor.Phase >= SyncPhases.Count;
        if (completed)
        {
            cursor.Done = true;
            cursor.CompletedAt = now.ToUnixTimeSeconds();
            await SaveCursorAsync(cursor, ct).ConfigureAwait(false);
            SwarmHealthMetrics.RecordSyncSweepCompleted();
            var took = TimeSpan.FromSeconds(cursor.CompletedAt.Value - (cursor.StartedAt ?? cursor.CompletedAt.Value));
            _logger.LogInformation(
                "Swarm-health score sweep {Sweep} completed: read={Read} written={Written} took={Took}s cycle={Cycle}h",
                cursor.Sweep,
                cursor.Read,
                cursor.Written,
                (long)took.TotalSeconds,
                cfg.SwarmHealthRecheckHours);
            if (took > TimeSpan.FromHours(cfg.SwarmHealthRecheckHours / 2.0))
            {
                _logger.LogWarning(
                    "Swarm-health score sweep took {Took} — more than half the {Cycle}h measurement cycle; raise SwarmHealthSyncBatchSize",
                    took,
                    cfg.SwarmHealthRecheckHours);
            }
        }

        return new SyncStepResult(read, written, completed);
    }

    private async Task<int> SaveAsync(Guid channelId, List<BaseItem> changed, CancellationToken ct)
    {
        if (changed.Count == 0)
        {
            return 0;
        }

        var parent = _libraryManager.GetItemById(channelId) ?? changed[0].GetParent();
        await _libraryManager.UpdateItemsAsync(changed, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        return changed.Count;
    }

    private async Task<SyncCursor> LoadCursorAsync(CancellationToken ct)
    {
        var raw = await _db.GetMetaAsync(SyncCursorMetaKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new SyncCursor { Done = true };
        }

        try
        {
            var cursor = JsonSerializer.Deserialize<SyncCursor>(raw, CursorJson) ?? new SyncCursor { Done = true };
            if (cursor.Phase < 0 || cursor.Phase > SyncPhases.Count)
            {
                cursor = new SyncCursor { Done = true, Sweep = cursor.Sweep };
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
        => _db.SetMetaAsync(SyncCursorMetaKey, JsonSerializer.Serialize(cursor, CursorJson), ct);

    /// <summary>Outcome of one <see cref="SyncStepAsync"/> call.</summary>
    internal readonly record struct SyncStepResult(int Read, int Written, bool SweepCompleted)
    {
        public static SyncStepResult Idle => new(0, 0, false);
    }

    /// <summary>Persisted sweep position (JSON in <c>plugin_meta</c>).</summary>
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
    }
}

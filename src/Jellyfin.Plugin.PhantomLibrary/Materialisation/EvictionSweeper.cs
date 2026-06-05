using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using NCrontab;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Background sweeper (M7): demotes stale Materialised items back to Virtual,
/// prunes stale Phantom rows, purges expired caches. Cron-scheduled via
/// <see cref="PluginConfiguration.EvictionScheduleCron"/>; defaults to daily 04:00.
/// </summary>
public sealed class EvictionSweeper : IHostedService, IDisposable
{
    private const string DefaultCron = "0 4 * * *";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IGostreamClient _gostream;
    private readonly PhantomDb _db;
    private readonly ILogger<EvictionSweeper> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public EvictionSweeper(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IGostreamClient gostream,
        PhantomDb db,
        ILogger<EvictionSweeper> logger)
        : this(libraryManager, userManager, userDataManager, gostream, db, logger,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => DateTimeOffset.UtcNow)
    {
    }

    public EvictionSweeper(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IGostreamClient gostream,
        PhantomDb db,
        ILogger<EvictionSweeper> logger,
        Func<PluginConfiguration> configProvider,
        Func<DateTimeOffset> nowProvider)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _gostream = gostream;
        _db = db;
        _logger = logger;
        _configProvider = configProvider;
        _nowProvider = nowProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _tickLock.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cron = _configProvider().EvictionScheduleCron;
            if (string.IsNullOrWhiteSpace(cron)) cron = DefaultCron;

            CrontabSchedule schedule;
            try
            {
                schedule = CrontabSchedule.Parse(cron);
            }
            catch (CrontabException ex)
            {
                _logger.LogWarning(ex, "EvictionScheduleCron '{Cron}' invalid; falling back to {Default}", cron, DefaultCron);
                schedule = CrontabSchedule.Parse(DefaultCron);
            }

            var nowLocal = _nowProvider().LocalDateTime;
            var next = schedule.GetNextOccurrence(nowLocal);
            var delay = next - nowLocal;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EvictionSweeper tick threw");
            }
        }
    }

    /// <summary>Run one sweep cycle. Public for tests / future manual trigger.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var cfg = _configProvider();
        if (!cfg.EvictionEnabled)
        {
            _logger.LogInformation("[Eviction] sweeper disabled by config; skipping tick");
            return;
        }

        if (!await _tickLock.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
        {
            _logger.LogInformation("[Eviction] concurrent tick skipped (previous run still holds the lock)");
            return;
        }

        var sw = Stopwatch.StartNew();
        var demoted = 0;
        int prunedPhantoms;
        int purgedTmdbCache;
        int purgedUnavailable;

        try
        {
            // 1. Demote stale Materialised
            var materialised = await _db.ListItemsByStateAsync(
                PhantomItemState.Materialised.ToString(), ct).ConfigureAwait(false);
            var idleCutoff = TimeSpan.FromDays(Math.Max(1, cfg.EvictionIdleDays));
            var users = SafeUsers();

            foreach (var row in materialised)
            {
                ct.ThrowIfCancellationRequested();
                var item = SafeGetItem(row.ItemGuid);
                if (item is null)
                {
                    _logger.LogInformation("[Eviction] item {Guid} not found in library; dropping phantom row", row.ItemGuid);
                    await _db.DeleteItemAsync(row.ItemGuid, ct).ConfigureAwait(false);
                    continue;
                }

                DateTime? lastPlayed = null;
                var protectedByFav = false;
                foreach (var user in users)
                {
                    UserItemData? ud;
                    try { ud = _userDataManager.GetUserData(user, item); }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[Eviction] GetUserData threw for user {User} item {Item}", user.Id, item.Id);
                        continue;
                    }

                    if (ud is null) continue;

                    if (ud.LastPlayedDate is DateTime lp && (lastPlayed is null || lp > lastPlayed))
                    {
                        lastPlayed = lp;
                    }

                    if (ud.IsFavorite)
                    {
                        var prefs = await _db.GetUserPrefsAsync(user.Id, ct).ConfigureAwait(false);
                        if (prefs.ProtectFavourites)
                        {
                            protectedByFav = true;
                        }
                    }
                }

                var now = _nowProvider();
                var idle = lastPlayed is null
                    || (now - new DateTimeOffset(lastPlayed.Value.ToUniversalTime(), TimeSpan.Zero)) > idleCutoff;

                if (!idle) continue;
                if (protectedByFav)
                {
                    _logger.LogDebug("[Eviction] item {Item} protected by favourite", item.Id);
                    continue;
                }

                await DemoteAsync(item, row, ct).ConfigureAwait(false);
                demoted++;
            }

            // 2. Prune stale Phantoms
            prunedPhantoms = await _db.PurgeExpiredPhantomsAsync(
                TimeSpan.FromDays(Math.Max(1, cfg.PhantomRetentionDays)), ct).ConfigureAwait(false);

            // 3. tmdb_cache + unavailable_marker
            purgedTmdbCache = await _db.PurgeExpiredTmdbCacheAsync(ct).ConfigureAwait(false);
            purgedUnavailable = await _db.PurgeExpiredUnavailableMarkersAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _tickLock.Release();
        }

        _logger.LogInformation(
            "[Eviction] demoted={Demoted} pruned_phantoms={Pruned} purged_tmdb_cache={Tmdb} purged_unavailable={Un} duration={Ms}ms",
            demoted, prunedPhantoms, purgedTmdbCache, purgedUnavailable, sw.ElapsedMilliseconds);
    }

    private IReadOnlyList<Jellyfin.Database.Implementations.Entities.User> SafeUsers()
    {
        try
        {
            var list = new List<Jellyfin.Database.Implementations.Entities.User>();
            foreach (var u in _userManager.GetUsers())
            {
                list.Add(u);
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Eviction] failed to enumerate users");
            return Array.Empty<Jellyfin.Database.Implementations.Entities.User>();
        }
    }

    private BaseItem? SafeGetItem(Guid id)
    {
        try { return _libraryManager.GetItemById(id); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Eviction] GetItemById threw for {Id}", id);
            return null;
        }
    }

    private async Task DemoteAsync(BaseItem item, PhantomItemRow row, CancellationToken ct)
    {
        // 1. Vault Mode unprestage (best effort)
        if (!string.IsNullOrWhiteSpace(row.StubPath))
        {
            try
            {
                if (await _gostream.IsVaultModePresentAsync(ct).ConfigureAwait(false))
                {
                    await _gostream.UnprestageAsync(row.StubPath!, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Eviction] UnprestageAsync failed for {Stub}", row.StubPath);
            }

            // 2. gostream RemoveAsync — required by sweeper contract.
            try
            {
                await _gostream.RemoveAsync(row.StubPath!, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Eviction] gostream RemoveAsync failed for {Stub}; continuing demotion", row.StubPath);
            }
        }
        else
        {
            _logger.LogDebug("[Eviction] {Item} has no stub_path; skipping RemoveAsync", item.Id);
        }

        // 3. Clear path + flip IsVirtualItem (reflective; mirrors Materialiser).
        var isVirtualProp = typeof(BaseItem).GetProperty(
            "IsVirtualItem", BindingFlags.Public | BindingFlags.Instance);
        try
        {
            item.Path = string.Empty;
            if (isVirtualProp is not null && isVirtualProp.CanWrite)
            {
                isVirtualProp.SetValue(item, true);
            }

            await _libraryManager.UpdateItemAsync(item, item.GetParent(),
                MediaBrowser.Controller.Library.ItemUpdateType.MetadataImport, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Eviction] UpdateItemAsync (demote) failed for {Id}", item.Id);
        }

        // 4. Update DB row: state=Virtual; clear stub/fuse/materialised_at.
        await _db.UpsertPhantomItemAsync(item.Id, new PhantomItemRow
        {
            TmdbId = row.TmdbId,
            ImdbId = row.ImdbId,
            Type = row.Type,
            State = PhantomItemState.Virtual,
            FirstSeen = row.FirstSeen,
            LastTouched = _nowProvider(),
            EvictionProtected = row.EvictionProtected,
            OriginalOverview = row.OriginalOverview,
            StubPath = null,
            FusePath = null,
            MaterialisedAt = null,
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("[Eviction] demoted {Item} to Virtual", item.Id);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Cron-driven idle-eviction sweeper for channel-arch materialised
/// items (plan §6.1). For each row in <c>materialised_state</c>:
///
///   * resolves the channel BaseItem via its ExternalId,
///   * checks per-user LastPlayedDate / IsFavorite across all users
///     (a favourite pins the shared file only while at least one
///     favouriting user keeps their per-user protect_favourites toggle
///     on — REQ-M14-PER-USER, Surface 2),
///   * skips if favourite-protected, recently played, or recently
///     materialised-but-never-played within the idle window,
///   * otherwise atomically CLAIMS the row by deleting it from
///     <c>materialised_state</c> first, then calls
///     <c>gostream.RemoveAsync</c>, and re-refreshes the channel item so
///     the channel re-emits it with the splash MediaSource + 'phantom'
///     tag.
///
/// Multi-writer safety (p4-phantomdb-multiwriter-safety-fixes,
/// docs/tasks/p4-phantomdb-multiwriter-audit.md): the sweeper lists every
/// candidate row up front, so two concurrent sweepers — a sibling replica
/// sharing the DB, or an overlapping manual/cron run in this same process
/// — can both see the SAME row before either acts on it. Deleting the row
/// FIRST (an atomic single-statement DELETE) makes the row itself the
/// claim: exactly one concurrent caller's delete affects a row and
/// proceeds to call gostream; every other caller sees "0 rows affected"
/// and skips, so <c>gostream.RemoveAsync</c> is never invoked twice for
/// the same stub. A failed remove re-inserts the row (preserving its
/// original <c>materialised_at</c>) so it is retried, unclaimed, next tick.
///
/// Failure semantics:
///   * gostream.RemoveAsync throws → log, restore the claimed row (state
///     row is back so the next tick retries; no refresh is fired so the
///     channel keeps presenting the materialised view until the
///     remove actually succeeds).
///   * BaseItem not found for a state row's external id → log + skip
///     (orphan state row; surfaced for operator inspection rather
///     than silently masked by an eviction).
/// </summary>
public sealed class EvictionSweeper : IHostedService, IDisposable
{
    private readonly PhantomDb _db;
    private readonly IGostreamClient _gostream;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IChannelItemRefreshManager _refreshManager;
    private readonly ChannelStateProvider _state;
    private readonly ILogger<EvictionSweeper> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public EvictionSweeper(
        PhantomDb db,
        IGostreamClient gostream,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IChannelItemRefreshManager refreshManager,
        ChannelStateProvider state,
        ILogger<EvictionSweeper> logger)
        : this(db, gostream, libraryManager, userManager, userDataManager, refreshManager, state, logger,
               () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal EvictionSweeper(
        PhantomDb db,
        IGostreamClient gostream,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IChannelItemRefreshManager refreshManager,
        ChannelStateProvider state,
        ILogger<EvictionSweeper> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _gostream = gostream ?? throw new ArgumentNullException(nameof(gostream));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
        _refreshManager = refreshManager ?? throw new ArgumentNullException(nameof(refreshManager));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        if (_loop is { } t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Eviction] loop terminated with error during stop");
            }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _configProvider();
            CrontabSchedule? schedule = null;
            try
            {
                schedule = CrontabSchedule.Parse(cfg.EvictionScheduleCron);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[Eviction] failed to parse EvictionScheduleCron '{Cron}'; sleeping 1h then retrying",
                    cfg.EvictionScheduleCron);
            }

            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan delay = schedule is null
                ? TimeSpan.FromHours(1)
                : schedule.GetNextOccurrence(nowUtc) - nowUtc;

            if (delay < TimeSpan.FromSeconds(1))
            {
                delay = TimeSpan.FromSeconds(1);
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            cfg = _configProvider();
            if (!cfg.EvictionEnabled)
            {
                _logger.LogDebug("[Eviction] tick skipped (EvictionEnabled=false)");
                continue;
            }

            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Eviction] RunOnceAsync threw; loop continues");
            }
        }
    }

    /// <summary>
    /// One pass over every materialised_state row. Public so tests
    /// and the rig can trigger a sweep without spinning the cron loop.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var cfg = _configProvider();
        var idleCutoff = TimeSpan.FromDays(Math.Max(0, cfg.EvictionIdleDays));
        var protectFavourites = cfg.ProtectFavourites;
        var nowUtc = DateTimeOffset.UtcNow;

        var movieRows = await _db.ListMaterialisedStateAsync("movie", ct).ConfigureAwait(false);
        var episodeRows = await _db.ListMaterialisedStateAsync("episode", ct).ConfigureAwait(false);
        var allRows = new List<MaterialisedStateRow>(movieRows.Count + episodeRows.Count);
        allRows.AddRange(movieRows);
        allRows.AddRange(episodeRows);

        var users = _userManager.GetUsers().ToList();

        // Per-user favourite-protection toggles (REQ-M14-PER-USER, Surface 2).
        // A user with no user_prefs row falls back to defaults (protect on), so
        // an empty table reproduces the historical any-user-favourite behaviour.
        var prefsByUser = new Dictionary<Guid, UserPrefs>(users.Count);
        foreach (var user in users)
        {
            prefsByUser[user.Id] = await _db.GetUserPrefsAsync(user.Id, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "[Eviction] tick start: {Total} candidate row(s) ({Movies} movies + {Episodes} episodes), idleCutoff={Days}d, protectFavourites={Protect}, users={UserCount}",
            allRows.Count, movieRows.Count, episodeRows.Count, idleCutoff.TotalDays, protectFavourites, users.Count);

        int evicted = 0, skippedFav = 0, skippedRecent = 0, skippedYoung = 0, skippedOrphan = 0, removeFailed = 0;

        foreach (var row in allRows)
        {
            ct.ThrowIfCancellationRequested();

            var kind = row.Type == "movie"
                ? ChannelStateProvider.KindMovies
                : ChannelStateProvider.KindShows;
            var channelId = ChannelIds.For(kind);
            string externalId;
            try
            {
                externalId = row.Type == "movie"
                    ? ChannelItemId.ForMovie(row.TmdbId).Encode()
                    : ChannelItemId.ForEpisode(row.TmdbId, row.Season, row.Episode).Encode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[Eviction] could not encode external id for tmdb={Tmdb} type={Type} s={Season} e={Episode}; skipping",
                    row.TmdbId, row.Type, row.Season, row.Episode);
                continue;
            }

            var matches = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ExternalId = externalId,
            });

            BaseItem? baseItem = null;
            if (matches is not null)
            {
                foreach (var candidate in matches)
                {
                    if (candidate is null)
                    {
                        continue;
                    }

                    if (candidate.ChannelId == channelId)
                    {
                        baseItem = candidate;
                        break;
                    }
                }

                if (baseItem is null && matches.Count > 0)
                {
                    // Fall back to the first hit if none matched on
                    // ChannelId — ExternalId is unique enough for our
                    // channel-arch external ids that this is safe and
                    // beats reporting a phantom orphan.
                    baseItem = matches[0];
                }
            }

            if (baseItem is null)
            {
                _logger.LogWarning(
                    "[Eviction] orphan materialised_state row; no BaseItem for ExternalId={External} (channel={Channel}); skipping (operator should inspect)",
                    externalId, channelId);
                skippedOrphan++;
                continue;
            }

            DateTime? lastPlayed = null;
            bool favProtected = false;
            foreach (var user in users)
            {
                var ud = SafeGetUserData(user, baseItem);
                if (ud is null)
                {
                    continue;
                }

                if (ud.LastPlayedDate.HasValue)
                {
                    if (!lastPlayed.HasValue || ud.LastPlayedDate.Value > lastPlayed.Value)
                    {
                        lastPlayed = ud.LastPlayedDate;
                    }
                }

                // Per-user favourite protection: this user's favourite pins the
                // shared file only while they keep protect_favourites on. A
                // missing prefs row means defaults (protect on). The shared file
                // stops being pinned once the last opted-in favouriting user
                // drops the favourite or turns the toggle off.
                if (ud.IsFavorite)
                {
                    var prefs = prefsByUser.TryGetValue(user.Id, out var p) ? p : UserPrefs.Defaults;
                    if (prefs.ProtectFavourites)
                    {
                        favProtected = true;
                    }
                }
            }

            if (protectFavourites && favProtected)
            {
                _logger.LogDebug(
                    "[Eviction] favourite-protected, skipping ExternalId={External}",
                    externalId);
                skippedFav++;
                continue;
            }

            if (lastPlayed.HasValue)
            {
                var lpUtc = lastPlayed.Value.Kind == DateTimeKind.Utc
                    ? new DateTimeOffset(lastPlayed.Value, TimeSpan.Zero)
                    : new DateTimeOffset(DateTime.SpecifyKind(lastPlayed.Value, DateTimeKind.Utc), TimeSpan.Zero);
                if ((nowUtc - lpUtc) < idleCutoff)
                {
                    skippedRecent++;
                    continue;
                }
            }
            else if ((nowUtc - row.MaterialisedAt) < idleCutoff)
            {
                skippedYoung++;
                continue;
            }

            // Claim the row FIRST via an atomic delete, then act on it.
            // Under concurrent writers — two replica sweepers racing the
            // same shared DB, or a manual RunOnceAsync overlapping the cron
            // loop within one process — every sweeper lists the SAME
            // materialised_state row before any of them acts on it.
            // Deleting first (rather than removing via gostream then
            // deleting, the original order) makes the row itself the
            // mutual-exclusion primitive: SQLite guarantees the DELETE is
            // atomic, so exactly one concurrent caller's delete affects a
            // row (returns 1) and every other caller sees 0 and skips —
            // never double-calling gostream.RemoveAsync for the same
            // stub_path. If the subsequent gostream remove fails, the row
            // is re-inserted (preserving the ORIGINAL materialised_at so a
            // failed-then-retried evict does not look freshly
            // materialised) so the existing retry-next-tick behaviour is
            // unchanged.
            var claimed = await _db.DeleteMaterialisedStateAsync(row.TmdbId, row.Type, row.Season, row.Episode, ct)
                .ConfigureAwait(false);
            if (claimed == 0)
            {
                // Another concurrent sweeper (this process or a sibling
                // replica sharing the DB) already claimed this row.
                _logger.LogDebug(
                    "[Eviction] row already claimed by a concurrent sweeper; skipping ExternalId={External}",
                    externalId);
                continue;
            }

            try
            {
                await _gostream.RemoveAsync(row.StubPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Put the row back before propagating — we already claimed
                // it, and cancellation must not silently drop it.
                await _db.InsertMaterialisedStateAsync(
                    row.TmdbId, row.Type, row.Season, row.Episode, row.StubPath, row.FusePath, CancellationToken.None, row.MaterialisedAt)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[Eviction] gostream RemoveAsync failed for stub_path={Path}; restoring claimed row, will retry next tick",
                    row.StubPath);
                await _db.InsertMaterialisedStateAsync(
                    row.TmdbId, row.Type, row.Season, row.Episode, row.StubPath, row.FusePath, CancellationToken.None, row.MaterialisedAt)
                    .ConfigureAwait(false);
                removeFailed++;
                continue;
            }

            try
            {
                await _refreshManager.RefreshChannelItemAsync(
                    channelId,
                    externalId,
                    new ChannelItemRefreshOptions
                    {
                        ForceUpdate = true,
                        ForceProbe = false,
                        InvalidateMediaInfoCache = true,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[Eviction] post-evict RefreshChannelItem failed for ExternalId={External}; next browse will catch up via DataVersion bump",
                    externalId);
            }

            _state.BumpDataVersion(kind);
            evicted++;

            _logger.LogInformation(
                "[Eviction] evicted ExternalId={External} stub_path={Path}",
                externalId, row.StubPath);
        }

        _logger.LogInformation(
            "[Eviction] tick done: evicted={Evicted} fav={Fav} recent={Recent} young={Young} orphan={Orphan} removeFailed={RemoveFailed}",
            evicted, skippedFav, skippedRecent, skippedYoung, skippedOrphan, removeFailed);
    }

    private UserItemData? SafeGetUserData(User user, BaseItem item)
    {
        try
        {
            return _userDataManager.GetUserData(user, item);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[Eviction] IUserDataManager.GetUserData threw for user={User} item={Item}; treating as no data",
                user?.Id, item?.Id);
            return null;
        }
    }
}

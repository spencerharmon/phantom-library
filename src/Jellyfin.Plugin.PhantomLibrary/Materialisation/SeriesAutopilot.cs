using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// On Episode playback &gt;= <see cref="EFFECTIVELY_FINISHED_THRESHOLD"/>,
/// pre-materialises upcoming Episodes; on Movie favourite, pre-resolves
/// the immediate sequel from the same TMDB collection.
/// </summary>
/// <remarks>
/// <para>
/// "Next Up" integration is implicit: Jellyfin natively computes Next Up
/// from items in the Series/Season/Episode hierarchy. Once the
/// pre-materialised next Episode exists with the correct
/// <c>ParentIndexNumber</c> + <c>IndexNumber</c>, Jellyfin surfaces it on
/// every client. No explicit Next-Up wiring beyond ingestor correctness
/// is required (PLAN.md §M8).
/// </para>
/// </remarks>
public interface ISeriesAutopilot
{
    Task OnEpisodePlaybackProgressAsync(Guid userId, Episode episode, double percentWatched, CancellationToken ct);
    Task OnMovieFavouritedAsync(Guid userId, Movie movie, CancellationToken ct);
    Task EnsureUpcomingMaterialisedAsync(Guid userId, Series series, int currentSeason, int currentEpisode, int prefetchWindow, CancellationToken ct);

    /// <summary>Clears the once-per-playback debounce for (user, episode).</summary>
    void ResetPlaybackDebounce(Guid userId, Guid episodeId);
}

/// <inheritdoc />
public sealed class SeriesAutopilot : ISeriesAutopilot, IHostedService, IDisposable
{
    /// <summary>
    /// Fraction of <see cref="MediaBrowser.Controller.Entities.BaseItem.RunTimeTicks"/>
    /// at which an episode is considered "effectively finished" and the
    /// autopilot may pre-materialise the next one. 0.80 chosen so binge
    /// auto-play and credits-rolling both trigger reliably.
    /// </summary>
#pragma warning disable CA1707, SA1310 // documented public threshold; SHOUTING_CASE intentional
    public const double EFFECTIVELY_FINISHED_THRESHOLD = 0.80;
#pragma warning restore CA1707, SA1310

    private readonly ITmdbClient _tmdb;
    private readonly ISeriesIngestor _ingestor;
    private readonly IMaterialisationQueue _queue;
    private readonly IMaterialiser _materialiser;
    private readonly IGostreamClient _gostream;
    private readonly PhantomDb _db;
    private readonly VirtualLibraryRoot _root;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<SeriesAutopilot> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    // Per-(user, episode) once-per-playback debounce for the >=80% trigger.
    private readonly ConcurrentDictionary<(Guid User, Guid Item), byte> _firedThisPlayback = new();

    public SeriesAutopilot(
        ITmdbClient tmdb,
        ISeriesIngestor ingestor,
        IMaterialisationQueue queue,
        IMaterialiser materialiser,
        IGostreamClient gostream,
        PhantomDb db,
        VirtualLibraryRoot root,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<SeriesAutopilot> logger)
        : this(tmdb, ingestor, queue, materialiser, gostream, db, root, libraryManager,
               userManager, userDataManager, logger,
               () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    public SeriesAutopilot(
        ITmdbClient tmdb,
        ISeriesIngestor ingestor,
        IMaterialisationQueue queue,
        IMaterialiser materialiser,
        IGostreamClient gostream,
        PhantomDb db,
        VirtualLibraryRoot root,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<SeriesAutopilot> logger,
        Func<PluginConfiguration> configProvider)
    {
        _tmdb = tmdb;
        _ingestor = ingestor;
        _queue = queue;
        _materialiser = materialiser;
        _gostream = gostream;
        _db = db;
        _root = root;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
        _configProvider = configProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _materialiser.LifecycleChanged += OnLifecycleChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _materialiser.LifecycleChanged -= OnLifecycleChanged;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _materialiser.LifecycleChanged -= OnLifecycleChanged; } catch { }
    }

    /// <summary>
    /// Clears the once-per-playback debounce for (user, episode). Called by
    /// listeners when a new playback session starts.
    /// </summary>
    public void ResetPlaybackDebounce(Guid userId, Guid episodeId)
    {
        _firedThisPlayback.TryRemove((userId, episodeId), out _);
    }

    public async Task OnEpisodePlaybackProgressAsync(Guid userId, Episode episode, double percentWatched, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var cfg = _configProvider();
        if (!cfg.SeriesAutopilotEnabled)
        {
            _logger.LogDebug("SeriesAutopilot disabled; ignoring playback progress for {Ep}", episode.Id);
            return;
        }

        if (percentWatched < EFFECTIVELY_FINISHED_THRESHOLD)
        {
            return;
        }

        if (!_firedThisPlayback.TryAdd((userId, episode.Id), 0))
        {
            return;
        }

        if (episode.IndexNumber is not int currentEp || episode.ParentIndexNumber is not int currentSeason)
        {
            _logger.LogDebug("Episode {Id} missing index numbers; cannot advance", episode.Id);
            return;
        }

        var series = episode.Series ?? WalkToSeries(episode);
        if (series is null)
        {
            _logger.LogDebug("Episode {Id} has no parent Series; cannot advance", episode.Id);
            return;
        }

        var prefetch = Math.Max(1, cfg.SeriesAutopilotPrefetchEpisodes);
        await EnsureUpcomingMaterialisedAsync(userId, series, currentSeason, currentEp, prefetch, ct)
            .ConfigureAwait(false);
    }

    public async Task OnMovieFavouritedAsync(Guid userId, Movie movie, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var cfg = _configProvider();
        if (!cfg.SeriesAutopilotEnabled)
        {
            return;
        }

        var tmdbStr = movie.ProviderIds?.GetValueOrDefault("Tmdb");
        if (string.IsNullOrWhiteSpace(tmdbStr)
            || !int.TryParse(tmdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var movieTmdb))
        {
            return;
        }

        TmdbMovieDetails? sequel;
        try
        {
            sequel = await _tmdb.GetMovieCollectionSequelAsync(movieTmdb, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sequel lookup failed for tmdb={Tmdb}", movieTmdb);
            return;
        }

        if (sequel is null)
        {
            _logger.LogDebug("No sequel found for tmdb={Tmdb}", movieTmdb);
            return;
        }

        // Reuse existing Virtual if already created.
        var existing = FindMovieByTmdb(sequel.Id);
        Guid newId;
        if (existing is null)
        {
            var newMovie = VirtualItemFactory.CreateVirtualMovie(sequel);
            newMovie.Id = _libraryManager.GetNewItemId(
                $"phantom_movie_{sequel.Id.ToString(CultureInfo.InvariantCulture)}", newMovie.GetType());
            var parent = _root.ResolveMoviesParent() ?? _libraryManager.GetUserRootFolder();
            try
            {
                _libraryManager.CreateItem(newMovie, parent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sequel CreateItem failed for tmdb={Tmdb}", sequel.Id);
                return;
            }

            await _db.UpsertPhantomItemAsync(newMovie.Id, new PhantomItemRow
            {
                TmdbId = sequel.Id,
                ImdbId = sequel.ImdbId,
                Type = "movie",
                State = PhantomItemState.Virtual,
                FirstSeen = DateTimeOffset.UtcNow,
                LastTouched = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            newId = newMovie.Id;
            _logger.LogInformation("Sequel virtualised tmdb={Tmdb} ({Name})", sequel.Id, sequel.Title);
        }
        else
        {
            newId = existing.Id;
        }

        _queue.EnqueueEager(newId);
    }

    public async Task EnsureUpcomingMaterialisedAsync(Guid userId, Series series, int currentSeason, int currentEpisode, int prefetchWindow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (prefetchWindow <= 0) return;

        var seriesTmdbStr = series.ProviderIds?.GetValueOrDefault("Tmdb");
        if (string.IsNullOrWhiteSpace(seriesTmdbStr)
            || !int.TryParse(seriesTmdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seriesTmdb))
        {
            _logger.LogDebug("Series {Id} missing TMDB id; autopilot cannot advance", series.Id);
            return;
        }

        var seriesImdb = series.ProviderIds?.GetValueOrDefault("Imdb") ?? string.Empty;

        var seasonCache = new Dictionary<int, TmdbSeasonDetails?>();
        async Task<TmdbSeasonDetails?> GetSeasonAsync(int s)
        {
            if (seasonCache.TryGetValue(s, out var c)) return c;
            var d = await _tmdb.GetSeasonAsync(seriesTmdb, s, null, ct).ConfigureAwait(false);
            seasonCache[s] = d;
            return d;
        }

        var (curS, curE) = (currentSeason, currentEpisode);
        var lastQueued = (Season: currentSeason, Episode: currentEpisode);
        var queuedCount = 0;
        for (var i = 0; i < prefetchWindow; i++)
        {
            var next = await AdvanceAsync(curS, curE, GetSeasonAsync).ConfigureAwait(false);
            if (next is null)
            {
                _logger.LogInformation("Series tmdb={Tmdb} reached end-of-series after s{S}e{E}", seriesTmdb, curS, curE);
                break;
            }

            var (ns, ne) = next.Value;
            try
            {
                var episodeItem = await _ingestor.EnsureEpisodeAsync(seriesTmdb, ns, ne, ct).ConfigureAwait(false);
                _queue.EnqueueUser(episodeItem.Id, MaterialiseTrigger.Autopilot);
                lastQueued = (ns, ne);
                queuedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Autopilot EnsureEpisode failed for tmdb={Tmdb} s{S}e{E}", seriesTmdb, ns, ne);
                break;
            }

            curS = ns;
            curE = ne;
        }

        if (queuedCount > 0 && !string.IsNullOrWhiteSpace(seriesImdb))
        {
            await _db.UpsertAutopilotStateAsync(new AutopilotStateRow
            {
                UserId = userId,
                SeriesImdb = seriesImdb,
                LastPlayedSeason = currentSeason,
                LastPlayedEpisode = currentEpisode,
                NextMaterialisedSeason = lastQueued.Season,
                NextMaterialisedEpisode = lastQueued.Episode,
                PrefetchCursorSeason = lastQueued.Season,
                PrefetchCursorEpisode = lastQueued.Episode,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(int Season, int Episode)?> AdvanceAsync(int s, int e, Func<int, Task<TmdbSeasonDetails?>> getSeason)
    {
        var current = await getSeason(s).ConfigureAwait(false);
        if (current is null) return null;

        var hasNextInSeason = current.Episodes.Any(ep => ep.EpisodeNumber == e + 1);
        if (hasNextInSeason)
        {
            return (s, e + 1);
        }

        // Try next season's episode 1.
        var nextSeason = await getSeason(s + 1).ConfigureAwait(false);
        if (nextSeason is not null && nextSeason.Episodes.Any(ep => ep.EpisodeNumber == 1))
        {
            return (s + 1, 1);
        }

        return null;
    }

    private Series? WalkToSeries(Episode episode)
    {
        var p = episode.GetParent();
        while (p is not null)
        {
            if (p is Series s) return s;
            p = p.GetParent();
        }

        return null;
    }

    private Movie? FindMovieByTmdb(int tmdbId)
    {
        try
        {
            var q = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
                HasAnyProviderId = new Dictionary<string, string>
                {
                    ["Tmdb"] = tmdbId.ToString(CultureInfo.InvariantCulture),
                },
                Limit = 1,
            };
            var matches = _libraryManager.GetItemList(q);
            return matches.Count > 0 ? matches[0] as Movie : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnLifecycleChanged(object? sender, MaterialisationLifecycleEvent e)
    {
        // Vault Mode prestage hand-off: when an Episode finishes materialising,
        // and the underlying gostream exposes Vault Mode, request a prestage
        // for the stub at priority 50 — but only if at least one user has
        // IsFavorite=true on the item (or its parent Series/Movie) AND that
        // user's ProtectFavourites pref is on. Best-effort; failures are logged.
        if (e.Phase != MaterialisationLifecyclePhase.Finished) return;
        if (e.Outcome?.Status != MaterialisationStatus.Success) return;
        var stub = e.Outcome.StubPath;
        if (string.IsNullOrWhiteSpace(stub)) return;
        var itemId = e.ItemId;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await _gostream.IsVaultModePresentAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    return;
                }

                if (!await AnyUserProtectFavouriteAsync(itemId, CancellationToken.None).ConfigureAwait(false))
                {
                    _logger.LogDebug("Vault prestage skipped for {Stub}: no protecting favourite", stub);
                    return;
                }

                await _gostream.PrestageAsync(stub!, 50, CancellationToken.None).ConfigureAwait(false);
                _logger.LogDebug("Vault prestage requested for {Stub}", stub);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Vault prestage failed for {Stub}", stub);
            }
        });
    }

    private async Task<bool> AnyUserProtectFavouriteAsync(Guid itemId, CancellationToken ct)
    {
        MediaBrowser.Controller.Entities.BaseItem? item;
        try { item = _libraryManager.GetItemById(itemId); }
        catch { return false; }
        if (item is null) return false;

        // Candidates: the item itself + (for Episode) its Series + (for Movie) itself only.
        var candidates = new List<MediaBrowser.Controller.Entities.BaseItem> { item };
        if (item is Episode ep)
        {
            var s = ep.Series ?? WalkToSeries(ep);
            if (s is not null) candidates.Add(s);
        }

        IEnumerable<Jellyfin.Database.Implementations.Entities.User> users;
        try { users = _userManager.GetUsers(); }
        catch { return false; }

        foreach (var user in users)
        {
            bool isFav = false;
            foreach (var c in candidates)
            {
                try
                {
                    var ud = _userDataManager.GetUserData(user, c);
                    if (ud is not null && ud.IsFavorite) { isFav = true; break; }
                }
                catch { }
            }

            if (!isFav) continue;

            var prefs = await _db.GetUserPrefsAsync(user.Id, ct).ConfigureAwait(false);
            if (prefs.ProtectFavourites) return true;
        }

        return false;
    }
}

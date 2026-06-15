using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Stage-2.1 stub. Real autopilot rewritten in Stage 5.2 per plan.
/// </summary>
public interface ISeriesAutopilot
{
    Task OnEpisodePlaybackProgressAsync(Guid userId, Episode episode, double percentWatched, CancellationToken ct);
    Task OnMovieFavouritedAsync(Guid userId, Movie movie, CancellationToken ct);
    Task EnsureUpcomingMaterialisedAsync(Guid userId, Series series, int currentSeason, int currentEpisode, int prefetchWindow, CancellationToken ct);
    void ResetPlaybackDebounce(Guid userId, Guid episodeId);
}

/// <inheritdoc />
public sealed class SeriesAutopilot : ISeriesAutopilot, IHostedService
{
    private readonly ILogger<SeriesAutopilot> _logger;

    public SeriesAutopilot(ILogger<SeriesAutopilot> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[SeriesAutopilot] stage-2.1 stub; rewritten in Stage 5.2");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnEpisodePlaybackProgressAsync(Guid userId, Episode episode, double percentWatched, CancellationToken ct)
        => Task.CompletedTask;

    public Task OnMovieFavouritedAsync(Guid userId, Movie movie, CancellationToken ct)
        => Task.CompletedTask;

    public Task EnsureUpcomingMaterialisedAsync(Guid userId, Series series, int currentSeason, int currentEpisode, int prefetchWindow, CancellationToken ct)
        => Task.CompletedTask;

    public void ResetPlaybackDebounce(Guid userId, Guid episodeId)
    {
    }
}

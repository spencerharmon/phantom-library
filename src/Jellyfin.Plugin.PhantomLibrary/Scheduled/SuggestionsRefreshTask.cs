using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// Jellyfin <see cref="IScheduledTask"/> that periodically pulls TMDB
/// Trending + per-user Recommended surfaces into the library as Virtual
/// items. Default interval is 6h, aligning with the trending TTL.
/// </summary>
public sealed class SuggestionsRefreshTask : IScheduledTask
{
    private readonly ISuggestionsContributor _contributor;
    private readonly ILogger<SuggestionsRefreshTask> _logger;

    public SuggestionsRefreshTask(ISuggestionsContributor contributor, ILogger<SuggestionsRefreshTask> logger)
    {
        _contributor = contributor;
        _logger = logger;
    }

    public string Name => "Phantom Library — refresh suggestions";

    public string Key => "PhantomLibrarySuggestionsRefresh";

    public string Description =>
        "Pulls TMDB Trending and per-user Recommended into the library as Virtual items.";

    public string Category => "Phantom Library";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0d);
        int total;
        try
        {
            progress.Report(50d);
            total = await _contributor.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            progress.Report(100d);
        }

        _logger.LogInformation("SuggestionsRefreshTask: total Virtual items created/touched = {Total}", total);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks,
            },
        };
    }
}

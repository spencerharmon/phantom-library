using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.Swarm;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// ROI Priority 0.5: the "Healthiest swarms" Home shelf — visible titles ranked
/// by their freshly measured DHT swarm size (see <see cref="SwarmHealthScore"/>).
/// Omitted while swarm health is disabled or nothing visible has a live swarm.
/// </summary>
internal static class SwarmHealthRows
{
    public const string Title = "Healthiest swarms";

    public static async Task<IReadOnlyList<CuratedRow>> AppendHealthiestAsync(
        IReadOnlyList<CuratedRow> rows,
        IReadOnlyList<ChannelItemInfo> visibleItems,
        PhantomDb db,
        string swarmType,
        PluginConfiguration config,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.SwarmHealthServiceUrl))
        {
            return rows;
        }

        var peers = await db.GetSwarmPeersByTmdbAsync(
            swarmType,
            DateTimeOffset.UtcNow.AddDays(-config.SwarmHealthRankWindowDays),
            ct).ConfigureAwait(false);
        var members = SwarmHealthScore.HealthiestRow(visibleItems, peers, config.CuratedRowSize);
        if (members.Count == 0)
        {
            return rows;
        }

        var result = rows.ToList();
        result.Add(new CuratedRow { Key = CuratedRows.KeyHealthiestSwarms, Title = Title, Items = members });
        return result;
    }
}

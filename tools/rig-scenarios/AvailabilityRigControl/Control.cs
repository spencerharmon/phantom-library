// TEST ONLY: never referenced by src/, packaged, or installed outside the isolated rig.
using System.Net;
using System.Reflection;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using PhantomPlugin = Jellyfin.Plugin.PhantomLibrary.Plugin;

namespace AvailabilityRigControl;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Rig/Availability")]
public sealed class Control(PhantomDb db, ChannelStateProvider state, IEnumerable<IHostedService> services) : ControllerBase
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static int ticks;

    private void Guard()
    {
        if (Environment.GetEnvironmentVariable("PHANTOM_RIG_AVAILABILITY_CONTROL") != "1"
            || HttpContext.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Test-only control requires an explicit isolated loopback rig");
        }
    }

    [HttpGet("Visible")]
    public async Task<object> Visible(CancellationToken ct)
    {
        Guard();
        // Execute the candidate DLL's actual repository methods, not copied SQL.
        var movies = await db.ListVisibleMovieRowsAsync(ct);
        var series = await db.ListVisibleSeriesRowsAsync(1, ct);
        return new { Movies = movies.Select(x => x.Metadata.TmdbId), Series = series.Select(x => x.Metadata.TmdbId) };
    }

    [HttpGet("Policy")]
    public object Policy()
    {
        Guard();
        var cfg = PhantomPlugin.Instance!.Configuration;
        var hash = typeof(AvailabilityProbeWorker).GetMethod("ComputePolicyHash", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [cfg]);
        return new { Hash = hash, Ticks = ticks, Enabled = cfg.AvailabilityProbeEnabled,
            MoviesVersion = state.DataVersion(ChannelStateProvider.KindMovies),
            ShowsVersion = state.DataVersion(ChannelStateProvider.KindShows) };
    }

    [HttpPost("Tick")]
    public async Task<object> Tick(CancellationToken ct)
    {
        Guard();
        await Gate.WaitAsync(ct);
        var cfg = PhantomPlugin.Instance!.Configuration;
        try
        {
            // The real hosted instance has a one-hour timer and is disabled at boot.
            // No replacement worker/probe delegate/DB. Exactly one awaited TickAsync.
            if (cfg.AvailabilityProbeEnabled || cfg.AvailabilityProbeMinIntervalSeconds < 3600
                || cfg.AvailabilityProbeMaxIntervalSeconds < 3600)
            {
                throw new InvalidOperationException("Rig worker is not quiescent");
            }
            var worker = services.OfType<AvailabilityProbeWorker>().Single();
            cfg.AvailabilityProbeEnabled = true;
            await (Task)typeof(AvailabilityProbeWorker).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(worker, [ct])!;
            return new { Ticks = ++ticks };
        }
        finally
        {
            cfg.AvailabilityProbeEnabled = false;
            Gate.Release();
        }
    }
}

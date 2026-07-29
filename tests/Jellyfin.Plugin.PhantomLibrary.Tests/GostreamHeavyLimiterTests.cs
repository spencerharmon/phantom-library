using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class GostreamHeavyLimiterTests
{
    [Fact]
    public async Task AcquireAsync_RespectsConfiguredConcurrency()
    {
        var cfg = new PluginConfiguration { GostreamHeavyConcurrency = 1 };
        using var limiter = new GostreamHeavyLimiter(() => cfg);
        var active = 0;
        var maxActive = 0;
        var tasks = new List<Task>();

        for (var i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                using (await limiter.AcquireAsync(CancellationToken.None))
                {
                    var now = Interlocked.Increment(ref active);
                    maxActive = Math.Max(maxActive, now);
                    await Task.Delay(25);
                    Interlocked.Decrement(ref active);
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task AcquireAsync_UsesUpdatedCapacityWithoutDeadlock()
    {
        var cfg = new PluginConfiguration { GostreamHeavyConcurrency = 1 };
        using var limiter = new GostreamHeavyLimiter(() => cfg);

        using (await limiter.AcquireAsync(CancellationToken.None))
        {
            cfg.GostreamHeavyConcurrency = 2;
            using var second = await limiter.AcquireAsync(CancellationToken.None);
        }
    }
}

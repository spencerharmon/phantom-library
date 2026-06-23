using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class MaterialisationQueueTests
{
    private sealed class RecorderMat : IMaterialiser
    {
        public readonly List<(Guid id, MaterialiseTrigger trigger)> Calls = new();
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _wait;

        public RecorderMat(bool wait = false) { _wait = wait; }

        public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged
        {
            add { _ = value; }
            remove { _ = value; }
        }

        public async Task<MaterialisationOutcome> MaterialiseAsync(Guid id, MaterialiseTrigger trigger, CancellationToken ct)
        {
            lock (Calls) Calls.Add((id, trigger));
            if (_wait) await Gate.Task.WaitAsync(ct);
            return new MaterialisationOutcome { Status = MaterialisationStatus.Success };
        }

        public Task<MaterialisationOutcome> MaterialiseAsync(int tmdbId, string type, int? season, int? episode, MaterialiseTrigger trigger, CancellationToken ct)
            => Task.FromResult(new MaterialisationOutcome { Status = MaterialisationStatus.Success });

        public Task<MaterialisationOutcome> MaterialiseAsync(int tmdbId, string type, int? season, int? episode, MagnetCandidate selectedCandidate, MaterialiseTrigger trigger, CancellationToken ct)
            => Task.FromResult(new MaterialisationOutcome { Status = MaterialisationStatus.Success });
    }

    private static ServiceProvider BuildSp(IMaterialiser m)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(m);
        return sc.BuildServiceProvider();
    }

    [Fact]
    public void Enqueue_Dedup_Inflight_Returns_NoOp()
    {
        var mat = new RecorderMat(wait: true);
        var sp = BuildSp(mat);
        var cfg = new PluginConfiguration { MaterialisationConcurrencyGlobal = 1 };
        using var q = new MaterialisationQueue(sp, NullLogger<MaterialisationQueue>.Instance, () => cfg);

        var id = Guid.NewGuid();
        q.EnqueueUser(id, MaterialiseTrigger.Manual);
        q.EnqueueUser(id, MaterialiseTrigger.Manual);
        q.EnqueueUser(id, MaterialiseTrigger.Manual);
        // Only one enqueued because subsequent are deduped while in-flight.
        Assert.True(q.PendingUserCount <= 1);
    }

    [Fact]
    public async Task User_Lane_Preferred_Over_Eager()
    {
        var mat = new RecorderMat();
        var sp = BuildSp(mat);
        var cfg = new PluginConfiguration { MaterialisationConcurrencyGlobal = 1, EagerResolveMaxConcurrent = 2 };
        using var q = new MaterialisationQueue(sp, NullLogger<MaterialisationQueue>.Instance, () => cfg);

        // Pre-populate queues BEFORE starting workers so we control ordering.
        var eager1 = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var eager2 = Guid.NewGuid();
        q.EnqueueEager(eager1);
        q.EnqueueUser(user1, MaterialiseTrigger.Manual);
        q.EnqueueEager(eager2);

        await q.StartAsync(CancellationToken.None);
        // wait for all three to drain
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (mat.Calls.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await q.StopAsync(CancellationToken.None);
        Assert.Equal(3, mat.Calls.Count);
        // User should have been drained before any eager *that arrived
        // before the worker started*. Specifically: user1 should run before eager2.
        var userIdx = mat.Calls.FindIndex(c => c.id == user1);
        var eager2Idx = mat.Calls.FindIndex(c => c.id == eager2);
        Assert.True(userIdx < eager2Idx, $"user1 (idx {userIdx}) should run before eager2 (idx {eager2Idx})");
    }

    [Fact]
    public async Task Graceful_Shutdown_Returns_Promptly()
    {
        var mat = new RecorderMat();
        var sp = BuildSp(mat);
        var cfg = new PluginConfiguration { MaterialisationConcurrencyGlobal = 2 };
        using var q = new MaterialisationQueue(sp, NullLogger<MaterialisationQueue>.Instance, () => cfg);
        await q.StartAsync(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await q.StopAsync(CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(11));
    }
}

using System;
using System.Threading;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// ttfb-warmup-userless-root-churn: marks the current async flow as the
/// <c>ChannelBaseItemWarmupWorker</c> root warmup. Core's
/// <c>RefreshLatestChannelItems</c> and the warmup both reach the channels as a
/// userless (UserId=Guid.Empty, no FolderId) root query; without this marker
/// the warmup hit the bounded latest-only branch (~16-20 items), and core then
/// deleted every other persisted channel BaseItem — continuous mass
/// delete/recreate churn, 40P01 deadlocks and 23503 FK list errors. Inside the
/// scope the channels return the FULL canonical root catalogue (no per-user
/// hidden subtraction), and use a distinct cache key so the latest-only result
/// cached for the userless Home Latest query is never served to the warmup (or
/// vice versa). AsyncLocal so it flows through core's awaits into
/// <c>IChannel.GetChannelItems</c> / <c>IHasCacheKey.GetCacheKey</c> without
/// leaking to concurrent requests.
/// </summary>
internal static class ChannelWarmupScope
{
    internal const string CacheKey = "phantom-warmup-full-root";

    private static readonly AsyncLocal<bool> Active = new();

    internal static bool IsActive => Active.Value;

    internal static IDisposable Enter()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Restore(previous);
    }

    /// <summary>Cache key for IHasCacheKey: partitions the warmup's full root from the userless latest root.</summary>
    internal static string? CacheKeyFor(string? userId) => IsActive ? CacheKey : userId;

    private sealed class Restore : IDisposable
    {
        private readonly bool _previous;

        public Restore(bool previous) => _previous = previous;

        public void Dispose() => Active.Value = _previous;
    }
}

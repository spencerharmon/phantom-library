using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// Stable internal Guids for the two phantom channels. These ids are
/// what Jellyfin's <c>ChannelManager.GetInternalChannelId</c> would
/// compute from each channel's <see cref="IChannel.Name"/>; precomputing
/// them lets feature code (Materialiser, badge controller, etc.) call
/// <c>RefreshChannelItemAsync(channelId, …)</c> without having to
/// resolve the channel through <c>ILibraryManager</c> on every call.
///
/// Derivation matches Jellyfin's
/// <c>LibraryManager.GetNewItemId("Channel " + name, typeof(Channel))</c>:
///
///   1. <c>type.FullName</c> ("MediaBrowser.Controller.Channels.Channel")
///   2. lowercased key ("channel phantom movies") when
///      <c>EnableCaseSensitiveItemIds</c> is false (default).
///   3. MD5 of the UTF-16 (Unicode) bytes of the concatenation.
///
/// We do NOT depend on <c>ILibraryManager</c> here because the values
/// must be available before DI is fully wired (e.g. in static field
/// initialisers in the channel classes themselves). If
/// <c>EnableCaseSensitiveItemIds</c> is ever enabled in the operator's
/// config, this needs to switch to the case-sensitive form; the plugin
/// would then need to read the config to decide. For now (operator
/// has not changed that default) the case-insensitive form is fixed.
///
/// Hardcoded channel display names per plan §"Operator-accepted
/// regressions" — renaming would invalidate <c>BaseItem.Id</c>
/// derivation and wipe UserData.
/// </summary>
public static class ChannelIds
{
    public const string MoviesName = "Phantom Movies";
    public const string ShowsName = "Phantom Shows";

    private const string ChannelTypeFullName = "MediaBrowser.Controller.Channels.Channel";

    /// <summary>
    /// Internal Guid for the "Phantom Movies" channel.
    /// </summary>
    public static Guid Movies { get; } = ComputeChannelId(MoviesName);

    /// <summary>
    /// Internal Guid for the "Phantom Shows" channel.
    /// </summary>
    public static Guid Shows { get; } = ComputeChannelId(ShowsName);

    /// <summary>
    /// Resolve a kind tag ("movies" or "shows") to its channel id.
    /// </summary>
    public static Guid For(string kind)
    {
        return kind switch
        {
            "movies" => Movies,
            "shows" => Shows,
            _ => throw new ArgumentException("Unknown channel kind: " + kind, nameof(kind)),
        };
    }

    /// <summary>
    /// True iff <paramref name="channelId"/> is one of the phantom
    /// channels managed by this plugin.
    /// </summary>
    public static bool IsPhantom(Guid channelId)
        => channelId == Movies || channelId == Shows;

    private static Guid ComputeChannelId(string channelName)
    {
        // ChannelManager.GetInternalChannelId(name)
        //   → LibraryManager.GetNewItemId("Channel " + name, typeof(Channel))
        //   → key.ToLowerInvariant() (when EnableCaseSensitiveItemIds=false)
        //   → type.FullName + key
        //   → MD5(Unicode bytes)
#pragma warning disable CA1308 // ToLower is correct: matches Jellyfin's own algorithm exactly
        var key = ChannelTypeFullName + ("Channel " + channelName).ToLowerInvariant();
#pragma warning restore CA1308
#pragma warning disable CA5351 // MD5 here is identity-hash, not cryptography
        var bytes = MD5.HashData(Encoding.Unicode.GetBytes(key));
#pragma warning restore CA5351
        return new Guid(bytes);
    }
}

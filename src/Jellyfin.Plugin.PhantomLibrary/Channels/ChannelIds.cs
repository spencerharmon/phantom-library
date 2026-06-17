using System;

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

    /// <summary>
    /// Internal Guid for the "Phantom Movies" channel as emitted by Jellyfin
    /// 10.11.9's ChannelManager for <see cref="MoviesName"/>. Verified
    /// against /Channels in the real rig and production.
    /// </summary>
    public static Guid Movies { get; } = Guid.Parse("80089d10-394f-b545-b5e4-d7d56a872393");

    /// <summary>
    /// Internal Guid for the "Phantom Shows" channel as emitted by Jellyfin
    /// 10.11.9's ChannelManager for <see cref="ShowsName"/>. Verified
    /// against /Channels in the real rig and production.
    /// </summary>
    public static Guid Shows { get; } = Guid.Parse("40ab6e9a-f516-a84f-46dc-ea7140855d88");

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

}

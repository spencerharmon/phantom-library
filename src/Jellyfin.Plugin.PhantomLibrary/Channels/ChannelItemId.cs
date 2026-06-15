using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// Stable, parseable external id for items emitted by the phantom
/// channels. The encoded string is what gets stored in
/// <c>BaseItem.ExternalId</c> by ChannelManager; the derived
/// <c>BaseItem.Id</c> hashes off it, so the id must be stable across
/// the phantom → materialised transition to preserve UserData.
///
/// Format:
/// <code>
///   movie_&lt;tmdb&gt;
///   series_&lt;tmdb&gt;
///   season_&lt;tmdb&gt;_s&lt;NN&gt;
///   episode_&lt;tmdb&gt;_s&lt;NN&gt;e&lt;NN&gt;
///   orphan_&lt;hex16&gt;
/// </code>
///
/// The episode form pads season + episode numbers to two digits, and
/// supports overflow past 99 by emitting the natural number unpadded
/// in that range (parser accepts variable-width). The orphan hash is
/// a 16-char hex SHA1 prefix of the absolute file path; stable for
/// the same path across calls, distinct across paths.
///
/// IMPORTANT (plan §2.3, critic round 3 BLOCKER 1 fix): the id does
/// NOT encode materialise state. <c>ForMovie(42)</c> returns
/// <c>"movie_42"</c> regardless of whether tmdb=42 has a row in
/// <c>materialised_state</c>. The channel decides MediaSources at
/// query time; the BaseItem id stays stable across materialise.
/// </summary>
public sealed record ChannelItemId(
    string Kind,
    int? TmdbId,
    int? Season,
    int? Episode,
    string? OrphanHash)
{
    public const string KindMovie = "movie";
    public const string KindSeries = "series";
    public const string KindSeason = "season";
    public const string KindEpisode = "episode";
    public const string KindOrphan = "orphan";

    /// <summary>
    /// Sentinel value used in <c>materialised_state</c> /
    /// <c>materialise_in_flight</c> primary keys when season or
    /// episode is "not applicable" (movies). SQLite treats NULL as
    /// distinct in UNIQUE/PK constraints, so a real integer sentinel
    /// is required. See plan §2.2 + critic v2 BLOCKER 3.
    /// </summary>
    public const int Sentinel = -1;

    /// <summary>
    /// Encode this id to its on-the-wire string form.
    /// </summary>
    public string Encode()
    {
        return Kind switch
        {
            KindMovie => string.Create(CultureInfo.InvariantCulture, $"movie_{TmdbId!.Value}"),
            KindSeries => string.Create(CultureInfo.InvariantCulture, $"series_{TmdbId!.Value}"),
            KindSeason => string.Create(CultureInfo.InvariantCulture, $"season_{TmdbId!.Value}_s{Season!.Value:00}"),
            KindEpisode => string.Create(CultureInfo.InvariantCulture, $"episode_{TmdbId!.Value}_s{Season!.Value:00}e{Episode!.Value:00}"),
            KindOrphan => "orphan_" + OrphanHash,
            _ => throw new InvalidOperationException("Unknown ChannelItemId kind: " + Kind),
        };
    }

    /// <summary>
    /// Parse a previously-encoded id, throwing on malformed input.
    /// </summary>
    public static ChannelItemId Parse(string s)
    {
        if (!TryParse(s, out var id))
        {
            throw new FormatException("Not a valid ChannelItemId: " + s);
        }

        return id;
    }

    /// <summary>
    /// Try to parse a previously-encoded id. Returns false on
    /// malformed input; <paramref name="id"/> is set to a sentinel
    /// non-null value in that case to satisfy the analyzer.
    /// </summary>
    public static bool TryParse(string? s, out ChannelItemId id)
    {
        id = new ChannelItemId(string.Empty, null, null, null, null);
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        // orphan_<hex>
        if (s.StartsWith("orphan_", StringComparison.Ordinal))
        {
            var hash = s.Substring("orphan_".Length);
            if (hash.Length == 0 || !IsHex(hash))
            {
                return false;
            }

            id = new ChannelItemId(KindOrphan, null, null, null, hash);
            return true;
        }

        // movie_<tmdb>
        if (s.StartsWith("movie_", StringComparison.Ordinal))
        {
            var rest = s.Substring("movie_".Length);
            if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdb))
            {
                return false;
            }

            id = new ChannelItemId(KindMovie, tmdb, null, null, null);
            return true;
        }

        // series_<tmdb>
        if (s.StartsWith("series_", StringComparison.Ordinal))
        {
            var rest = s.Substring("series_".Length);
            if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdb))
            {
                return false;
            }

            id = new ChannelItemId(KindSeries, tmdb, null, null, null);
            return true;
        }

        // episode_<tmdb>_s<NN>e<NN>
        if (s.StartsWith("episode_", StringComparison.Ordinal))
        {
            var rest = s.Substring("episode_".Length);
            var sIdx = rest.IndexOf("_s", StringComparison.Ordinal);
            if (sIdx <= 0)
            {
                return false;
            }

            if (!int.TryParse(rest.AsSpan(0, sIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdb))
            {
                return false;
            }

            var afterS = rest.Substring(sIdx + 2);
            var eIdx = afterS.IndexOf('e', StringComparison.Ordinal);
            if (eIdx <= 0)
            {
                return false;
            }

            if (!int.TryParse(afterS.AsSpan(0, eIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
            {
                return false;
            }

            if (!int.TryParse(afterS.AsSpan(eIdx + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode))
            {
                return false;
            }

            id = new ChannelItemId(KindEpisode, tmdb, season, episode, null);
            return true;
        }

        // season_<tmdb>_s<NN>
        if (s.StartsWith("season_", StringComparison.Ordinal))
        {
            var rest = s.Substring("season_".Length);
            var sIdx = rest.IndexOf("_s", StringComparison.Ordinal);
            if (sIdx <= 0)
            {
                return false;
            }

            if (!int.TryParse(rest.AsSpan(0, sIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdb))
            {
                return false;
            }

            if (!int.TryParse(rest.AsSpan(sIdx + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
            {
                return false;
            }

            id = new ChannelItemId(KindSeason, tmdb, season, null, null);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Translate nullable season/episode to the DB sentinel form.
    /// Movies pass <c>(null, null)</c>; result is <c>(-1, -1)</c>.
    /// </summary>
    public static (int Season, int Episode) ToSentinels(int? season, int? episode)
    {
        return (season ?? Sentinel, episode ?? Sentinel);
    }

    /// <summary>
    /// Translate DB sentinel form back to nullable. A stored value of
    /// <c>-1</c> becomes <c>null</c>; any other value passes through.
    /// </summary>
    public static (int? Season, int? Episode) FromSentinels(int season, int episode)
    {
        return (season == Sentinel ? (int?)null : season,
                episode == Sentinel ? (int?)null : episode);
    }

    // ---- factories ------------------------------------------------

    public static ChannelItemId ForMovie(int tmdb)
        => new(KindMovie, tmdb, null, null, null);

    public static ChannelItemId ForSeries(int tmdb)
        => new(KindSeries, tmdb, null, null, null);

    public static ChannelItemId ForSeason(int seriesTmdb, int seasonNumber)
        => new(KindSeason, seriesTmdb, seasonNumber, null, null);

    public static ChannelItemId ForEpisode(int seriesTmdb, int seasonNumber, int episodeNumber)
        => new(KindEpisode, seriesTmdb, seasonNumber, episodeNumber, null);

    /// <summary>
    /// Build an orphan id from an absolute file path. The hash is a
    /// 16-character hex SHA1 prefix of the UTF-8 bytes of the path —
    /// stable for the same path across calls. A path rename produces
    /// a new id; UserData on the prior orphan id is lost (acceptable,
    /// orphan files are by definition things the plugin did not put
    /// there).
    /// </summary>
    public static ChannelItemId ForOrphanPath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        // Non-cryptographic hash: we want a stable, collision-resistant
        // identifier from a path, not a security primitive. SHA1 of the
        // UTF-8 bytes, truncated to 16 hex chars (64 bits), is
        // sufficient given the small per-channel orphan population.
#pragma warning disable CA5350
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(absolutePath));
#pragma warning restore CA5350
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return new ChannelItemId(KindOrphan, null, null, null, sb.ToString());
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}

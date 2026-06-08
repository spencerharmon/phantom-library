using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Helpers for recognising "is this one of ours" across both the
/// legacy <c>__phantom_tmdb&lt;id&gt;</c> filename-sentinel scheme
/// (originally introduced in §M10) and the Jellyfin-native
/// <c>[tmdbid-&lt;id&gt;]</c> path-token scheme introduced by the
/// stub-layout spike.
///
/// Used by dedupe / heal / eviction / migration code paths as the
/// canonical "safe to mutate / safe to overwrite" signal. Both string-
/// based checks (filename / Name fields) and path-based checks (full
/// absolute paths with directory segments) are supported by the same
/// substring-matching implementation.
/// </summary>
public static class PhantomPathUtilities
{
    internal const string LegacySentinel = "__phantom_tmdb";
    internal const string NewTokenPrefix = "[tmdbid-";

    // Anchored to non-digit / EOL so tmdb=12 does not match tmdb=12345.
    private static readonly Regex LegacyRegex = new(
        @"__phantom_tmdb(?<id>\d+)", RegexOptions.Compiled);

    private static readonly Regex NewTokenRegex = new(
        @"\[tmdbid-(?<id>\d+)\]", RegexOptions.Compiled);

    /// <summary>
    /// True if any segment of <paramref name="path"/> (filename or any
    /// directory component) carries either the legacy
    /// <c>__phantom_tmdb&lt;n&gt;</c> sentinel or the new
    /// <c>[tmdbid-&lt;n&gt;]</c> Jellyfin-native token. Works equally well
    /// against bare strings (e.g. a BaseItem.Name) — the implementation
    /// is purely substring-based.
    /// </summary>
    public static bool IsPhantomStubPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.Contains(LegacySentinel, StringComparison.Ordinal)
            || NewTokenRegex.IsMatch(path);
    }

    /// <summary>
    /// Extracts the tmdb id from either sentinel form. Returns null if
    /// neither is present. Inspects the full input string (filename and
    /// every directory segment).
    /// </summary>
    public static int? TryParseTmdbId(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var m = NewTokenRegex.Match(path);
        if (m.Success && int.TryParse(m.Groups["id"].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        m = LegacyRegex.Match(path);
        if (m.Success && int.TryParse(m.Groups["id"].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var idLegacy))
        {
            return idLegacy;
        }

        return null;
    }

    /// <summary>
    /// True iff the path carries the legacy <c>__phantom_tmdb&lt;n&gt;</c>
    /// sentinel (i.e. needs migration to the new layout). Used by the
    /// one-shot stub-layout migration.
    /// </summary>
    public static bool IsLegacyStubPath(string? path)
        => !string.IsNullOrEmpty(path)
            && path!.Contains(LegacySentinel, StringComparison.Ordinal);
}

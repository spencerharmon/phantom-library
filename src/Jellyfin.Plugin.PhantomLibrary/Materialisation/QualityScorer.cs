using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Configurable scorer used by the materialiser to pick the best indexer
/// candidate. <see cref="QualityPreset.GostreamDefault"/> mirrors the
/// regex weighting in gostream's <c>internal/syncer/quality/scorer.go</c>.
/// <see cref="QualityPreset.Custom"/> is not yet implemented and falls
/// back to <see cref="QualityPreset.GostreamDefault"/> with a warning
/// (full implementation deferred to M9+).
/// </summary>
public sealed class QualityScorer
{
    // Regexes ported verbatim from gostream's quality/scorer.go.
    private static readonly Regex Re4K = new(@"(?i)(4k|2160p|uhd)", RegexOptions.Compiled);
    private static readonly Regex Re1080p = new(@"(?i)(1080p)", RegexOptions.Compiled);
    private static readonly Regex ReHDR = new(@"(?i)\bHDR\b", RegexOptions.Compiled);
    private static readonly Regex ReDV = new(@"(?i)(dolby.?vision|\bdv\b)", RegexOptions.Compiled);
    private static readonly Regex ReHDR10Plus = new(@"(?i)(hdr10\+|hdr10plus)", RegexOptions.Compiled);
    private static readonly Regex ReAtmos = new(@"(?i)(atmos)", RegexOptions.Compiled);
    private static readonly Regex Re51 = new(@"(?i)(5\.1|dts|ddp5|ddp|dd\+|eac3|ac3)", RegexOptions.Compiled);
    private static readonly Regex ReStereo = new(@"(?i)(stereo|aac|mp3|2\.0)", RegexOptions.Compiled);
    private static readonly Regex ReBluRay = new(@"(?i)(bluray|blu.?ray|bdrip|bdremux|remux)", RegexOptions.Compiled);
    private static readonly Regex ReRemux = new(@"(?i)\b(remux|bdremux)\b", RegexOptions.Compiled);
    private static readonly string[] DefaultResolutionOrder = { "1080p", "720p", "480p", "2160p", "4k", "unknown" };

    private readonly ILogger<QualityScorer> _logger;

    public QualityScorer(ILogger<QualityScorer> logger) { _logger = logger; }

    /// <summary>
    /// Selects the best candidate per the requested preset, applying seeder
    /// and size floors. Returns null if no candidate passes the floors.
    /// </summary>
    public IndexerCandidate? PickBest(
        IReadOnlyList<IndexerCandidate> candidates,
        QualityPreset preset,
        int minSeeders,
        int minSizeGb1080p,
        int minSizeGb4K,
        string? resolutionFallbackOrder = null,
        int seederWeight = 0,
        string? preferredResolution = null)
    {
        var ranked = RankCandidates(
            candidates,
            preset,
            minSeeders,
            minSizeGb1080p,
            minSizeGb4K,
            resolutionFallbackOrder,
            seederWeight,
            preferredResolution);
        return ranked.Count > 0 ? ranked[0] : null;
    }

    /// <summary>
    /// Returns all acceptable candidates sorted from most to least preferred
    /// under the requested preset. Applies the same floors as
    /// <see cref="PickBest"/> so retry-over-candidates preserves existing
    /// quality semantics instead of inventing a separate fallback order.
    /// </summary>
    public IReadOnlyList<IndexerCandidate> RankCandidates(
        IReadOnlyList<IndexerCandidate> candidates,
        QualityPreset preset,
        int minSeeders,
        int minSizeGb1080p,
        int minSizeGb4K,
        string? resolutionFallbackOrder = null,
        int seederWeight = 0,
        string? preferredResolution = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return Array.Empty<IndexerCandidate>();
        }

        var effective = preset;
        if (preset == QualityPreset.Custom)
        {
            _logger.LogWarning("Custom quality preset not yet implemented; falling back to GostreamDefault");
            effective = QualityPreset.GostreamDefault;
        }

        var min1080p = (long)minSizeGb1080p * 1024L * 1024L * 1024L;
        var min4K = (long)minSizeGb4K * 1024L * 1024L * 1024L;

        var filtered = candidates.Where(c =>
        {
            if (c.Seeders < minSeeders) return false;
            var is4K = Re4K.IsMatch(c.Title);
            var is1080 = Re1080p.IsMatch(c.Title);
            if (effective != QualityPreset.ResolutionSeeders && is4K && c.Size > 0 && c.Size < min4K) return false;
            if (effective != QualityPreset.ResolutionSeeders && !is4K && is1080 && c.Size > 0 && c.Size < min1080p) return false;
            // gostream's library.FilterVideoFiles only accepts the 1080p
            // (4-20 GB) or 4K (10-60 GB) size bands. A release with no
            // 1080p/4K tag whose size is below the 1080p floor is
            // effectively SD (HDRip, XviD, DVDRip, 720p, etc.) and will
            // be rejected downstream with no_valid_files after wasting a
            // torrent add. Reject up front so the materialiser reports
            // "no candidate passed quality floors" instead of the
            // misleading 422 the operator sees when an SD-only release
            // slips through.
            if (effective != QualityPreset.ResolutionSeeders && !is4K && !is1080 && c.Size > 0 && c.Size < min1080p) return false;
            return true;
        }).ToList();

        if (filtered.Count == 0)
        {
            return Array.Empty<IndexerCandidate>();
        }

        return effective switch
        {
            QualityPreset.BiggestMostSeeded => filtered
                .OrderByDescending(c => c.Size)
                .ThenByDescending(c => c.Seeders)
                .ToList(),
            QualityPreset.ResolutionSeeders => filtered
                .OrderByDescending(c => ScoreResolutionSeeders(c, resolutionFallbackOrder, seederWeight, preferredResolution))
                .ThenByDescending(c => c.Seeders)
                .ThenByDescending(c => c.Size)
                .ToList(),
            _ => filtered
                .OrderByDescending(ScoreGostream)
                .ThenByDescending(c => c.Seeders)
                .ThenByDescending(c => c.Size)
                .ToList(),
        };
    }

    public static int ScoreResolutionSeeders(IndexerCandidate c, string? resolutionFallbackOrder, int seederWeight, string? preferredResolution = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var order = ParseResolutionOrder(resolutionFallbackOrder, preferredResolution);
        var resolution = DetectResolution(c.Title);
        var index = order.FindIndex(token => string.Equals(token, resolution, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = order.FindIndex(token => string.Equals(token, "unknown", StringComparison.OrdinalIgnoreCase));
        }

        var score = Math.Max(0, order.Count - Math.Max(index, 0)) * 1000;
        score += Math.Max(0, c.Seeders) * Math.Max(0, seederWeight);
        if (ReRemux.IsMatch(c.Title)) score += 25;
        if (ReAtmos.IsMatch(c.Title)) score += 10;
        if (Re51.IsMatch(c.Title)) score += 5;
        return score;
    }

    private static List<string> ParseResolutionOrder(string? value, string? preferredResolution)
    {
        var tokens = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeResolutionToken)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var preferred = string.IsNullOrWhiteSpace(preferredResolution) ? string.Empty : NormalizeResolutionToken(preferredResolution);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            tokens.RemoveAll(token => string.Equals(token, preferred, StringComparison.OrdinalIgnoreCase));
            tokens.Insert(0, preferred);
        }

        if (tokens.Count == 0)
        {
            tokens.AddRange(DefaultResolutionOrder);
        }

        if (!tokens.Contains("unknown", StringComparer.OrdinalIgnoreCase))
        {
            tokens.Add("unknown");
        }

        return tokens;
    }

    private static string NormalizeResolutionToken(string value)
    {
#pragma warning disable CA1308 // Lowercase tokens are operator-facing config values, not identifiers used for round-trip display.
        var token = value.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        return token switch
        {
            "uhd" => "2160p",
            "4k" => "4k",
            "2160" => "2160p",
            "1080" => "1080p",
            "720" => "720p",
            "480" => "480p",
            "sd" => "480p",
            _ => token,
        };
    }

    private static string DetectResolution(string title)
    {
        if (Regex.IsMatch(title, @"(?i)\b(4k|uhd|2160p)\b")) return title.Contains("4k", StringComparison.OrdinalIgnoreCase) ? "4k" : "2160p";
        if (Regex.IsMatch(title, @"(?i)\b1080p\b")) return "1080p";
        if (Regex.IsMatch(title, @"(?i)\b720p\b")) return "720p";
        if (Regex.IsMatch(title, @"(?i)\b(480p|dvdrip|xvid)\b")) return "480p";
        return "unknown";
    }

    /// <summary>
    /// Gostream-default scoring: 4K DV &gt; 4K HDR10+ &gt; 4K HDR &gt; 4K
    /// &gt; 1080p REMUX &gt; 1080p. Mirrors scorer.go's Score() with the
    /// DefaultMovieProfile weights.
    /// </summary>
    public static int ScoreGostream(IndexerCandidate c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var t = c.Title;
        var score = 0;

        if (Re4K.IsMatch(t)) score += 200;
        else if (Re1080p.IsMatch(t)) score += 50;

        if (ReDV.IsMatch(t)) score += 150;
        if (ReHDR10Plus.IsMatch(t)) score += 100;
        else if (ReHDR.IsMatch(t)) score += 100;

        if (ReAtmos.IsMatch(t)) score += 50;
        var has51 = Re51.IsMatch(t);
        if (has51) score += 25;
        if (ReStereo.IsMatch(t) && !has51 && !ReAtmos.IsMatch(t)) score -= 50;

        if (ReBluRay.IsMatch(t)) score += 10;
        // Bonus for 1080p REMUX placement over plain 1080p — already covered
        // by ReBluRay; add an explicit tilt so the 1080p REMUX > 1080p ordering
        // holds even when seeders are equal.
        if (Re1080p.IsMatch(t) && ReRemux.IsMatch(t)) score += 5;

        if (c.Seeders > 50) score += 5;

        return score;
    }
}

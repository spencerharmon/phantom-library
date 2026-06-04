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
        int minSizeGb4K)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return null;
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
            if (is4K && c.Size > 0 && c.Size < min4K) return false;
            if (!is4K && is1080 && c.Size > 0 && c.Size < min1080p) return false;
            return true;
        }).ToList();

        if (filtered.Count == 0)
        {
            return null;
        }

        return effective switch
        {
            QualityPreset.BiggestMostSeeded => filtered
                .OrderByDescending(c => c.Size)
                .ThenByDescending(c => c.Seeders)
                .First(),
            _ => filtered
                .OrderByDescending(ScoreGostream)
                .ThenByDescending(c => c.Seeders)
                .ThenByDescending(c => c.Size)
                .First(),
        };
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

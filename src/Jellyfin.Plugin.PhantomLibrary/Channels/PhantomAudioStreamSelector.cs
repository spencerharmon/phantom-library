using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

internal sealed record PhantomAudioSelectionOptions(
    string? AudioLanguagePreference,
    bool PlayDefaultAudioTrack,
    bool RememberAudioSelections,
    int? RememberedAudioStreamIndex,
    bool AllowRememberingSelection)
{
    public static PhantomAudioSelectionOptions Default { get; } = new(null, true, false, null, true);
}

internal static class PhantomAudioStreamSelector
{
    public static void SetDefaultAudioStreamIndex(
        MediaSourceInfo source,
        PhantomAudioSelectionOptions options,
        ILocalizationManager? localizationManager = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        if (options.RememberedAudioStreamIndex.HasValue
            && options.RememberAudioSelections
            && options.AllowRememberingSelection
            && source.MediaStreams.Any(i => i.Type == MediaStreamType.Audio && i.Index == options.RememberedAudioStreamIndex.Value))
        {
            source.DefaultAudioStreamIndex = options.RememberedAudioStreamIndex.Value;
            source.DefaultAudioIndexSource = AudioIndexSource.User;
            return;
        }

        var preferredAudio = NormalizeLanguage(options.AudioLanguagePreference, localizationManager);
        source.DefaultAudioStreamIndex = GetDefaultAudioStreamIndex(
            source.MediaStreams,
            preferredAudio,
            options.PlayDefaultAudioTrack);

        source.DefaultAudioIndexSource = AudioIndexSource.None;
        if (options.PlayDefaultAudioTrack)
        {
            source.DefaultAudioIndexSource |= AudioIndexSource.Default;
        }

        if (preferredAudio.Count > 0)
        {
            source.DefaultAudioIndexSource |= AudioIndexSource.Language;
        }
    }

    internal static IReadOnlyList<string> NormalizeLanguage(string? language, ILocalizationManager? localizationManager)
    {
        if (string.IsNullOrEmpty(language))
        {
            return Array.Empty<string>();
        }

        var culture = localizationManager?.FindLanguageInfo(language);
        if (culture is not null)
        {
            return culture.Name.Contains('-', StringComparison.OrdinalIgnoreCase)
                ? new[] { culture.Name }
                : culture.ThreeLetterISOLanguageNames;
        }

        return new[] { language };
    }

    private static int? GetDefaultAudioStreamIndex(
        IReadOnlyList<MediaStream> streams,
        IReadOnlyList<string> preferredLanguages,
        bool preferDefaultTrack)
    {
        var sortedStreams = streams
            .Where(i => i.Type == MediaStreamType.Audio)
            .OrderByDescending(i => GetStreamScore(i, preferredLanguages))
            .ToList();

        if (preferredLanguages.Count > 0)
        {
            var preferredStream = sortedStreams.FirstOrDefault(i => MatchesPreferredLanguage(i.Language, preferredLanguages));
            if (preferredStream is not null)
            {
                return preferredStream.Index;
            }
        }

        if (preferDefaultTrack)
        {
            var defaultStream = sortedStreams.FirstOrDefault(i => i.IsDefault);
            if (defaultStream is not null)
            {
                return defaultStream.Index;
            }
        }

        return sortedStreams.FirstOrDefault()?.Index;
    }

    private static int GetStreamScore(MediaStream stream, IReadOnlyList<string> languagePreferences)
    {
        var index = FindLanguageIndex(languagePreferences, stream.Language);
        var score = index == -1 ? 1 : 101 - index;
        score = (score * 10) + (stream.IsForced ? 2 : 1);
        score = (score * 10) + (stream.IsDefault ? 2 : 1);
        score = (score * 10) + (stream.SupportsExternalStream ? 2 : 1);
        score = (score * 10) + (stream.IsTextSubtitleStream ? 2 : 1);
        score = (score * 10) + (stream.IsExternal ? 2 : 1);
        return score;
    }

    private static bool MatchesPreferredLanguage(string language, IReadOnlyList<string> languagePreferences)
        => FindLanguageIndex(languagePreferences, language) != -1;

    private static int FindLanguageIndex(IReadOnlyList<string> languagePreferences, string language)
    {
        for (var i = 0; i < languagePreferences.Count; i++)
        {
            if (string.Equals(languagePreferences[i], language, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

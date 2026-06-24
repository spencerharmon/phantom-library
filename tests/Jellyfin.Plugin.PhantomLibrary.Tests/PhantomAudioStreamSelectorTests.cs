using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomAudioStreamSelectorTests
{
    [Fact]
    public void PreferredLanguage_SelectsEnglish_WhenPolishIsContainerDefault()
    {
        var source = SourceWithPolishDefaultAndEnglish();
        var localization = new Mock<ILocalizationManager>(MockBehavior.Loose);
        localization.Setup(l => l.FindLanguageInfo("English"))
            .Returns(new CultureDto("en", "English", "en", new[] { "eng" }));

        PhantomAudioStreamSelector.SetDefaultAudioStreamIndex(
            source,
            new PhantomAudioSelectionOptions(
                AudioLanguagePreference: "English",
                PlayDefaultAudioTrack: false,
                RememberAudioSelections: false,
                RememberedAudioStreamIndex: null,
                AllowRememberingSelection: true),
            localization.Object);

        Assert.Equal(2, source.DefaultAudioStreamIndex);
        Assert.True(source.DefaultAudioIndexSource.HasFlag(AudioIndexSource.Language));
    }

    [Fact]
    public void PlayDefaultAudioTrack_MirrorsJellyfinDefaultFlagSemantics()
    {
        var source = SourceWithPolishDefaultAndEnglish();

        PhantomAudioStreamSelector.SetDefaultAudioStreamIndex(
            source,
            new PhantomAudioSelectionOptions(
                AudioLanguagePreference: "eng",
                PlayDefaultAudioTrack: true,
                RememberAudioSelections: false,
                RememberedAudioStreamIndex: null,
                AllowRememberingSelection: true));

        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.True(source.DefaultAudioIndexSource.HasFlag(AudioIndexSource.Default));
        Assert.True(source.DefaultAudioIndexSource.HasFlag(AudioIndexSource.Language));
    }

    [Fact]
    public void RememberedAudioSelection_WinsWhenStillPresent()
    {
        var source = SourceWithPolishDefaultAndEnglish();

        PhantomAudioStreamSelector.SetDefaultAudioStreamIndex(
            source,
            new PhantomAudioSelectionOptions(
                AudioLanguagePreference: "eng",
                PlayDefaultAudioTrack: false,
                RememberAudioSelections: true,
                RememberedAudioStreamIndex: 1,
                AllowRememberingSelection: true));

        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.Equal(AudioIndexSource.User, source.DefaultAudioIndexSource);
    }

    private static MediaSourceInfo SourceWithPolishDefaultAndEnglish()
        => new()
        {
            MediaStreams = new List<MediaStream>
            {
                new() { Index = 0, Type = MediaStreamType.Video, IsDefault = true },
                new() { Index = 1, Type = MediaStreamType.Audio, Language = "pol", IsDefault = true },
                new() { Index = 2, Type = MediaStreamType.Audio, Language = "eng", IsDefault = false },
            },
        };
}

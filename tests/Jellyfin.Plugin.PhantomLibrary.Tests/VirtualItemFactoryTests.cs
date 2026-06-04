using System;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class VirtualItemFactoryTests
{
    [Fact]
    public void CreateVirtualMovie_PopulatesProviderIds()
    {
        var details = new TmdbMovieDetails(
            Id: 603,
            Title: "The Matrix",
            OriginalTitle: "The Matrix",
            Overview: "Hacker fights machines.",
            PosterPath: "/x.jpg",
            BackdropPath: "/b.jpg",
            ReleaseDate: "1999-03-30",
            VoteAverage: 8.2,
            VoteCount: 24000,
            Runtime: 136,
            Genres: new[] { "Action", "Science Fiction" },
            Status: "Released",
            Tagline: "Welcome to the Real World.",
            ImdbId: "tt0133093",
            Budget: 63000000,
            Revenue: 463517383);

        var movie = VirtualItemFactory.CreateVirtualMovie(details);

        Assert.Equal("The Matrix", movie.Name);
        Assert.Equal(1999, movie.ProductionYear);
        Assert.Equal("Welcome to the Real World.", movie.Tagline);
        Assert.Equal(8.2f, movie.CommunityRating);
        Assert.Equal("603", movie.ProviderIds["Tmdb"]);
        Assert.Equal("tt0133093", movie.ProviderIds["Imdb"]);
        Assert.Null(movie.Path);
        Assert.NotNull(movie.PremiereDate);
        Assert.Equal(TimeSpan.FromMinutes(136).Ticks, movie.RunTimeTicks);
    }

    [Fact]
    public void CreateVirtualSeries_PopulatesProviderIds()
    {
        var details = new TmdbSeriesDetails(
            Id: 1399,
            Name: "Game of Thrones",
            OriginalName: "Game of Thrones",
            Overview: "Seven noble families...",
            PosterPath: "/p.jpg",
            BackdropPath: "/b.jpg",
            FirstAirDate: "2011-04-17",
            VoteAverage: 8.4,
            VoteCount: 21000,
            Genres: new[] { "Drama" },
            Status: "Ended",
            NumberOfSeasons: 8,
            NumberOfEpisodes: 73,
            OriginCountry: new[] { "US" },
            ImdbId: "tt0944947");

        var series = VirtualItemFactory.CreateVirtualSeries(details);

        Assert.Equal("Game of Thrones", series.Name);
        Assert.Equal(2011, series.ProductionYear);
        Assert.Equal("1399", series.ProviderIds["Tmdb"]);
        Assert.Equal("tt0944947", series.ProviderIds["Imdb"]);
        Assert.Null(series.Path);
        Assert.Equal(8.4f, series.CommunityRating);
    }
}

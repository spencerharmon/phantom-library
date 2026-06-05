using System.Collections.Generic;

namespace Jellyfin.Plugin.PhantomLibrary.Clients.Models;

/// <summary>
/// Static TMDB genre id → name map for movies and series. These ids are
/// stable across TMDB's lifetime; new genres are appended, never reused.
/// Snapshot taken 2024-Q4 from <c>/genre/movie/list</c> and <c>/genre/tv/list</c>.
/// Used to populate <c>BaseItem.Genres</c> when only <c>genre_ids</c> are
/// available (search hits, trending, similar, recommendations).
/// </summary>
public static class TmdbGenres
{
    /// <summary>Movie genre id → English name.</summary>
    public static readonly IReadOnlyDictionary<int, string> Movies = new Dictionary<int, string>
    {
        [28] = "Action",
        [12] = "Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [14] = "Fantasy",
        [36] = "History",
        [27] = "Horror",
        [10402] = "Music",
        [9648] = "Mystery",
        [10749] = "Romance",
        [878] = "Science Fiction",
        [10770] = "TV Movie",
        [53] = "Thriller",
        [10752] = "War",
        [37] = "Western",
    };

    /// <summary>Series genre id → English name.</summary>
    public static readonly IReadOnlyDictionary<int, string> Series = new Dictionary<int, string>
    {
        [10759] = "Action & Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [10762] = "Kids",
        [9648] = "Mystery",
        [10763] = "News",
        [10764] = "Reality",
        [10765] = "Sci-Fi & Fantasy",
        [10766] = "Soap",
        [10767] = "Talk",
        [10768] = "War & Politics",
        [37] = "Western",
    };

    /// <summary>Resolves an array of TMDB movie genre ids to English names; unknown ids are dropped.</summary>
    public static string[] ResolveMovieGenres(int[]? ids)
    {
        if (ids is null || ids.Length == 0) return System.Array.Empty<string>();
        var list = new List<string>(ids.Length);
        foreach (var id in ids)
        {
            if (Movies.TryGetValue(id, out var name)) list.Add(name);
        }

        return list.ToArray();
    }

    /// <summary>Resolves an array of TMDB series genre ids to English names; unknown ids are dropped.</summary>
    public static string[] ResolveSeriesGenres(int[]? ids)
    {
        if (ids is null || ids.Length == 0) return System.Array.Empty<string>();
        var list = new List<string>(ids.Length);
        foreach (var id in ids)
        {
            if (Series.TryGetValue(id, out var name)) list.Add(name);
        }

        return list.ToArray();
    }
}

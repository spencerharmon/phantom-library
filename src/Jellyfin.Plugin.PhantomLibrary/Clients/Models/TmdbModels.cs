using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PhantomLibrary.Clients.Models;

/// <summary>Unified TMDB search hit. Series searches map name → Title and first_air_date → ReleaseDate.</summary>
public sealed record TmdbSearchHit(
    int Id,
    string? Title,
    string? OriginalTitle,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? ReleaseDate,
    double? VoteAverage,
    int? VoteCount)
{
    /// <summary>TMDB genre ids as returned by search / trending / similar / recommendations.</summary>
    public int[]? GenreIds { get; init; }
}

/// <summary>TMDB movie details — superset of a search hit.</summary>
public sealed record TmdbMovieDetails(
    int Id,
    string? Title,
    string? OriginalTitle,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? ReleaseDate,
    double? VoteAverage,
    int? VoteCount,
    int Runtime,
    string[] Genres,
    string Status,
    string? Tagline,
    string? ImdbId,
    int? Budget,
    long? Revenue);

/// <summary>TMDB series details. ImdbId requires append_to_response=external_ids.</summary>
public sealed record TmdbSeriesDetails(
    int Id,
    string Name,
    string? OriginalName,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? FirstAirDate,
    double? VoteAverage,
    int? VoteCount,
    string[] Genres,
    string Status,
    int NumberOfSeasons,
    int NumberOfEpisodes,
    string[] OriginCountry,
    string? ImdbId);

/// <summary>Single TMDB image entry.</summary>
public sealed record TmdbImage(
    string FilePath,
    int Width,
    int Height,
    double VoteAverage,
    int VoteCount,
    string? Iso6391);

/// <summary>TMDB images bundle for a movie or series.</summary>
public sealed record TmdbImages(
    TmdbImage[] Posters,
    TmdbImage[] Backdrops,
    TmdbImage[] Logos);

/// <summary>TMDB /configuration response (image base URL + size buckets).</summary>
public sealed record TmdbConfiguration(
    string SecureBaseUrl,
    string[] PosterSizes,
    string[] BackdropSizes,
    string[] LogoSizes);

/// <summary>URL builder helpers for TMDB CDN paths.</summary>
public static class TmdbConfigurationExtensions
{
    /// <summary>Builds a poster CDN URL. <paramref name="size"/> is a TMDB size like "w500" or "original".</summary>
    public static string BuildPosterUrl(this TmdbConfiguration cfg, string filePath, string size)
        => $"{cfg.SecureBaseUrl.TrimEnd('/')}/{size}{(filePath.StartsWith('/') ? filePath : "/" + filePath)}";

    /// <summary>Builds a backdrop CDN URL.</summary>
    public static string BuildBackdropUrl(this TmdbConfiguration cfg, string filePath, string size)
        => $"{cfg.SecureBaseUrl.TrimEnd('/')}/{size}{(filePath.StartsWith('/') ? filePath : "/" + filePath)}";

    /// <summary>Builds a logo CDN URL.</summary>
    public static string BuildLogoUrl(this TmdbConfiguration cfg, string filePath, string size)
        => $"{cfg.SecureBaseUrl.TrimEnd('/')}/{size}{(filePath.StartsWith('/') ? filePath : "/" + filePath)}";
}

// ---------- Internal wire-format DTOs (snake_case decoded by System.Text.Json) ----------

internal sealed class TmdbSearchResponse<T>
{
    public int Page { get; set; }
    public T[] Results { get; set; } = System.Array.Empty<T>();
    public int TotalPages { get; set; }
    public int TotalResults { get; set; }
}

internal sealed class TmdbMovieSearchHitDto
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public int[]? GenreIds { get; set; }
}

internal sealed class TmdbSeriesSearchHitDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? FirstAirDate { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public int[]? GenreIds { get; set; }
}

internal sealed class TmdbGenreDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class TmdbExternalIdsDto
{
    public string? ImdbId { get; set; }
}

internal sealed class TmdbMovieDetailsDto
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public int? Runtime { get; set; }
    public TmdbGenreDto[]? Genres { get; set; }
    public string? Status { get; set; }
    public string? Tagline { get; set; }
    public string? ImdbId { get; set; }
    public int? Budget { get; set; }
    public long? Revenue { get; set; }
    public TmdbExternalIdsDto? ExternalIds { get; set; }
}

internal sealed class TmdbSeriesDetailsDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? FirstAirDate { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public TmdbGenreDto[]? Genres { get; set; }
    public string? Status { get; set; }
    public int? NumberOfSeasons { get; set; }
    public int? NumberOfEpisodes { get; set; }
    public string[]? OriginCountry { get; set; }
    public TmdbExternalIdsDto? ExternalIds { get; set; }
}

internal sealed class TmdbImageDto
{
    public string? FilePath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; set; }
}

internal sealed class TmdbImagesDto
{
    public TmdbImageDto[]? Posters { get; set; }
    public TmdbImageDto[]? Backdrops { get; set; }
    public TmdbImageDto[]? Logos { get; set; }
}

internal sealed class TmdbConfigurationDto
{
    public TmdbConfigurationImagesDto? Images { get; set; }
}

internal sealed class TmdbConfigurationImagesDto
{
    public string? SecureBaseUrl { get; set; }
    public string[]? PosterSizes { get; set; }
    public string[]? BackdropSizes { get; set; }
    public string[]? LogoSizes { get; set; }
}

internal sealed class TmdbExternalIdsResponseDto
{
    public string? ImdbId { get; set; }
}

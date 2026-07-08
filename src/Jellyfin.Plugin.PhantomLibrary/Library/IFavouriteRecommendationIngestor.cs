using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Ingests TMDB "similar" + "recommendations" for a title a user has just
/// favourited, folding the results into the append-only Phantom catalogue.
///
/// This is the event-driven complement to <see cref="Scheduled.DiscoveryRefreshTask"/>'s
/// periodic trending/discover walk: when a user favourites a movie or series,
/// <c>UserDataSavedListener</c> calls this so the catalogue grows toward the
/// user's demonstrated taste. New movie rows are enqueued for availability
/// probing and new series rows for bounded expansion by the same
/// <see cref="State.PhantomDb.UpsertCatalogueHitsAsync"/> path the discovery
/// task uses.
/// </summary>
public interface IFavouriteRecommendationIngestor
{
    /// <summary>
    /// Fetches similar + recommended titles for the favourited seed and upserts
    /// them into the catalogue under the favourite-recommendation source bit.
    /// </summary>
    /// <param name="tmdbId">TMDB id of the favourited movie or series (for an
    /// episode favourite the caller passes the parent series id). Must be &gt; 0.</param>
    /// <param name="type"><c>"movie"</c> or <c>"series"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A summary of what was fetched and written.</returns>
    Task<FavouriteRecommendationResult> IngestForFavouriteAsync(int tmdbId, string type, CancellationToken ct);
}

/// <summary>
/// Outcome of a single <see cref="IFavouriteRecommendationIngestor.IngestForFavouriteAsync"/>
/// call. All counts are zero when <see cref="Enabled"/> is false.
/// </summary>
/// <param name="TmdbId">The favourited seed id the ingest ran for.</param>
/// <param name="Type"><c>"movie"</c> or <c>"series"</c>.</param>
/// <param name="Enabled">False when <c>FavouriteRecommendationsEnabled</c> is off (no-op).</param>
/// <param name="CandidatesConsidered">Total hits returned by TMDB across similar + recommendations (pre-dedupe).</param>
/// <param name="Seen">Rows presented to the catalogue writer after dedupe/drop/cap.</param>
/// <param name="Inserted">New <c>catalogue_items</c> rows created.</param>
/// <param name="MetadataInserted">New <c>tmdb_metadata</c> rows created.</param>
/// <param name="AvailabilityInserted">New <c>availability_items</c> rows created (movies).</param>
/// <param name="SeriesExpansionInserted">New <c>series_expansion_state</c> rows created (series).</param>
public sealed record FavouriteRecommendationResult(
    int TmdbId,
    string Type,
    bool Enabled,
    int CandidatesConsidered,
    int Seen,
    int Inserted,
    int MetadataInserted,
    int AvailabilityInserted,
    int SeriesExpansionInserted)
{
    /// <summary>A no-op result used when the feature is disabled by configuration.</summary>
    public static FavouriteRecommendationResult Disabled(int tmdbId, string type)
        => new(tmdbId, type, Enabled: false, 0, 0, 0, 0, 0, 0);
}

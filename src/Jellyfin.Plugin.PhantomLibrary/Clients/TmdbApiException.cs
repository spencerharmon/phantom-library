using System;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Thrown when TMDB returns a non-success status other than 429 (rate-limit retried)
/// and other than 404 on detail lookups (mapped to null instead).
/// </summary>
public sealed class TmdbApiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TmdbApiException"/> class.</summary>
    public TmdbApiException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TmdbApiException"/> class with a message.</summary>
    public TmdbApiException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TmdbApiException"/> class with a message and inner exception.</summary>
    public TmdbApiException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TmdbApiException"/> class with status code and body.</summary>
    public TmdbApiException(int statusCode, string requestPath, string? responseBody)
        : base($"TMDB request to {requestPath} failed with HTTP {statusCode}: {responseBody}")
    {
        StatusCode = statusCode;
        RequestPath = requestPath;
        ResponseBody = responseBody;
    }

    /// <summary>Gets the HTTP status code, if known.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the request path that failed.</summary>
    public string? RequestPath { get; }

    /// <summary>Gets the response body, if any.</summary>
    public string? ResponseBody { get; }
}

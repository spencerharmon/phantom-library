using System;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>Base type for gostream HTTP-level failures.</summary>
public abstract class GostreamException : Exception
{
    protected GostreamException(string message) : base(message) { }
    protected GostreamException(string message, Exception inner) : base(message, inner) { }
    protected GostreamException() { }
}

/// <summary>gostream returned 504: torrent metadata not resolved within the server-side timeout.</summary>
public sealed class GostreamTimeoutException : GostreamException
{
    public GostreamTimeoutException(string message) : base(message) { }
    public GostreamTimeoutException() : base("gostream metadata timeout") { }
    public GostreamTimeoutException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>gostream returned 422 no_valid_files: torrent has no acceptable video files.</summary>
public sealed class GostreamNoValidFilesException : GostreamException
{
    public GostreamNoValidFilesException(string message) : base(message) { }
    public GostreamNoValidFilesException() : base("gostream: no valid files in torrent") { }
    public GostreamNoValidFilesException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>gostream rejected the request (400/405/415).</summary>
public sealed class GostreamBadRequestException : GostreamException
{
    public GostreamBadRequestException(string message) : base(message) { }
    public GostreamBadRequestException() : base("gostream: bad request") { }
    public GostreamBadRequestException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>gostream returned 5xx (other than 504) after one retry.</summary>
public sealed class GostreamServerException : GostreamException
{
    public int StatusCode { get; }

    public GostreamServerException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
    public GostreamServerException(string message) : base(message) { }
    public GostreamServerException() : base("gostream: server error") { }
    public GostreamServerException(string message, Exception inner) : base(message, inner) { }
}

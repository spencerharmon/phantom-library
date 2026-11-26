using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Cancellation with hits already obtained by sibling subqueries or parsed
/// before a download redirect stalled. Remains an OperationCanceledException
/// for callers; only the budget collector, which owns the request timeout and
/// can distinguish it from caller/shutdown cancellation, may salvage the hits.
/// </summary>
internal sealed class IndexerPartialCancellationException : OperationCanceledException
{
    public IndexerPartialCancellationException(IReadOnlyList<IndexerCandidate> candidates, OperationCanceledException inner)
        : base("Indexer cancelled after receiving partial results", inner, inner.CancellationToken)
    {
        Candidates = candidates;
    }

    public IndexerPartialCancellationException() { }
    public IndexerPartialCancellationException(string message) : base(message) { }
    public IndexerPartialCancellationException(string message, Exception innerException) : base(message, innerException) { }

    public IReadOnlyList<IndexerCandidate> Candidates { get; } = Array.Empty<IndexerCandidate>();
}

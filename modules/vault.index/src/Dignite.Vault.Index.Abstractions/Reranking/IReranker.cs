using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Index.Reranking;

/// <summary>
/// Scores how well each candidate text answers a query, reading the two together (a cross-encoder), to reorder the
/// candidates retrieval found. Microsoft.Extensions.AI has no reranking abstraction, so Index defines this one; a host
/// registers an implementation, and without one search skips the step.
/// <para>
/// The scores order candidates within one call and mean nothing across calls or models, so Index never filters by them.
/// </para>
/// </summary>
public interface IReranker
{
    /// <summary>The model this reranker calls, for reports and logs.</summary>
    string ModelId { get; }

    /// <summary>One score per document, in the order of <paramref name="documents"/>; higher is more relevant.</summary>
    Task<RerankResult> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default);
}

/// <param name="Scores">One per input document, in input order.</param>
/// <param name="InputTokenCount">What the call cost, when the provider says.</param>
public sealed record RerankResult(IReadOnlyList<double> Scores, long? InputTokenCount);

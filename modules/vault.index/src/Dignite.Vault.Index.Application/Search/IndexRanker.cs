using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Index.Reranking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Index.Search;

/// <summary>A candidate in its final order, with the reranker's score when there was one.</summary>
public sealed record RankedChunk(FusedChunk Fused, double? RerankScore);

/// <summary>
/// The ranking half of search: weighted fusion of the two legs, then, when the host registers an
/// <see cref="IReranker"/>, a rerank of the leading candidates. Search and the evaluation both rank through this
/// class, so what the evaluation measures is what search returns.
/// </summary>
public class IndexRanker : ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;
    private readonly VaultIndexSearchOptions _options;

    public ILogger<IndexRanker> Logger { get; set; } = NullLogger<IndexRanker>.Instance;

    public IndexRanker(IServiceProvider serviceProvider, IOptions<VaultIndexSearchOptions> options)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    /// <summary>The configured reranker, or <c>null</c>.</summary>
    public virtual IReranker? Reranker => _serviceProvider.GetService<IReranker>();

    public virtual IReadOnlyList<FusedChunk> Fuse(IReadOnlyList<ChunkMatch> keyword, IReadOnlyList<ChunkMatch> vector)
        => Fuse(keyword, vector, _options.KeywordWeight, _options.VectorWeight);

    /// <summary>Fusion with the given leg weights instead of the host's: the retrieval test's.</summary>
    public virtual IReadOnlyList<FusedChunk> Fuse(
        IReadOnlyList<ChunkMatch> keyword,
        IReadOnlyList<ChunkMatch> vector,
        double keywordWeight,
        double vectorWeight)
        => ReciprocalRankFusion.Fuse(keyword, vector, IndexSearchConsts.RrfK, keywordWeight, vectorWeight);

    /// <summary>How many fused candidates go to the reranker.</summary>
    public virtual int RerankCandidateCount => Math.Clamp(_options.RerankCandidateCount, 1, IndexSearchConsts.MaxRerankCandidates);

    /// <summary>
    /// Reorders <paramref name="candidates"/> by the reranker's scores, reading each through
    /// <paramref name="texts"/>; a candidate without a text keeps its fused place after the reranked ones. Returns
    /// <c>null</c> when there is no reranker, or when it failed: the fused order is still evidence, so search answers
    /// with it rather than failing.
    /// </summary>
    public virtual async Task<IReadOnlyList<RankedChunk>?> RerankAsync(
        string query,
        IReadOnlyList<FusedChunk> candidates,
        IReadOnlyDictionary<Guid, string> texts,
        IReranker? reranker = null,
        CancellationToken cancellationToken = default)
    {
        reranker ??= Reranker;
        var scored = candidates.Where(c => texts.ContainsKey(c.ChunkId)).ToList();
        if (reranker is null || scored.Count == 0)
        {
            return null;
        }

        // Every text crossing a model boundary has a ceiling. Cutting the tail costs a little relevance, not correctness:
        // the reranker only orders what retrieval already found.
        var documents = scored.Select(c => TextTruncator.AtCharBoundary(texts[c.ChunkId], IndexSearchConsts.MaxRerankTextLength)).ToList();
        RerankResult result;
        try
        {
            result = await reranker.RerankAsync(TextTruncator.AtCharBoundary(query, IndexSearchConsts.MaxQueryLength), documents, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "Reranking with {Model} failed; search returns the fused order.", reranker.ModelId);
            return null;
        }

        var reranked = scored
            .Select((c, i) => new RankedChunk(c, result.Scores[i]))
            .OrderByDescending(r => r.RerankScore)
            .ThenByDescending(r => r.Fused.Score)
            .ToList();
        var unscored = candidates.Where(c => !texts.ContainsKey(c.ChunkId)).Select(c => new RankedChunk(c, null));
        return reranked.Concat(unscored).ToList();
    }
}

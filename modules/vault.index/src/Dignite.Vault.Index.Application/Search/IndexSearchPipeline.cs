using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Ingestion;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The ranking settings of one search. Search runs with the host's (<see cref="IndexSearchPipeline.DefaultSettings"/>);
/// the retrieval test may change them for one run.
/// </summary>
/// <param name="KeywordWeight">The keyword leg's weight in the fusion; 0 turns the leg off.</param>
/// <param name="VectorWeight">The vector leg's weight in the fusion; 0 turns the leg off.</param>
/// <param name="CandidatesPerLeg">How many candidates each leg returns.</param>
/// <param name="RerankCandidateCount">How many of the leading fused candidates the reranker reorders.</param>
/// <param name="Rerank">Whether to rerank at all, when the host has a reranker.</param>
public sealed record IndexSearchSettings(
    double KeywordWeight,
    double VectorWeight,
    int CandidatesPerLeg,
    int RerankCandidateCount,
    bool Rerank);

/// <summary>Everything one run of the pipeline produced, from each leg to the final order.</summary>
public sealed class IndexSearchOutcome
{
    public static readonly IndexSearchOutcome Empty = new();

    /// <summary>The keyword leg's candidates, best first.</summary>
    public IReadOnlyList<ChunkMatch> KeywordMatches { get; init; } = [];

    /// <summary>The vector leg's candidates, nearest first.</summary>
    public IReadOnlyList<ChunkMatch> VectorMatches { get; init; } = [];

    /// <summary>Both legs after fusion, best first.</summary>
    public IReadOnlyList<FusedChunk> Fused { get; init; } = [];

    /// <summary>
    /// The loaded candidates in their final order: the reranked ones first when there was a rerank, then the rest in
    /// fused order. A chunk that left the scope after the legs ran is not here.
    /// </summary>
    public IReadOnlyList<RankedChunk> Ranked { get; init; } = [];

    public bool Reranked { get; init; }

    /// <summary>The reranker's model, when one was asked.</summary>
    public string? RerankModelId { get; init; }

    public IReadOnlyDictionary<Guid, Chunk> Chunks { get; init; } = new Dictionary<Guid, Chunk>();

    public IReadOnlyDictionary<Guid, IndexedDocument> Documents { get; init; } = new Dictionary<Guid, IndexedDocument>();

    public TimeSpan KeywordTime { get; init; }

    /// <summary>The vector leg, including embedding the query.</summary>
    public TimeSpan VectorTime { get; init; }

    public TimeSpan RerankTime { get; init; }
}

/// <summary>
/// One search over a resolved scope: the two legs, fusion, loading the candidates within the scope, and the rerank.
/// Search and the retrieval test both run it, so the test shows what search does. Authorization and the read scope
/// are the callers'; every chunk this reads goes through <see cref="ChunkScopeFilter"/> again.
/// </summary>
public class IndexSearchPipeline : ITransientDependency
{
    private readonly IChunkRepository _chunkRepository;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IChunkSearchIndex _searchIndex;
    private readonly IndexGenerationManager _generationManager;
    private readonly IndexEmbedder _embedder;
    private readonly IndexRanker _ranker;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly VaultIndexSearchOptions _options;

    public IndexSearchPipeline(
        IChunkRepository chunkRepository,
        IRepository<IndexedDocument, Guid> documentRepository,
        IChunkSearchIndex searchIndex,
        IndexGenerationManager generationManager,
        IndexEmbedder embedder,
        IndexRanker ranker,
        IAsyncQueryableExecuter asyncExecuter,
        IOptions<VaultIndexSearchOptions> options)
    {
        _chunkRepository = chunkRepository;
        _documentRepository = documentRepository;
        _searchIndex = searchIndex;
        _generationManager = generationManager;
        _embedder = embedder;
        _ranker = ranker;
        _asyncExecuter = asyncExecuter;
        _options = options.Value;
    }

    /// <summary>The host's settings: what search runs with.</summary>
    public virtual IndexSearchSettings DefaultSettings => new(
        _options.KeywordWeight,
        _options.VectorWeight,
        IndexSearchConsts.CandidatesPerLeg,
        _ranker.RerankCandidateCount,
        Rerank: true);

    /// <summary>
    /// Runs the pipeline. <paramref name="loadCount"/> is how many leading fused candidates to load and order; at least
    /// the reranker's share is loaded when it reranks.
    /// </summary>
    public virtual async Task<IndexSearchOutcome> RunAsync(string query, ChunkSearchScope scope, IndexSearchSettings settings, int loadCount)
    {
        Check.NotNullOrWhiteSpace(query, nameof(query));
        Check.NotNull(scope, nameof(scope));
        Check.NotNull(settings, nameof(settings));
        if (scope.ReachesNothing)
        {
            return IndexSearchOutcome.Empty;
        }

        var candidatesPerLeg = Math.Clamp(settings.CandidatesPerLeg, 1, IndexSearchConsts.MaxCandidatesPerLeg);
        var watch = Stopwatch.StartNew();
        var vectorMatches = await SearchVectorsAsync(query, scope, candidatesPerLeg);
        var vectorTime = watch.Elapsed;

        watch.Restart();
        var keywordMatches = await _searchIndex.SearchKeywordsAsync(query, scope, candidatesPerLeg);
        var keywordTime = watch.Elapsed;

        var fused = _ranker.Fuse(keywordMatches, vectorMatches, settings.KeywordWeight, settings.VectorWeight);
        if (fused.Count == 0)
        {
            return new IndexSearchOutcome
            {
                KeywordMatches = keywordMatches,
                VectorMatches = vectorMatches,
                KeywordTime = keywordTime,
                VectorTime = vectorTime
            };
        }

        // Load the leading candidates (or as many as the reranker reads) through the scope again: a chunk can leave it
        // between the legs and this read (its document went to the recycle bin), and it is dropped here.
        var reranker = settings.Rerank ? _ranker.Reranker : null;
        var rerankCount = Math.Clamp(settings.RerankCandidateCount, 1, IndexSearchConsts.MaxRerankCandidates);
        var candidates = fused.Take(Math.Max(loadCount, reranker is null ? 0 : rerankCount)).ToList();
        var ids = candidates.Select(c => c.ChunkId).ToList();
        var chunks = (await _asyncExecuter.ToListAsync(
                ChunkScopeFilter.Apply(await _chunkRepository.GetQueryableAsync(), scope).Where(c => ids.Contains(c.Id))))
            .ToDictionary(c => c.Id);
        var documents = await GetDocumentsAsync(chunks.Values.Select(c => c.DocumentId));

        // Only the reranker's share is sent to it, whatever was loaded; the rest keep their fused order behind it.
        watch.Restart();
        var head = candidates.Take(rerankCount).ToList();
        var texts = head
            .Where(c => chunks.ContainsKey(c.ChunkId))
            .ToDictionary(c => c.ChunkId, c => ChunkContext.Compose(documents.GetValueOrDefault(chunks[c.ChunkId].DocumentId)?.Title, chunks[c.ChunkId].Text));
        var reranked = reranker is null ? null : await _ranker.RerankAsync(query, head, texts, reranker);
        var rerankTime = reranked is null ? TimeSpan.Zero : watch.Elapsed;

        var ordered = reranked is null
            ? candidates.Select(c => new RankedChunk(c, null))
            : reranked.Concat(candidates.Skip(head.Count).Select(c => new RankedChunk(c, null)));

        return new IndexSearchOutcome
        {
            KeywordMatches = keywordMatches,
            VectorMatches = vectorMatches,
            Fused = fused,
            Ranked = ordered.Where(r => chunks.ContainsKey(r.Fused.ChunkId)).ToList(),
            Reranked = reranked is not null,
            RerankModelId = reranker?.ModelId,
            Chunks = chunks,
            Documents = documents,
            KeywordTime = keywordTime,
            VectorTime = vectorTime,
            RerankTime = rerankTime
        };
    }

    /// <summary>
    /// The vector leg, when there is one. Searching with a model other than the one the serving vectors came from would
    /// compare two vector spaces, so it is refused, like ingestion refuses it.
    /// </summary>
    protected virtual async Task<IReadOnlyList<ChunkMatch>> SearchVectorsAsync(string query, ChunkSearchScope scope, int take)
    {
        var serving = await _generationManager.FindServingAsync();
        if (serving is null)
        {
            return [];
        }

        if (!_embedder.IsConfigured)
        {
            throw new BusinessException(VaultIndexErrorCodes.EmbeddingGeneratorMissing);
        }

        var modelId = _embedder.GetModelId();
        if (!string.Equals(modelId, serving.ModelId, StringComparison.Ordinal))
        {
            throw new BusinessException(VaultIndexErrorCodes.EmbeddingModelChanged)
                .WithData("ModelId", modelId)
                .WithData("Dimensions", serving.Dimensions)
                .WithData("ServingModelId", serving.ModelId)
                .WithData("ServingDimensions", serving.Dimensions);
        }

        var embedding = await _embedder.EmbedAsync([query]);
        return await _searchIndex.SearchVectorsAsync(serving.Id, embedding.Vectors[0], scope, take);
    }

    protected virtual async Task<Dictionary<Guid, IndexedDocument>> GetDocumentsAsync(IEnumerable<Guid> documentIds)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, IndexedDocument>();
        }

        return (await _documentRepository.GetListAsync(d => ids.Contains(d.Id))).ToDictionary(d => d.Id);
    }
}

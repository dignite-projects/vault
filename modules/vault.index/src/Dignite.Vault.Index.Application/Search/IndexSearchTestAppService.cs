using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Index.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Uow;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The retrieval test page's one call. It resolves the scope as search does and runs the same
/// <see cref="IndexSearchPipeline"/>, so what it shows is what search does with those settings; the settings live only
/// in this call. It returns every candidate, not the top few, because the question it answers is usually "why is this
/// passage not in the results".
/// </summary>
public class IndexSearchTestAppService : VaultIndexAppService, IIndexSearchTestAppService
{
    private readonly IndexReadScopeResolver _scopeResolver;
    private readonly IndexSearchPipeline _pipeline;
    private readonly IndexRanker _ranker;

    public IndexSearchTestAppService(IndexReadScopeResolver scopeResolver, IndexSearchPipeline pipeline, IndexRanker ranker)
    {
        _scopeResolver = scopeResolver;
        _pipeline = pipeline;
        _ranker = ranker;
    }

    [UnitOfWork(isTransactional: false)]
    public virtual async Task<IndexSearchTestResultDto> RunAsync(IndexSearchTestInput input)
    {
        Check.NotNull(input, nameof(input));
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Status);

        var query = Check.NotNullOrWhiteSpace(input.Query, nameof(input.Query), IndexSearchConsts.MaxQueryLength).Trim();
        var defaults = _pipeline.DefaultSettings;
        var settings = new IndexSearchSettings(
            input.KeywordWeight ?? defaults.KeywordWeight,
            input.VectorWeight ?? defaults.VectorWeight,
            input.CandidatesPerLeg ?? defaults.CandidatesPerLeg,
            input.RerankCandidateCount ?? defaults.RerankCandidateCount,
            input.Rerank ?? defaults.Rerank);

        var result = new IndexSearchTestResultDto
        {
            KeywordWeight = settings.KeywordWeight,
            VectorWeight = settings.VectorWeight,
            CandidatesPerLeg = settings.CandidatesPerLeg,
            RerankCandidateCount = settings.RerankCandidateCount,
            RerankAvailable = _ranker.Reranker is not null
        };

        var types = await _scopeResolver.GetVisibleTypesAsync();
        var scope = await _scopeResolver.GetScopeAsync(input.DocumentTypeCode, types, input.FieldFilters);
        if (scope.ReachesNothing)
        {
            return result;
        }

        // Every fused candidate is loaded: the page shows them all.
        var outcome = await _pipeline.RunAsync(query, scope, settings, IndexSearchConsts.MaxCandidatesPerLeg * 2);
        var keyword = outcome.KeywordMatches.Select((m, i) => (m.ChunkId, Rank: i + 1, m.Score)).ToDictionary(x => x.ChunkId);
        var vector = outcome.VectorMatches.Select((m, i) => (m.ChunkId, Rank: i + 1, m.Score)).ToDictionary(x => x.ChunkId);
        var fusedRanks = outcome.Fused.Select((c, i) => (c.ChunkId, Rank: i + 1)).ToDictionary(x => x.ChunkId, x => x.Rank);

        result.Reranked = outcome.Reranked;
        result.RerankModelId = outcome.RerankModelId;
        result.KeywordCandidateCount = outcome.KeywordMatches.Count;
        result.VectorCandidateCount = outcome.VectorMatches.Count;
        result.KeywordMilliseconds = (long)outcome.KeywordTime.TotalMilliseconds;
        result.VectorMilliseconds = (long)outcome.VectorTime.TotalMilliseconds;
        result.RerankMilliseconds = (long)outcome.RerankTime.TotalMilliseconds;

        foreach (var (candidate, rerankScore) in outcome.Ranked)
        {
            var chunk = outcome.Chunks[candidate.ChunkId];
            var text = TextTruncator.AtCharBoundary(chunk.Text, IndexSearchConsts.MaxTestTextLength);
            result.Candidates.Add(new IndexSearchTestCandidateDto
            {
                Rank = result.Candidates.Count + 1,
                FusedRank = fusedRanks[candidate.ChunkId],
                FusedScore = candidate.Score,
                KeywordRank = candidate.KeywordRank,
                KeywordScore = keyword.TryGetValue(candidate.ChunkId, out var k) ? k.Score : null,
                VectorRank = candidate.VectorRank,
                VectorDistance = vector.TryGetValue(candidate.ChunkId, out var v) ? v.Score : null,
                RerankScore = rerankScore,
                ChunkId = chunk.Id,
                Pointer = IndexPointer.ForChunk(chunk.Id).ToString(),
                DocumentId = chunk.DocumentId,
                DocumentTitle = outcome.Documents.GetValueOrDefault(chunk.DocumentId)?.Title,
                DocumentTypeCode = types.GetValueOrDefault(chunk.DocumentTypeId),
                HeadingPath = chunk.HeadingPath,
                Text = text,
                TextTruncated = text.Length < chunk.Text.Length
            });
        }

        return result;
    }
}

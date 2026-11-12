using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// Retrieval: entry check, read scope, the two legs, fusion and assembly. It reads the caller's read scope from Extract
/// (<see cref="IDocumentAppService.GetReadScopeAsync"/>) and nothing else of Extract's documents: Index's own rows
/// carry what a hit needs, and the integration service, which reads with no user, is for ingestion only (an
/// architecture test holds that line).
/// </summary>
public class IndexSearchAppService : VaultIndexAppService, IIndexSearchAppService
{
    /// <summary>The read-scope versions this Index understands; see <see cref="DocumentReadScopeDto.Version"/>.</summary>
    protected const int SupportedReadScopeVersion = 1;

    private const string ChunkSeparator = "\n\n";

    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IChunkRepository _chunkRepository;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IChunkSearchIndex _searchIndex;
    private readonly IndexGenerationManager _generationManager;
    private readonly IndexEmbedder _embedder;

    public IndexSearchAppService(
        IDocumentAppService documentAppService,
        IDocumentTypeAppService documentTypeAppService,
        IChunkRepository chunkRepository,
        IRepository<IndexedDocument, Guid> documentRepository,
        IChunkSearchIndex searchIndex,
        IndexGenerationManager generationManager,
        IndexEmbedder embedder)
    {
        _documentAppService = documentAppService;
        _documentTypeAppService = documentTypeAppService;
        _chunkRepository = chunkRepository;
        _documentRepository = documentRepository;
        _searchIndex = searchIndex;
        _generationManager = generationManager;
        _embedder = embedder;
    }

    [UnitOfWork(isTransactional: false)]
    public virtual async Task<IndexSearchResultDto> SearchAsync(IndexSearchInput input)
    {
        Check.NotNull(input, nameof(input));
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Search);

        var query = Check.NotNullOrWhiteSpace(input.Query, nameof(input.Query), IndexSearchConsts.MaxQueryLength).Trim();
        var take = Math.Clamp(input.MaxResultCount ?? IndexSearchConsts.DefaultMaxResultCount, 1, IndexSearchConsts.MaxResultCount);

        var types = await GetVisibleTypesAsync();
        var scope = await GetScopeAsync(input.DocumentTypeCode, types);
        if (scope.ReachesNothing)
        {
            return new IndexSearchResultDto();
        }

        var vectorMatches = await SearchVectorsAsync(query, scope);
        var keywordMatches = await _searchIndex.SearchKeywordsAsync(query, scope, IndexSearchConsts.CandidatesPerLeg);
        var fused = ReciprocalRankFusion.Fuse(keywordMatches, vectorMatches, IndexSearchConsts.RrfK);

        var result = new IndexSearchResultDto();
        if (fused.Count == 0)
        {
            return result;
        }

        // Load the winners plus a margin: a chunk can leave the scope between the legs and this read (its document went
        // to the recycle bin), and it is dropped here rather than returned.
        var candidates = fused.Take(take * 2).ToList();
        var ids = candidates.Select(c => c.ChunkId).ToList();
        var chunks = (await AsyncExecuter.ToListAsync(
                ChunkScopeFilter.Apply(await _chunkRepository.GetQueryableAsync(), scope).Where(c => ids.Contains(c.Id))))
            .ToDictionary(c => c.Id);
        var documents = await GetDocumentsAsync(chunks.Values.Select(c => c.DocumentId));

        foreach (var candidate in candidates)
        {
            if (!chunks.TryGetValue(candidate.ChunkId, out var chunk))
            {
                continue;
            }

            var text = TextTruncator.AtCharBoundary(chunk.Text, IndexSearchConsts.MaxHitTextLength);
            result.Hits.Add(new IndexSearchHitDto
            {
                Pointer = IndexPointer.ForChunk(chunk.Id).ToString(),
                DocumentId = chunk.DocumentId,
                DocumentTitle = documents.GetValueOrDefault(chunk.DocumentId)?.Title,
                DocumentTypeCode = types.GetValueOrDefault(chunk.DocumentTypeId),
                HeadingPath = chunk.HeadingPath,
                Text = text,
                TextTruncated = text.Length < chunk.Text.Length,
                TextTotalLength = chunk.Text.Length,
                Score = candidate.Score,
                KeywordRank = candidate.KeywordRank,
                VectorRank = candidate.VectorRank
            });

            if (result.Hits.Count == take)
            {
                break;
            }
        }

        return result;
    }

    public virtual async Task<DocumentOutlineDto> GetOutlineAsync(Guid documentId)
    {
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Search);

        var types = await GetVisibleTypesAsync();
        var scope = await GetScopeAsync(null, types);
        var chunks = await GetOutlineChunksAsync(documentId, scope);
        var document = await _documentRepository.FindAsync(documentId);
        if (chunks.Count == 0 || document is null)
        {
            throw new EntityNotFoundException(typeof(IndexedDocument), documentId);
        }

        var outline = DocumentOutline.Build(chunks.Select(c => new OutlineChunk(c.Ordinal, c.HeadingPath)));
        return new DocumentOutlineDto
        {
            DocumentId = documentId,
            DocumentTitle = document.Title,
            DocumentTypeCode = types.GetValueOrDefault(chunks[0].DocumentTypeId),
            Nodes = outline.Nodes
                .Take(IndexSearchConsts.MaxOutlineNodes)
                .Select(n => new OutlineNodeDto
                {
                    Pointer = IndexPointer.ForNode(documentId, n.Path).ToString(),
                    Title = n.Title,
                    Depth = n.Depth,
                    Level = n.Level,
                    ChunkCount = n.ChunkCount
                })
                .ToList(),
            Truncated = outline.Nodes.Count > IndexSearchConsts.MaxOutlineNodes,
            TotalNodeCount = outline.Nodes.Count,
            ChunkCount = chunks.Count
        };
    }

    public virtual async Task<IndexReadResultDto> ReadAsync(IndexReadInput input)
    {
        Check.NotNull(input, nameof(input));
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Search);

        if (!IndexPointer.TryParse(input.Pointer, out var pointer))
        {
            throw new BusinessException(VaultIndexErrorCodes.InvalidPointer).WithData("Pointer", input.Pointer);
        }

        var types = await GetVisibleTypesAsync();
        var scope = await GetScopeAsync(null, types);

        return pointer.Kind == IndexPointerKind.Chunk
            ? await ReadChunkAsync(pointer, scope, types)
            : await ReadNodeAsync(pointer, scope, types);
    }

    protected virtual async Task<IndexReadResultDto> ReadChunkAsync(
        IndexPointer pointer,
        ChunkSearchScope scope,
        IReadOnlyDictionary<Guid, string> types)
    {
        var chunks = ChunkScopeFilter.Apply(await _chunkRepository.GetQueryableAsync(), scope);
        var chunk = await AsyncExecuter.FirstOrDefaultAsync(chunks.Where(c => c.Id == pointer.Id))
                    ?? throw new EntityNotFoundException(typeof(Chunk), pointer.Id);

        var neighbours = await AsyncExecuter.ToListAsync(chunks
            .Where(c => c.DocumentId == chunk.DocumentId && (c.Ordinal == chunk.Ordinal - 1 || c.Ordinal == chunk.Ordinal + 1))
            .Select(c => new { c.Id, c.Ordinal }));
        var document = await _documentRepository.FindAsync(chunk.DocumentId);
        var text = TextTruncator.AtCharBoundary(chunk.Text, IndexSearchConsts.MaxReadTextLength);

        return new IndexReadResultDto
        {
            Pointer = pointer.ToString(),
            DocumentId = chunk.DocumentId,
            DocumentTitle = document?.Title,
            DocumentTypeCode = types.GetValueOrDefault(chunk.DocumentTypeId),
            HeadingPath = chunk.HeadingPath,
            Text = text,
            TextTruncated = text.Length < chunk.Text.Length,
            TextTotalLength = chunk.Text.Length,
            ChunkCount = 1,
            PreviousPointer = neighbours.Where(n => n.Ordinal < chunk.Ordinal).Select(n => IndexPointer.ForChunk(n.Id).ToString()).FirstOrDefault(),
            NextPointer = neighbours.Where(n => n.Ordinal > chunk.Ordinal).Select(n => IndexPointer.ForChunk(n.Id).ToString()).FirstOrDefault()
        };
    }

    protected virtual async Task<IndexReadResultDto> ReadNodeAsync(
        IndexPointer pointer,
        ChunkSearchScope scope,
        IReadOnlyDictionary<Guid, string> types)
    {
        var documentId = pointer.Id;
        var outlineChunks = await GetOutlineChunksAsync(documentId, scope);
        var node = DocumentOutline.Build(outlineChunks.Select(c => new OutlineChunk(c.Ordinal, c.HeadingPath))).Find(pointer.NodePath)
                   ?? throw new EntityNotFoundException(typeof(Chunk), pointer.ToString());

        var inNode = ChunkScopeFilter.Apply(await _chunkRepository.GetQueryableAsync(), scope)
            .Where(c => c.DocumentId == documentId && c.Ordinal >= node.FirstOrdinal && c.Ordinal <= node.LastOrdinal);
        var textLengths = await AsyncExecuter.ToListAsync(inNode.OrderBy(c => c.Ordinal).Select(c => new { c.Id, c.Text.Length }));
        var totalLength = textLengths.Sum(t => t.Length) + ChunkSeparator.Length * Math.Max(0, textLengths.Count - 1);

        // Load only the chunks that fit under the cap, then cut the last one.
        var wanted = new List<Guid>();
        var budget = IndexSearchConsts.MaxReadTextLength;
        foreach (var entry in textLengths)
        {
            wanted.Add(entry.Id);
            budget -= entry.Length + ChunkSeparator.Length;
            if (budget <= 0)
            {
                break;
            }
        }

        var texts = (await AsyncExecuter.ToListAsync(inNode.Where(c => wanted.Contains(c.Id)).OrderBy(c => c.Ordinal).Select(c => c.Text)));
        var text = TextTruncator.AtCharBoundary(string.Join(ChunkSeparator, texts), IndexSearchConsts.MaxReadTextLength);
        var document = await _documentRepository.FindAsync(documentId);

        return new IndexReadResultDto
        {
            Pointer = pointer.ToString(),
            DocumentId = documentId,
            DocumentTitle = document?.Title,
            DocumentTypeCode = types.GetValueOrDefault(outlineChunks[0].DocumentTypeId),
            HeadingPath = node.HeadingPath,
            Text = text,
            TextTruncated = text.Length < totalLength,
            TextTotalLength = totalLength,
            ChunkCount = textLengths.Count
        };
    }

    /// <summary>
    /// The vector leg, when there is one. Searching with a model other than the one the serving vectors came from would
    /// compare two vector spaces, so it is refused, like ingestion refuses it.
    /// </summary>
    protected virtual async Task<IReadOnlyList<ChunkMatch>> SearchVectorsAsync(string query, ChunkSearchScope scope)
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
        return await _searchIndex.SearchVectorsAsync(serving.Id, embedding.Vectors[0], scope, IndexSearchConsts.CandidatesPerLeg);
    }

    /// <summary>
    /// The caller's read scope, from Extract, narrowed to <paramref name="documentTypeCode"/> when given. Extract refuses
    /// a caller without its entry permission; a scope version this Index does not know is refused here.
    /// </summary>
    protected virtual async Task<ChunkSearchScope> GetScopeAsync(string? documentTypeCode, IReadOnlyDictionary<Guid, string> visibleTypes)
    {
        var readScope = await _documentAppService.GetReadScopeAsync();
        if (readScope.Version != SupportedReadScopeVersion)
        {
            throw new BusinessException(VaultIndexErrorCodes.UnsupportedReadScope).WithData("Version", readScope.Version);
        }

        IEnumerable<Guid>? requested = null;
        if (!string.IsNullOrWhiteSpace(documentTypeCode))
        {
            // A code the caller cannot see narrows to nothing, the same answer as a code that does not exist.
            requested = visibleTypes.Where(t => t.Value == documentTypeCode.Trim()).Select(t => t.Key).ToList();
        }

        return new ChunkSearchScope(readScope.IsUnrestricted, readScope.DocumentTypeIds, readScope.OwnerId, requested);
    }

    /// <summary>The document types the caller can see, id to type code, for citing hits and resolving a type filter.</summary>
    protected virtual async Task<IReadOnlyDictionary<Guid, string>> GetVisibleTypesAsync()
    {
        var types = await _documentTypeAppService.GetVisibleSummariesAsync();
        return types.ToDictionary(t => t.Id, t => t.TypeCode);
    }

    /// <summary>A document's chunks within the scope, with only the outline's columns: a document can have thousands.</summary>
    protected virtual async Task<List<OutlineRow>> GetOutlineChunksAsync(Guid documentId, ChunkSearchScope scope)
    {
        return await AsyncExecuter.ToListAsync(
            ChunkScopeFilter.Apply(await _chunkRepository.GetQueryableAsync(), scope)
                .Where(c => c.DocumentId == documentId)
                .OrderBy(c => c.Ordinal)
                .Select(c => new OutlineRow(c.Ordinal, c.HeadingPath, c.DocumentTypeId)));
    }

    protected sealed record OutlineRow(int Ordinal, string? HeadingPath, Guid DocumentTypeId);

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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Runs;
using Dignite.Vault.Index.Search;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Index.Status;

/// <summary>
/// The index status page. The summary counts the tenant's rows and shows nothing of a document's content; the
/// document list and runs show titles and errors, so they pass the rows through the caller's read scope, the same rule
/// search uses. A rebuild queues an ordinary index job with <see cref="IndexDocumentJobArgs.Force"/> set, so it goes
/// through the same claim, retry cap and run record as an event.
/// </summary>
public class IndexStatusAppService : VaultIndexAppService, IIndexStatusAppService
{
    /// <summary>Runs listed per document: enough to see a retry sequence and what came before it.</summary>
    protected const int MaxRunCount = 20;

    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IChunkRepository _chunkRepository;
    private readonly IRepository<IndexRun, Guid> _runRepository;
    private readonly IndexGenerationManager _generationManager;
    private readonly IndexEmbedder _embedder;
    private readonly IChunkSearchIndex _searchIndex;
    private readonly IndexReadScopeResolver _scopeResolver;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public IndexStatusAppService(
        IRepository<IndexedDocument, Guid> documentRepository,
        IChunkRepository chunkRepository,
        IRepository<IndexRun, Guid> runRepository,
        IndexGenerationManager generationManager,
        IndexEmbedder embedder,
        IChunkSearchIndex searchIndex,
        IndexReadScopeResolver scopeResolver,
        IBackgroundJobManager backgroundJobManager)
    {
        _documentRepository = documentRepository;
        _chunkRepository = chunkRepository;
        _runRepository = runRepository;
        _generationManager = generationManager;
        _embedder = embedder;
        _searchIndex = searchIndex;
        _scopeResolver = scopeResolver;
        _backgroundJobManager = backgroundJobManager;
    }

    public virtual async Task<IndexStatusSummaryDto> GetSummaryAsync()
    {
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Status);

        var documents = await _documentRepository.GetQueryableAsync();
        var byStatus = (await AsyncExecuter.ToListAsync(documents
                .GroupBy(d => d.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })))
            .ToDictionary(x => x.Status, x => x.Count);
        var serving = await _generationManager.FindServingAsync();
        var store = await _searchIndex.GetStatusAsync(serving?.Id);

        return new IndexStatusSummaryDto
        {
            PendingCount = byStatus.GetValueOrDefault(IndexedDocumentStatus.Pending),
            IndexingCount = byStatus.GetValueOrDefault(IndexedDocumentStatus.Indexing),
            IndexedCount = byStatus.GetValueOrDefault(IndexedDocumentStatus.Indexed),
            FailedCount = byStatus.GetValueOrDefault(IndexedDocumentStatus.Failed),
            ArchivedCount = await AsyncExecuter.CountAsync(documents.Where(d => d.IsArchived)),
            WithheldCount = await AsyncExecuter.CountAsync(documents.Where(d => d.IsWithheld)),
            ChunkCount = await AsyncExecuter.LongCountAsync(await _chunkRepository.GetQueryableAsync()),
            VectorCount = store.VectorCount,
            ExactSearchChunkLimit = IndexSearchConsts.ExactSearchChunkLimit,
            ServingModelId = serving?.ModelId,
            ServingDimensions = serving?.Dimensions,
            ServingSince = serving?.ServingSince,
            ConfiguredModelId = GetConfiguredModelId(),
            KeywordIndexAvailable = store.KeywordIndexAvailable,
            KeywordIndexPendingChanges = store.KeywordIndexPendingChanges,
            KeywordIndexPopulating = store.KeywordIndexPopulating
        };
    }

    public virtual async Task<PagedResultDto<IndexedDocumentStatusDto>> GetDocumentListAsync(GetIndexedDocumentListInput input)
    {
        Check.NotNull(input, nameof(input));
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Status);

        var types = await _scopeResolver.GetVisibleTypesAsync();
        var scope = await _scopeResolver.GetScopeAsync(null, types);
        var query = ChunkScopeFilter.ApplyReadScope(await _documentRepository.GetQueryableAsync(), scope);
        if (input.Status is { } status)
        {
            query = query.Where(d => d.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(input.Filter))
        {
            var filter = input.Filter.Trim();
            query = query.Where(d => d.Title != null && d.Title.Contains(filter));
        }

        var totalCount = await AsyncExecuter.CountAsync(query);
        var page = await AsyncExecuter.ToListAsync(Sort(query, input.Sorting)
            .Skip(input.SkipCount)
            .Take(input.MaxResultCount));
        var errors = await GetLastErrorsAsync(page.Where(d => d.Status == IndexedDocumentStatus.Failed).Select(d => d.Id).ToList());

        return new PagedResultDto<IndexedDocumentStatusDto>(totalCount, page.Select(d => new IndexedDocumentStatusDto
        {
            DocumentId = d.Id,
            Title = d.Title,
            DocumentTypeId = d.DocumentTypeId,
            DocumentTypeCode = types.GetValueOrDefault(d.DocumentTypeId),
            Status = d.Status,
            IsArchived = d.IsArchived,
            IsWithheld = d.IsWithheld,
            ChunkCount = d.ChunkCount,
            FailedAttempts = d.FailedAttempts,
            LastIndexedTime = d.LastIndexedTime,
            LastModificationTime = d.LastModificationTime ?? d.CreationTime,
            LastError = errors.GetValueOrDefault(d.Id)
        }).ToList());
    }

    public virtual async Task<ListResultDto<IndexRunDto>> GetRunsAsync(Guid documentId)
    {
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Status);
        await GetReadableDocumentAsync(documentId);

        var runs = await AsyncExecuter.ToListAsync((await _runRepository.GetQueryableAsync())
            .Where(r => r.DocumentId == documentId)
            .OrderByDescending(r => r.StartedTime)
            .Take(MaxRunCount));

        return new ListResultDto<IndexRunDto>(runs.Select(r => new IndexRunDto
        {
            Id = r.Id,
            Trigger = r.Trigger,
            Status = r.Status,
            AttemptNumber = r.AttemptNumber,
            StartedTime = r.StartedTime,
            FinishedTime = r.FinishedTime,
            ErrorMessage = r.ErrorMessage,
            ChunkCount = r.ChunkCount,
            EmbeddingTokenCount = r.EmbeddingTokenCount
        }).ToList());
    }

    public virtual async Task RebuildAsync(Guid documentId)
    {
        await AuthorizationService.CheckAsync(VaultIndexPermissions.StatusRebuild);
        await GetReadableDocumentAsync(documentId);

        await _backgroundJobManager.EnqueueAsync(new IndexDocumentJobArgs
        {
            DocumentId = documentId,
            TenantId = CurrentTenant.Id,
            Kind = IndexDocumentJobKind.Sync,
            Trigger = IndexRunTrigger.Manual,
            Force = true
        });
    }

    /// <summary>The document's row, if the caller's read scope reaches it; not found otherwise.</summary>
    protected virtual async Task<IndexedDocument> GetReadableDocumentAsync(Guid documentId)
    {
        var types = await _scopeResolver.GetVisibleTypesAsync();
        var scope = await _scopeResolver.GetScopeAsync(null, types);
        return await AsyncExecuter.FirstOrDefaultAsync(
                   ChunkScopeFilter.ApplyReadScope(await _documentRepository.GetQueryableAsync(), scope).Where(d => d.Id == documentId))
               ?? throw new EntityNotFoundException(typeof(IndexedDocument), documentId);
    }

    /// <summary>The latest failed run's error for each failed document of a page.</summary>
    protected virtual async Task<Dictionary<Guid, string?>> GetLastErrorsAsync(IReadOnlyCollection<Guid> documentIds)
    {
        var errors = new Dictionary<Guid, string?>();
        var runs = await _runRepository.GetQueryableAsync();
        foreach (var documentId in documentIds)
        {
            errors[documentId] = await AsyncExecuter.FirstOrDefaultAsync(runs
                .Where(r => r.DocumentId == documentId && r.Status == IndexRunStatus.Failed)
                .OrderByDescending(r => r.StartedTime)
                .Select(r => r.ErrorMessage));
        }

        return errors;
    }

    protected virtual string? GetConfiguredModelId()
    {
        if (!_embedder.IsConfigured)
        {
            return null;
        }

        try
        {
            return _embedder.GetModelId();
        }
        catch (BusinessException)
        {
            // A generator that reports no model id; ingestion refuses it the same way.
            return null;
        }
    }

    /// <summary>The sortable columns; anything else falls back to the most recently changed first.</summary>
    protected static IQueryable<IndexedDocument> Sort(IQueryable<IndexedDocument> query, string? sorting)
    {
        return sorting?.Trim().ToLowerInvariant() switch
        {
            "title" or "title asc" => query.OrderBy(d => d.Title).ThenBy(d => d.Id),
            "title desc" => query.OrderByDescending(d => d.Title).ThenBy(d => d.Id),
            "status" or "status asc" => query.OrderBy(d => d.Status).ThenBy(d => d.Id),
            "status desc" => query.OrderByDescending(d => d.Status).ThenBy(d => d.Id),
            "lastindexedtime" or "lastindexedtime asc" => query.OrderBy(d => d.LastIndexedTime).ThenBy(d => d.Id),
            "lastindexedtime desc" => query.OrderByDescending(d => d.LastIndexedTime).ThenBy(d => d.Id),
            _ => query.OrderByDescending(d => d.LastModificationTime ?? d.CreationTime).ThenBy(d => d.Id)
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>The job name is a serialized string: renaming it orphans queued jobs.</summary>
[BackgroundJobName("VaultIndex.Backfill")]
public class IndexBackfillJobArgs
{
    public Guid? TenantId { get; set; }

    /// <summary>The last document id of the previous page; <c>null</c> to start from the beginning.</summary>
    public Guid? AfterId { get; set; }

    /// <summary>
    /// Walk the documents already in the index instead of Extract's Ready ones: the rebuild after an index profile
    /// changed. Each sync re-chunks a document only when its settings stamp differs, so walking more documents than
    /// changed costs a pull each, not an embedding.
    /// </summary>
    public bool IndexedOnly { get; set; }

    /// <summary>With <see cref="IndexedOnly"/>, only the documents of this type; every type when empty.</summary>
    public Guid? DocumentTypeId { get; set; }

    /// <summary>
    /// Walk the documents with chunks that have no vectors yet in the configured embedding model's generation, and queue
    /// an <see cref="IndexDocumentJobKind.Embed"/> for each: the rebuild after the model changed. Their chunks are
    /// embedded as they are, with no pull from Extract and no re-chunking.
    /// </summary>
    public bool EmbedOnly { get; set; }
}

/// <summary>
/// Walks a tenant's Ready documents one page at a time with Extract's keyset cursor (or, for a rebuild, the documents
/// already in the index, with its own) and enqueues a sync for each, then enqueues itself for the next page. Everything
/// it enqueues is low priority, so a backfill or a rebuild never starves Extract's pipeline jobs or the syncs that
/// events trigger. A document already indexed with the same text and settings is skipped by the sync itself, so running
/// a backfill twice costs pulls, not embeddings; likewise a document already in the new model's generation is skipped
/// by its embed job, and a second walk for a model change costs a lookup per document.
/// </summary>
public class IndexBackfillJob : AsyncBackgroundJob<IndexBackfillJobArgs>, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IDocumentIntegrationService _integrationService;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IndexGenerationManager _generationManager;
    private readonly IndexEmbedder _embedder;
    private readonly VaultIndexIngestionOptions _options;

    public IndexBackfillJob(
        ICurrentTenant currentTenant,
        IDocumentIntegrationService integrationService,
        IRepository<IndexedDocument, Guid> documentRepository,
        IUnitOfWorkManager unitOfWorkManager,
        IAsyncQueryableExecuter asyncExecuter,
        IBackgroundJobManager backgroundJobManager,
        IndexGenerationManager generationManager,
        IndexEmbedder embedder,
        IOptions<VaultIndexIngestionOptions> options)
    {
        _currentTenant = currentTenant;
        _integrationService = integrationService;
        _documentRepository = documentRepository;
        _unitOfWorkManager = unitOfWorkManager;
        _asyncExecuter = asyncExecuter;
        _backgroundJobManager = backgroundJobManager;
        _generationManager = generationManager;
        _embedder = embedder;
        _options = options.Value;
    }

    public override async Task ExecuteAsync(IndexBackfillJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            var pageSize = Math.Clamp(_options.BackfillPageSize, 1, 1000);
            var ids = args.EmbedOnly ? await GetUnembeddedIdsAsync(args, pageSize)
                : args.IndexedOnly ? await GetIndexedIdsAsync(args, pageSize)
                : await GetReadyIdsAsync(args, pageSize);
            var trigger = args.EmbedOnly ? IndexRunTrigger.ModelChange
                : args.IndexedOnly ? IndexRunTrigger.Rebuild
                : IndexRunTrigger.Backfill;

            foreach (var documentId in ids)
            {
                await _backgroundJobManager.EnqueueAsync(
                    new IndexDocumentJobArgs
                    {
                        DocumentId = documentId,
                        TenantId = args.TenantId,
                        Kind = args.EmbedOnly ? IndexDocumentJobKind.Embed : IndexDocumentJobKind.Sync,
                        Trigger = trigger
                    },
                    BackgroundJobPriority.Low);
            }

            if (ids.Count == pageSize)
            {
                await _backgroundJobManager.EnqueueAsync(
                    new IndexBackfillJobArgs
                    {
                        TenantId = args.TenantId,
                        AfterId = ids[^1],
                        IndexedOnly = args.IndexedOnly,
                        DocumentTypeId = args.DocumentTypeId,
                        EmbedOnly = args.EmbedOnly
                    },
                    BackgroundJobPriority.Low);
            }
        }
    }

    protected virtual async Task<IReadOnlyList<Guid>> GetReadyIdsAsync(IndexBackfillJobArgs args, int pageSize)
    {
        var page = await _integrationService.GetReadyIdsAsync(new GetReadyDocumentIdsInput
        {
            AfterId = args.AfterId,
            MaxResultCount = pageSize
        });
        return page.Items;
    }

    /// <summary>
    /// A page of the documents with chunks that have no vectors in the configured model's generation yet: all of them
    /// before the first vectors from that model create it.
    /// </summary>
    protected virtual async Task<IReadOnlyList<Guid>> GetUnembeddedIdsAsync(IndexBackfillJobArgs args, int pageSize)
    {
        if (!_embedder.IsConfigured)
        {
            return [];
        }

        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: false);
        var target = await _generationManager.FindTargetAsync(_embedder.GetModelId());
        var query = (await _documentRepository.GetQueryableAsync()).Where(d => d.ChunkCount > 0);
        if (target is not null)
        {
            var targetId = target.Id;
            query = query.Where(d => d.GenerationId != targetId);
        }

        if (args.AfterId is { } afterId)
        {
            query = query.Where(d => d.Id.CompareTo(afterId) > 0);
        }

        var ids = await _asyncExecuter.ToListAsync(query.OrderBy(d => d.Id).Select(d => d.Id).Take(pageSize));
        await uow.CompleteAsync();
        return ids;
    }

    /// <summary>A page of the index's own documents, by id, of one type or all.</summary>
    protected virtual async Task<IReadOnlyList<Guid>> GetIndexedIdsAsync(IndexBackfillJobArgs args, int pageSize)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: false);
        var query = await _documentRepository.GetQueryableAsync();
        if (args.DocumentTypeId is { } typeId)
        {
            query = query.Where(d => d.DocumentTypeId == typeId);
        }

        if (args.AfterId is { } afterId)
        {
            query = query.Where(d => d.Id.CompareTo(afterId) > 0);
        }

        var ids = await _asyncExecuter.ToListAsync(query.OrderBy(d => d.Id).Select(d => d.Id).Take(pageSize));
        await uow.CompleteAsync();
        return ids;
    }
}

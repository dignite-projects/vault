using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>The job name is a serialized string: renaming it orphans queued jobs.</summary>
[BackgroundJobName("VaultIndex.Backfill")]
public class IndexBackfillJobArgs
{
    public Guid? TenantId { get; set; }

    /// <summary>The last document id of the previous page; <c>null</c> to start from the beginning.</summary>
    public Guid? AfterId { get; set; }
}

/// <summary>
/// Walks a tenant's Ready documents one page at a time with Extract's keyset cursor and enqueues a sync for each,
/// then enqueues itself for the next page. Everything it enqueues is low priority, so a backfill or a rebuild never
/// starves Extract's pipeline jobs or the syncs that events trigger. A document already indexed with the same text
/// and settings is skipped by the sync itself, so running a backfill twice costs pulls, not embeddings.
/// </summary>
public class IndexBackfillJob : AsyncBackgroundJob<IndexBackfillJobArgs>, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IDocumentIntegrationService _integrationService;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly VaultIndexIngestionOptions _options;

    public IndexBackfillJob(
        ICurrentTenant currentTenant,
        IDocumentIntegrationService integrationService,
        IBackgroundJobManager backgroundJobManager,
        IOptions<VaultIndexIngestionOptions> options)
    {
        _currentTenant = currentTenant;
        _integrationService = integrationService;
        _backgroundJobManager = backgroundJobManager;
        _options = options.Value;
    }

    public override async Task ExecuteAsync(IndexBackfillJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            var pageSize = Math.Clamp(_options.BackfillPageSize, 1, 1000);
            var page = await _integrationService.GetReadyIdsAsync(new GetReadyDocumentIdsInput
            {
                AfterId = args.AfterId,
                MaxResultCount = pageSize
            });

            foreach (var documentId in page.Items)
            {
                await _backgroundJobManager.EnqueueAsync(
                    new IndexDocumentJobArgs
                    {
                        DocumentId = documentId,
                        TenantId = args.TenantId,
                        Kind = IndexDocumentJobKind.Sync,
                        Trigger = IndexRunTrigger.Backfill
                    },
                    BackgroundJobPriority.Low);
            }

            if (page.Items.Count == pageSize)
            {
                await _backgroundJobManager.EnqueueAsync(
                    new IndexBackfillJobArgs { TenantId = args.TenantId, AfterId = page.Items[^1] },
                    BackgroundJobPriority.Low);
            }
        }
    }
}

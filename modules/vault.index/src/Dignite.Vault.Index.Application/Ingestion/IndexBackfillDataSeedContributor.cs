using System;
using System.Threading.Tasks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// First install: when a layer has never been indexed, start a backfill of its Ready documents. Runs with
/// <c>--migrate-database</c> for the Host and every tenant, and on tenant creation, where it finds nothing to index.
/// Events index everything after that, so the backfill is started only while the layer has no indexed document and
/// no embedding generation; a second run before the first backfill finished only re-enqueues syncs that are then
/// skipped as unchanged. A host without an embedding model starts nothing, and gets its backfill on the first seed
/// after a model is added.
/// </summary>
public class IndexBackfillDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IRepository<IndexGeneration, Guid> _generationRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IndexEmbedder _embedder;

    public IndexBackfillDataSeedContributor(
        ICurrentTenant currentTenant,
        IRepository<IndexedDocument, Guid> documentRepository,
        IRepository<IndexGeneration, Guid> generationRepository,
        IBackgroundJobManager backgroundJobManager,
        IndexEmbedder embedder)
    {
        _currentTenant = currentTenant;
        _documentRepository = documentRepository;
        _generationRepository = generationRepository;
        _backgroundJobManager = backgroundJobManager;
        _embedder = embedder;
    }

    public virtual async Task SeedAsync(DataSeedContext context)
    {
        if (!_embedder.IsConfigured)
        {
            return;
        }

        using (_currentTenant.Change(context.TenantId))
        {
            if (await _generationRepository.GetCountAsync() > 0 || await _documentRepository.GetCountAsync() > 0)
            {
                return;
            }

            await _backgroundJobManager.EnqueueAsync(
                new IndexBackfillJobArgs { TenantId = context.TenantId },
                BackgroundJobPriority.Low);
        }
    }
}

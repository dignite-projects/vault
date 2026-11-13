using System;
using System.Threading.Tasks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Starts the walks a layer needs. Runs with <c>--migrate-database</c> for the Host and every tenant, and on tenant
/// creation, where it finds nothing to index.
/// <list type="bullet">
/// <item><b>First install</b>: when a layer has never been indexed, a backfill of its Ready documents. Events index
/// everything after that, so the backfill is started only while the layer has no indexed document and no embedding
/// generation; a second run before the first backfill finished only re-enqueues syncs that are then skipped as
/// unchanged.</item>
/// <item><b>A new embedding model</b>: when the configured model is not the serving generation's, or a rebuild for it
/// is under way, the walk that embeds every document missing from the new generation. A second walk only looks up
/// documents already there, so this also resumes a rebuild whose jobs ABP abandoned. A building generation of a model
/// the host no longer configures is abandoned, and a complete one is switched to.</item>
/// </list>
/// A host without an embedding model starts nothing, and gets its backfill on the first seed after a model is added.
/// </summary>
public class IndexBackfillDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IRepository<IndexGeneration, Guid> _generationRepository;
    private readonly IndexGenerationManager _generationManager;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IndexEmbedder _embedder;

    public IndexBackfillDataSeedContributor(
        ICurrentTenant currentTenant,
        IRepository<IndexedDocument, Guid> documentRepository,
        IRepository<IndexGeneration, Guid> generationRepository,
        IndexGenerationManager generationManager,
        IUnitOfWorkManager unitOfWorkManager,
        IBackgroundJobManager backgroundJobManager,
        IndexEmbedder embedder)
    {
        _currentTenant = currentTenant;
        _documentRepository = documentRepository;
        _generationRepository = generationRepository;
        _generationManager = generationManager;
        _unitOfWorkManager = unitOfWorkManager;
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
            if (await _generationRepository.GetCountAsync() == 0 && await _documentRepository.GetCountAsync() == 0)
            {
                await _backgroundJobManager.EnqueueAsync(
                    new IndexBackfillJobArgs { TenantId = context.TenantId },
                    BackgroundJobPriority.Low);
                return;
            }

            await ContinueModelChangeAsync(context.TenantId);
        }
    }

    protected virtual async Task ContinueModelChangeAsync(Guid? tenantId)
    {
        string modelId;
        try
        {
            modelId = _embedder.GetModelId();
        }
        catch (BusinessException)
        {
            // A generator that reports no model id; ingestion refuses it the same way.
            return;
        }

        bool abandoned;
        var switched = false;
        IndexGeneration? building;
        IndexGeneration? serving;
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true))
        {
            abandoned = await _generationManager.AbandonStaleBuildingAsync(modelId);
            building = await _generationManager.FindBuildingAsync();
            await uow.CompleteAsync();
        }

        if (building is not null)
        {
            try
            {
                using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
                switched = await _generationManager.TrySwitchAsync(building.Id);
                await uow.CompleteAsync();
            }
            catch (AbpDbConcurrencyException)
            {
                // A running host's job switched it at the same moment.
            }
        }

        using (var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: false))
        {
            serving = await _generationManager.FindServingAsync();
            await uow.CompleteAsync();
        }

        if (abandoned || switched)
        {
            using var cleanup = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: false);
            await _generationManager.DeleteRetiredVectorsAsync();
            await cleanup.CompleteAsync();
        }

        // After an abandon the walk also moves back the documents the abandoned generation held.
        var walk = abandoned
                   || (building is not null && !switched)
                   || (serving is not null && !string.Equals(serving.ModelId, modelId, StringComparison.Ordinal));
        if (walk)
        {
            await _backgroundJobManager.EnqueueAsync(
                new IndexBackfillJobArgs { TenantId = tenantId, EmbedOnly = true },
                BackgroundJobPriority.Low);
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Search;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp.Linq;

namespace Dignite.Vault.Index.Generations;

/// <summary>
/// The embedding generations of the ambient tenant: at most one serving, which search reads, and at most one building,
/// which ingestion fills for a newly configured model while the serving one keeps answering.
/// <para>
/// The first vectors of a tenant create the serving generation. Once the host's model is no longer the serving
/// generation's, vectors from it go into a building generation for it. When every document with chunks has vectors
/// there (its <see cref="IndexedDocument.GenerationId"/>), <see cref="TrySwitchAsync"/> makes it serving and retires the
/// old one. A building generation of a model the host no longer configures is abandoned: retired without serving.
/// Two creators racing collide on the unique serving and building indexes; the loser is retried and finds the winner.
/// </para>
/// </summary>
public class IndexGenerationManager : DomainService
{
    private readonly IRepository<IndexGeneration, Guid> _generationRepository;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IChunkSearchIndex _searchIndex;
    private readonly IAsyncQueryableExecuter _asyncExecuter;

    public IndexGenerationManager(
        IRepository<IndexGeneration, Guid> generationRepository,
        IRepository<IndexedDocument, Guid> documentRepository,
        IChunkSearchIndex searchIndex,
        IAsyncQueryableExecuter asyncExecuter)
    {
        _generationRepository = generationRepository;
        _documentRepository = documentRepository;
        _searchIndex = searchIndex;
        _asyncExecuter = asyncExecuter;
    }

    public virtual async Task<IndexGeneration?> FindServingAsync()
    {
        var serving = await _generationRepository.GetListAsync(g => g.Status == IndexGenerationStatus.Serving);
        return serving.OrderByDescending(g => g.ServingSince).FirstOrDefault();
    }

    public virtual async Task<IndexGeneration?> FindBuildingAsync()
    {
        var building = await _generationRepository.GetListAsync(g => g.Status == IndexGenerationStatus.Building);
        return building.OrderByDescending(g => g.CreationTime).FirstOrDefault();
    }

    /// <summary>
    /// The generation that vectors from <paramref name="modelId"/> go into, without creating one: the building
    /// generation of that model, else the serving one when it is that model's. <c>null</c> when neither is.
    /// </summary>
    public virtual async Task<IndexGeneration?> FindTargetAsync(string modelId)
    {
        var building = await FindBuildingAsync();
        if (building is not null && string.Equals(building.ModelId, modelId, StringComparison.Ordinal))
        {
            return building;
        }

        var serving = await FindServingAsync();
        return serving is not null && string.Equals(serving.ModelId, modelId, StringComparison.Ordinal) ? serving : null;
    }

    /// <summary>
    /// The generation that vectors from <paramref name="modelId"/> and <paramref name="dimensions"/> go into, created when
    /// missing: the serving one when it is that model's, or the tenant has none yet (the first vectors); otherwise the
    /// building one for it. A building generation of another model is abandoned on the way. Call it inside a
    /// transaction: the generation rows it changes are saved at once.
    /// </summary>
    public virtual async Task<IndexGenerationTarget> GetOrCreateTargetAsync(string modelId, int dimensions)
    {
        var serving = await FindServingAsync();
        var building = await FindBuildingAsync();

        if (serving is null)
        {
            var first = new IndexGeneration(GuidGenerator.Create(), CurrentTenant.Id, modelId, dimensions);
            first.StartServing(Clock.Now);
            return new IndexGenerationTarget(await _generationRepository.InsertAsync(first, autoSave: true), StartedBuilding: false, Abandoned: false);
        }

        var abandoned = false;
        if (building is not null && !Matches(building, modelId, dimensions))
        {
            await AbandonAsync(building);
            abandoned = true;
            building = null;
        }

        if (Matches(serving, modelId, dimensions))
        {
            return new IndexGenerationTarget(serving, StartedBuilding: false, abandoned);
        }

        if (building is not null)
        {
            return new IndexGenerationTarget(building, StartedBuilding: false, abandoned);
        }

        var next = new IndexGeneration(GuidGenerator.Create(), CurrentTenant.Id, modelId, dimensions);
        return new IndexGenerationTarget(await _generationRepository.InsertAsync(next, autoSave: true), StartedBuilding: true, abandoned);
    }

    /// <summary>
    /// Abandons the building generation when it is not <paramref name="modelId"/>'s: the host was configured back, or on
    /// to a third model, before it finished. Returns whether one was abandoned; its vectors go with
    /// <see cref="DeleteRetiredVectorsAsync"/>.
    /// </summary>
    public virtual async Task<bool> AbandonStaleBuildingAsync(string modelId)
    {
        var building = await FindBuildingAsync();
        if (building is null || string.Equals(building.ModelId, modelId, StringComparison.Ordinal))
        {
            return false;
        }

        await AbandonAsync(building);
        return true;
    }

    /// <summary>
    /// Makes the building generation serving once every document with chunks has vectors in it, and retires the one it
    /// replaces. Returns whether it switched. Call it inside a transaction, after the write that may have completed the
    /// generation has committed; then delete the retired vectors with <see cref="DeleteRetiredVectorsAsync"/>.
    /// </summary>
    public virtual async Task<bool> TrySwitchAsync(Guid buildingId)
    {
        var building = await _generationRepository.FindAsync(buildingId);
        if (building is null || building.Status != IndexGenerationStatus.Building)
        {
            return false;
        }

        var documents = await _documentRepository.GetQueryableAsync();
        if (await _asyncExecuter.AnyAsync(documents.Where(d => d.ChunkCount > 0 && d.GenerationId != buildingId)))
        {
            return false;
        }

        // Retired first and saved on its own: the unique serving index admits one serving row at a time.
        var serving = await FindServingAsync();
        if (serving is not null)
        {
            serving.Retire(Clock.Now);
            await _generationRepository.UpdateAsync(serving, autoSave: true);
        }

        building.StartServing(Clock.Now);
        await _generationRepository.UpdateAsync(building, autoSave: true);
        return true;
    }

    /// <summary>
    /// Deletes the vectors of every retired generation. A no-op for the ones already emptied, so it also finishes a
    /// deletion a crash interrupted. Call it outside a transaction: the store deletes in batches.
    /// </summary>
    public virtual async Task DeleteRetiredVectorsAsync()
    {
        var retired = await _generationRepository.GetListAsync(g => g.Status == IndexGenerationStatus.Retired);
        foreach (var generation in retired)
        {
            await _searchIndex.DeleteGenerationAsync(generation.Id);
        }
    }

    protected virtual async Task AbandonAsync(IndexGeneration building)
    {
        building.Retire(Clock.Now);
        await _generationRepository.UpdateAsync(building, autoSave: true);
    }

    protected static bool Matches(IndexGeneration generation, string modelId, int dimensions)
        => string.Equals(generation.ModelId, modelId, StringComparison.Ordinal) && generation.Dimensions == dimensions;
}

/// <summary>
/// Where vectors go. <see cref="StartedBuilding"/>: this call created a building generation, so the caller starts the
/// walk that fills it. <see cref="Abandoned"/>: it retired a building generation, whose vectors are to be deleted.
/// </summary>
public sealed record IndexGenerationTarget(IndexGeneration Generation, bool StartedBuilding, bool Abandoned);

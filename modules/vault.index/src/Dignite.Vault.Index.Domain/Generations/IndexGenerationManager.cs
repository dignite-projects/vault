using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;

namespace Dignite.Vault.Index.Generations;

/// <summary>
/// The serving embedding generation of the ambient tenant. The first index job of a tenant creates it from the
/// configured model; afterwards a different model or dimension is refused, because its vectors would not compare
/// with the ones already stored. Moving to a new model is a rebuild into a new generation.
/// </summary>
public class IndexGenerationManager : DomainService
{
    private readonly IRepository<IndexGeneration, Guid> _generationRepository;

    public IndexGenerationManager(IRepository<IndexGeneration, Guid> generationRepository)
    {
        _generationRepository = generationRepository;
    }

    public virtual async Task<IndexGeneration?> FindServingAsync()
    {
        var serving = await _generationRepository.GetListAsync(g => g.Status == IndexGenerationStatus.Serving);
        return serving.OrderByDescending(g => g.ServingSince).FirstOrDefault();
    }

    /// <summary>
    /// The serving generation for <paramref name="modelId"/> and <paramref name="dimensions"/>, created when the tenant
    /// has none. A concurrent first job that loses the race fails on the unique serving index, is retried, and then
    /// finds this one.
    /// </summary>
    public virtual async Task<IndexGeneration> GetOrCreateServingAsync(string modelId, int dimensions)
    {
        var serving = await FindServingAsync();
        if (serving is null)
        {
            var generation = new IndexGeneration(GuidGenerator.Create(), CurrentTenant.Id, modelId, dimensions);
            generation.StartServing(Clock.Now);
            return await _generationRepository.InsertAsync(generation, autoSave: true);
        }

        if (!string.Equals(serving.ModelId, modelId, StringComparison.Ordinal) || serving.Dimensions != dimensions)
        {
            throw new BusinessException(VaultIndexErrorCodes.EmbeddingModelChanged)
                .WithData("ModelId", modelId)
                .WithData("Dimensions", dimensions)
                .WithData("ServingModelId", serving.ModelId)
                .WithData("ServingDimensions", serving.Dimensions);
        }

        return serving;
    }
}

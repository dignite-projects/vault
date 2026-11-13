using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Generations;

/// <summary>
/// Which embedding model produced a set of vectors. Search reads only the serving generation and embeds the query with
/// that generation's model, and refuses when the host does not register it, since vectors from two models are not
/// comparable. Changing the model builds a new generation in the background while the serving one keeps answering,
/// and switches over when it is complete (<see cref="IndexGenerationManager"/>).
/// <para>
/// The vector column's dimension is part of the storage provider's table definition, so a model with a different
/// <see cref="Dimensions"/> also needs a schema migration, not only a new generation.
/// </para>
/// </summary>
public class IndexGeneration : AggregateRoot<Guid>, IMultiTenant, IHasCreationTime
{
    public virtual Guid? TenantId { get; protected set; }

    /// <summary>The embedding model's identifier as the host configures it.</summary>
    public virtual string ModelId { get; protected set; } = default!;

    public virtual int Dimensions { get; protected set; }

    public virtual IndexGenerationStatus Status { get; protected set; }

    public virtual DateTime? ServingSince { get; protected set; }

    public virtual DateTime? RetiredTime { get; protected set; }

    public virtual DateTime CreationTime { get; protected set; }

    protected IndexGeneration()
    {
    }

    public IndexGeneration(Guid id, Guid? tenantId, string modelId, int dimensions)
        : base(id)
    {
        TenantId = tenantId;
        ModelId = Check.NotNullOrWhiteSpace(modelId, nameof(modelId), IndexingConsts.MaxModelIdLength);
        Dimensions = Check.Range(dimensions, nameof(dimensions), 1, int.MaxValue);
        Status = IndexGenerationStatus.Building;
    }

    /// <summary>Makes this the generation search reads. The caller retires the one it replaces.</summary>
    public virtual void StartServing(DateTime now)
    {
        Status = IndexGenerationStatus.Serving;
        ServingSince = now;
    }

    /// <summary>Replaced by a newer generation, or abandoned before it served; its vectors are deleted.</summary>
    public virtual void Retire(DateTime now)
    {
        Status = IndexGenerationStatus.Retired;
        RetiredTime = now;
    }
}

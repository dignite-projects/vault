using System;
using Microsoft.Data.SqlTypes;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// One chunk's vector in one embedding generation. A storage record of the SQL Server provider, not a domain entity:
/// the domain sees vectors only through <c>IChunkSearchIndex</c>. The read-scope facts are repeated here so the vector
/// leg filters and scans one clustered range without joining the chunk table.
/// </summary>
public class ChunkVectorRecord : Entity, IMultiTenant
{
    public virtual Guid GenerationId { get; set; }

    public virtual Guid ChunkId { get; set; }

    public virtual Guid? TenantId { get; set; }

    public virtual Guid DocumentId { get; set; }

    public virtual Guid DocumentTypeId { get; set; }

    public virtual Guid? OwnerId { get; set; }

    public virtual SqlVector<float> Embedding { get; set; }

    public override object[] GetKeys() => [GenerationId, ChunkId];
}

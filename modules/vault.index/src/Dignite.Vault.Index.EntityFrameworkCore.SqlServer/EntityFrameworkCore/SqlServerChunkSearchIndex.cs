using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Index.Search;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// The default <see cref="IChunkSearchIndex"/>: vectors in <c>VaultIndexChunkVectors</c>, written through EF Core in
/// the caller's unit of work. Every statement goes through the DbSet, so the tenant filter applies.
/// </summary>
public class SqlServerChunkSearchIndex : IChunkSearchIndex, ITransientDependency
{
    private readonly IDbContextProvider<VaultIndexSqlServerDbContext> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly VaultIndexSqlServerOptions _options;

    public SqlServerChunkSearchIndex(
        IDbContextProvider<VaultIndexSqlServerDbContext> dbContextProvider,
        ICurrentTenant currentTenant,
        IOptions<VaultIndexSqlServerOptions> options)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _options = options.Value;
    }

    public virtual int? VectorDimensions => _options.VectorDimensions;

    public virtual async Task ReplaceDocumentAsync(
        Guid documentId,
        Guid generationId,
        IReadOnlyList<ChunkVector> vectors,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();

        await dbContext.ChunkVectors
            .Where(v => v.DocumentId == documentId && v.GenerationId == generationId)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var vector in vectors)
        {
            if (vector.Vector.Length != _options.VectorDimensions)
            {
                throw new BusinessException(VaultIndexErrorCodes.EmbeddingDimensionMismatch)
                    .WithData("Dimensions", vector.Vector.Length)
                    .WithData("StorageDimensions", _options.VectorDimensions);
            }

            dbContext.ChunkVectors.Add(new ChunkVectorRecord
            {
                GenerationId = generationId,
                ChunkId = vector.ChunkId,
                TenantId = _currentTenant.Id,
                DocumentId = vector.DocumentId,
                DocumentTypeId = vector.DocumentTypeId,
                OwnerId = vector.OwnerId,
                Embedding = new SqlVector<float>(vector.Vector)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task UpdateDocumentFiltersAsync(
        Guid documentId,
        Guid documentTypeId,
        Guid? ownerId,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        await dbContext.ChunkVectors
            .Where(v => v.DocumentId == documentId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(v => v.DocumentTypeId, documentTypeId).SetProperty(v => v.OwnerId, ownerId),
                cancellationToken);
    }

    public virtual async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        await dbContext.ChunkVectors.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken);
    }
}

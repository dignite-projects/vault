using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Index.Chunks;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Index.EntityFrameworkCore;

public class EfCoreChunkRepository : EfCoreRepository<IVaultIndexDbContext, Chunk, Guid>, IChunkRepository
{
    public EfCoreChunkRepository(IDbContextProvider<IVaultIndexDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task UpdateDocumentFiltersAsync(
        Guid documentId,
        Guid documentTypeId,
        Guid? ownerId,
        bool isHidden,
        CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate goes through the DbSet, so the tenant filter still narrows it.
        await (await GetDbSetAsync())
            .Where(c => c.DocumentId == documentId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.DocumentTypeId, documentTypeId)
                    .SetProperty(c => c.OwnerId, ownerId)
                    .SetProperty(c => c.IsHidden, isHidden),
                GetCancellationToken(cancellationToken));
    }

    public virtual async Task<List<string>> GetDisabledContentHashesAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .Where(c => c.DocumentId == documentId && c.IsDisabled)
            .Select(c => c.ContentHash)
            .Distinct()
            .ToListAsync(GetCancellationToken(cancellationToken));
    }
}

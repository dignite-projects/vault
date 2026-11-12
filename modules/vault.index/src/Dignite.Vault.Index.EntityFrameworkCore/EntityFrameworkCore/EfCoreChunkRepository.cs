using System;
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
        CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate goes through the DbSet, so the tenant filter still narrows it.
        await (await GetDbSetAsync())
            .Where(c => c.DocumentId == documentId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.DocumentTypeId, documentTypeId).SetProperty(c => c.OwnerId, ownerId),
                GetCancellationToken(cancellationToken));
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Index.Chunks;

public interface IChunkRepository : IRepository<Chunk, Guid>
{
    /// <summary>Re-copies the read-scope facts onto every chunk of a document, in one statement.</summary>
    Task UpdateDocumentFiltersAsync(Guid documentId, Guid documentTypeId, Guid? ownerId, CancellationToken cancellationToken = default);
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Index.Chunks;

public interface IChunkRepository : IRepository<Chunk, Guid>
{
    /// <summary>Re-copies the read-scope facts onto every chunk of a document, in one statement.</summary>
    Task UpdateDocumentFiltersAsync(Guid documentId, Guid documentTypeId, Guid? ownerId, bool isHidden, CancellationToken cancellationToken = default);

    /// <summary>The content hashes of a document's disabled chunks, which a rebuild carries to the new chunks.</summary>
    Task<List<string>> GetDisabledContentHashesAsync(Guid documentId, CancellationToken cancellationToken = default);
}

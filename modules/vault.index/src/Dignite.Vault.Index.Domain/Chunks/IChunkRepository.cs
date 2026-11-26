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

    /// <summary>
    /// Writes where each chunk sits (its pages, regions and content) and nothing else of the row, when the unit of work
    /// saves. The read-scope facts (<see cref="UpdateDocumentFiltersAsync"/>) and the disable mark are written by
    /// statements of their own that can run after these chunks were loaded; a whole-row update would put the loaded
    /// values back.
    /// </summary>
    Task UpdatePlacementAsync(IEnumerable<Chunk> chunks);

    /// <summary>The content hashes of a document's disabled chunks, which a rebuild carries to the new chunks.</summary>
    Task<List<string>> GetDisabledContentHashesAsync(Guid documentId, CancellationToken cancellationToken = default);
}

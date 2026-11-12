using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// Where the retrieval data that is not plain rows lives: the chunk vectors (and, with the retrieval step, the keyword
/// leg). Its P0 implementation is SQL Server through EF Core, committed in the same transaction as the chunk rows;
/// tests use an in-memory double. The vector and keyword legs are added to this interface by the retrieval step.
/// </summary>
public interface IChunkSearchIndex
{
    /// <summary>The vector dimension the store was created for; <c>null</c> when it accepts any.</summary>
    int? VectorDimensions { get; }

    /// <summary>Replaces a document's vectors in one generation with <paramref name="vectors"/>.</summary>
    Task ReplaceDocumentAsync(Guid documentId, Guid generationId, IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default);

    /// <summary>Re-copies the read-scope facts onto a document's vectors after a reclassification that left its text unchanged.</summary>
    Task UpdateDocumentFiltersAsync(Guid documentId, Guid documentTypeId, Guid? ownerId, CancellationToken cancellationToken = default);

    /// <summary>Removes a document's vectors in every generation.</summary>
    Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);
}

/// <summary>One chunk's vector, with the read-scope facts retrieval filters by.</summary>
public sealed record ChunkVector(Guid ChunkId, Guid DocumentId, Guid DocumentTypeId, Guid? OwnerId, ReadOnlyMemory<float> Vector);

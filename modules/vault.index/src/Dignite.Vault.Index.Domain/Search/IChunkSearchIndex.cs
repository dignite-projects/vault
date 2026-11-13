using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// Where the retrieval data that is not plain rows lives, and the two retrieval legs over it. Its P0 implementation is
/// SQL Server through EF Core: vectors written in the same transaction as the chunk rows, exact vector distance, and
/// full-text ranking. Tests use an in-memory double.
/// <para>
/// Both legs take a <see cref="ChunkSearchScope"/> and apply it through <see cref="ChunkScopeFilter"/> before ranking:
/// a row outside the caller's read scope is never a candidate. They return candidates best first and leave fusion to
/// the caller, so swapping the store does not change how results are ranked.
/// </para>
/// </summary>
public interface IChunkSearchIndex
{
    /// <summary>The vector dimension the store was created for; <c>null</c> when it accepts any.</summary>
    int? VectorDimensions { get; }

    /// <summary>Replaces a document's vectors in one generation with <paramref name="vectors"/>.</summary>
    Task ReplaceDocumentAsync(Guid documentId, Guid generationId, IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-copies the read-scope facts onto a document's vectors: after a reclassification that left its text
    /// unchanged, or when it enters or leaves retrieval (recycle bin, withhold).
    /// </summary>
    Task UpdateDocumentFiltersAsync(Guid documentId, Guid documentTypeId, Guid? ownerId, bool isHidden, CancellationToken cancellationToken = default);

    /// <summary>Copies a chunk's disable mark onto its vectors in every generation.</summary>
    Task SetChunkDisabledAsync(Guid chunkId, bool isDisabled, CancellationToken cancellationToken = default);

    /// <summary>Removes a document's vectors in every generation.</summary>
    Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the ambient tenant's vectors in one generation, a retired one. A store may delete in batches, so call it
    /// outside a transaction: one generation can hold every vector of the tenant.
    /// </summary>
    Task DeleteGenerationAsync(Guid generationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The vector leg: the chunks of <paramref name="generationId"/> within <paramref name="scope"/> nearest to
    /// <paramref name="queryVector"/> by cosine distance, nearest first.
    /// </summary>
    Task<IReadOnlyList<ChunkMatch>> SearchVectorsAsync(
        Guid generationId,
        ReadOnlyMemory<float> queryVector,
        ChunkSearchScope scope,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The keyword leg: the chunks within <paramref name="scope"/> that match <paramref name="query"/> as free text,
    /// best first. Empty, not an error, when the store has no keyword index.
    /// </summary>
    Task<IReadOnlyList<ChunkMatch>> SearchKeywordsAsync(
        string query,
        ChunkSearchScope scope,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What an operator needs to judge the store: the ambient tenant's vectors in <paramref name="generationId"/>, and
    /// whether the keyword leg works and how far behind it is.
    /// </summary>
    Task<ChunkSearchIndexStatus> GetStatusAsync(Guid? generationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The store's state. <see cref="KeywordIndexPendingChanges"/> and <see cref="KeywordIndexPopulating"/> describe the
/// whole keyword index, every tenant's rows; <c>null</c> when the store cannot tell.
/// </summary>
public sealed record ChunkSearchIndexStatus(
    long VectorCount,
    bool KeywordIndexAvailable,
    long? KeywordIndexPendingChanges,
    bool? KeywordIndexPopulating);

/// <summary>One chunk's vector, with the read-scope facts retrieval filters by.</summary>
public sealed record ChunkVector(
    Guid ChunkId,
    Guid DocumentId,
    Guid DocumentTypeId,
    Guid? OwnerId,
    bool IsHidden,
    ReadOnlyMemory<float> Vector,
    bool IsDisabled = false)
    : IChunkScopeFields;

/// <summary>A candidate from one retrieval leg, with that leg's own score (a distance, or a full-text rank).</summary>
public sealed record ChunkMatch(Guid ChunkId, Guid DocumentId, double Score);

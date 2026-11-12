using System;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The two facts about a document that Extract's read rule reads: its type and its owner. Copied onto every Index row
/// that names a document, so the read-scope filter needs no join. <see cref="ChunkScopeFilter"/> is the one place that
/// turns them into a query condition.
/// </summary>
public interface IReadScopeFields
{
    Guid DocumentTypeId { get; }

    Guid? OwnerId { get; }
}

/// <summary>A row retrieval reads: the read-scope facts, plus whether the document is in retrieval at all.</summary>
public interface IChunkScopeFields : IReadScopeFields
{
    Guid DocumentId { get; }

    /// <summary>The document is out of retrieval for everyone: in Extract's recycle bin, or withheld after a reclassification.</summary>
    bool IsHidden { get; }
}

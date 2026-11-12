using System;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The facts about a chunk that decide who may see it, copied from its document onto every row retrieval reads (the
/// chunk itself, and the storage provider's vector rows) so the read-scope filter needs no join.
/// <see cref="ChunkScopeFilter"/> is the one place that turns them into a query condition.
/// </summary>
public interface IChunkScopeFields
{
    Guid DocumentTypeId { get; }

    Guid? OwnerId { get; }

    /// <summary>The document is out of retrieval for everyone: in Extract's recycle bin, or withheld after a reclassification.</summary>
    bool IsHidden { get; }
}

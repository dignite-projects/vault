using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// The smallest unit of retrieval: a piece of one document's Markdown with its heading path. Its own aggregate rather
/// than a child of <see cref="Documents.IndexedDocument"/>, because a document's chunks are replaced as a set, in one
/// transaction, and read many at a time by retrieval; loading them through the document would not fit either.
/// <para>
/// The document's type and owner are repeated on every chunk so the read-scope filter needs no join. The vectors are
/// not here: they belong to an embedding generation and live in the storage provider's own table, so two generations
/// can coexist while a new model is rolled out.
/// </para>
/// </summary>
public class Chunk : BasicAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; protected set; }

    public virtual Guid DocumentId { get; protected set; }

    public virtual Guid DocumentTypeId { get; protected set; }

    public virtual Guid? OwnerId { get; protected set; }

    /// <summary>Position in the document, from 0.</summary>
    public virtual int Ordinal { get; protected set; }

    /// <summary>The headings above this chunk, outermost first; <c>null</c> for text with no heading.</summary>
    public virtual string? HeadingPath { get; protected set; }

    /// <summary>The chunk's text, as Index stores it: what retrieval cites and what <c>read</c> returns.</summary>
    public virtual string Text { get; protected set; } = default!;

    /// <summary>SHA-256 of <see cref="Text"/>, so a rebuild can tell which chunks are unchanged.</summary>
    public virtual string ContentHash { get; protected set; } = default!;

    protected Chunk()
    {
    }

    public Chunk(
        Guid id,
        Guid? tenantId,
        Guid documentId,
        Guid documentTypeId,
        Guid? ownerId,
        int ordinal,
        string? headingPath,
        string text,
        string contentHash)
        : base(id)
    {
        TenantId = tenantId;
        DocumentId = documentId;
        DocumentTypeId = documentTypeId;
        OwnerId = ownerId;
        Ordinal = Check.Range(ordinal, nameof(ordinal), 0, int.MaxValue);
        HeadingPath = Check.Length(headingPath, nameof(headingPath), IndexingConsts.MaxHeadingPathLength);
        Text = Check.NotNullOrEmpty(text, nameof(text));
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash), IndexingConsts.ContentHashLength, IndexingConsts.ContentHashLength);
    }
}

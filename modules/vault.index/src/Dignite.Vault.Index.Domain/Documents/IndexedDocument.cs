using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp.Auditing;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Documents;

/// <summary>
/// One Extract document as Index knows it. Its id is the Extract document id, so there is one row per document and
/// an event finds it without a lookup. Everything here is derived and can be rebuilt from Extract.
/// <para>
/// The type and owner are the two facts Extract's read rule reads about a document; they are copied here and onto
/// the document's chunks so retrieval can filter by the caller's read scope without asking Extract per row.
/// </para>
/// </summary>
public class IndexedDocument : AggregateRoot<Guid>, IMultiTenant, IHasCreationTime, IHasModificationTime
{
    public virtual Guid? TenantId { get; protected set; }

    /// <summary>The confirmed type at the last Ready version Index pulled. Immutable in Extract; re-copied on every pull.</summary>
    public virtual Guid DocumentTypeId { get; protected set; }

    /// <summary>The uploader, the owner arm of Extract's read rule; <c>null</c> for a row created with no user.</summary>
    public virtual Guid? OwnerId { get; protected set; }

    public virtual IndexedDocumentStatus Status { get; protected set; }

    /// <summary>The document is in Extract's recycle bin: kept, but excluded from retrieval until restored.</summary>
    public virtual bool IsArchived { get; protected set; }

    /// <summary>
    /// Withheld from retrieval because Extract reclassified it and its next Ready has not arrived yet; until then
    /// Index only knows the old type, and the old type's grant holders must not keep reading it.
    /// </summary>
    public virtual bool IsWithheld { get; protected set; }

    /// <summary>An event arrived while a job was running for this document, so the job runs again when it ends.</summary>
    public virtual bool RerunRequested { get; protected set; }

    /// <summary>The newest Extract <c>EventTime</c> applied; an older or equal one is a redelivery and is dropped.</summary>
    public virtual DateTime? LastEventTime { get; protected set; }

    /// <summary>SHA-256 of the Markdown the current chunks were built from.</summary>
    public virtual string? ContentHash { get; protected set; }

    /// <summary>The <c>IndexProfile.Version</c> the current chunks were built with.</summary>
    public virtual int? ProfileVersion { get; protected set; }

    public virtual int ChunkCount { get; protected set; }

    public virtual DateTime? LastIndexedTime { get; protected set; }

    public virtual DateTime CreationTime { get; protected set; }

    public virtual DateTime? LastModificationTime { get; set; }

    protected IndexedDocument()
    {
    }

    public IndexedDocument(Guid documentId, Guid? tenantId, Guid documentTypeId, Guid? ownerId)
        : base(documentId)
    {
        TenantId = tenantId;
        DocumentTypeId = documentTypeId;
        OwnerId = ownerId;
        Status = IndexedDocumentStatus.Pending;
    }
}

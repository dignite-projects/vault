using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Artifacts;

/// <summary>
/// Compiled knowledge, produced by a template and kept with what it was built from. P0 has one template, the
/// document's navigation tree; type-level artifacts (catalog, wiki) come later and add a list of source documents.
/// <para>
/// Reading rule: a document-level artifact is readable by whoever reads its document; a type-level one only by
/// whoever reads the whole type (<c>ReadAll</c> or the type's Read grant).
/// </para>
/// </summary>
public class Artifact : AggregateRoot<Guid>, IMultiTenant, IHasCreationTime, IHasModificationTime
{
    public virtual Guid? TenantId { get; protected set; }

    public virtual ArtifactLevel Level { get; protected set; }

    public virtual string TemplateName { get; protected set; } = default!;

    public virtual Guid DocumentTypeId { get; protected set; }

    /// <summary>The source document of a document-level artifact; <c>null</c> for a type-level one.</summary>
    public virtual Guid? DocumentId { get; protected set; }

    /// <summary>The template's output, in the template's own shape (JSON for the navigation tree).</summary>
    public virtual string Content { get; protected set; } = default!;

    /// <summary>Hash of the sources it was built from; when they change, the artifact is stale.</summary>
    public virtual string SourceHash { get; protected set; } = default!;

    public virtual bool IsStale { get; protected set; }

    public virtual DateTime CreationTime { get; protected set; }

    public virtual DateTime? LastModificationTime { get; set; }

    protected Artifact()
    {
    }

    public Artifact(
        Guid id,
        Guid? tenantId,
        ArtifactLevel level,
        string templateName,
        Guid documentTypeId,
        Guid? documentId,
        string content,
        string sourceHash)
        : base(id)
    {
        if (level == ArtifactLevel.Document && documentId is null)
        {
            throw new ArgumentException("A document-level artifact needs its document.", nameof(documentId));
        }

        if (level == ArtifactLevel.DocumentType && documentId is not null)
        {
            throw new ArgumentException("A type-level artifact belongs to no single document.", nameof(documentId));
        }

        TenantId = tenantId;
        Level = level;
        TemplateName = Check.NotNullOrWhiteSpace(templateName, nameof(templateName), IndexingConsts.MaxTemplateNameLength);
        DocumentTypeId = documentTypeId;
        DocumentId = documentId;
        Content = Check.NotNull(content, nameof(content));
        SourceHash = Check.NotNullOrWhiteSpace(sourceHash, nameof(sourceHash), IndexingConsts.ContentHashLength, IndexingConsts.ContentHashLength);
    }
}

using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Artifacts;

/// <summary>
/// Compiled knowledge, produced by a template (<see cref="ArtifactTemplateNames"/>) and kept with a hash of what it was
/// built from. A document-level artifact is fresh while <see cref="SourceHash"/> matches its document's current source,
/// which readers recompute; type-level artifacts (catalog, wiki) come later, add a list of source documents and use
/// <see cref="IsStale"/>.
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

    /// <summary>The template's output, in the template's own shape (JSON for the summaries).</summary>
    public virtual string Content { get; protected set; } = default!;

    /// <summary>Hash of the sources it was built from; when they change, the artifact is stale.</summary>
    public virtual string SourceHash { get; protected set; } = default!;

    public virtual bool IsStale { get; protected set; }

    /// <summary>The LLM input tokens the last compilation took; <c>null</c> when the model reported none.</summary>
    public virtual long? InputTokenCount { get; protected set; }

    public virtual long? OutputTokenCount { get; protected set; }

    /// <summary>What the last compilation left out or could not do, for the operator; <c>null</c> when nothing.</summary>
    public virtual string? Note { get; protected set; }

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
        SourceHash = CheckSourceHash(sourceHash);
    }

    /// <summary>Replaces the output with a new compilation from <paramref name="sourceHash"/>; the artifact is fresh again.</summary>
    public virtual void Recompile(string content, string sourceHash, long? inputTokenCount, long? outputTokenCount, string? note)
    {
        Content = Check.NotNull(content, nameof(content));
        SourceHash = CheckSourceHash(sourceHash);
        IsStale = false;
        InputTokenCount = inputTokenCount;
        OutputTokenCount = outputTokenCount;
        Note = note is null ? null : note.Length <= IndexingConsts.MaxArtifactNoteLength ? note : note[..IndexingConsts.MaxArtifactNoteLength];
    }

    /// <summary>Moves a document-level artifact with its document when Extract reclassifies it.</summary>
    public virtual void SetDocumentType(Guid documentTypeId)
    {
        DocumentTypeId = documentTypeId;
    }

    private static string CheckSourceHash(string sourceHash)
        => Check.NotNullOrWhiteSpace(sourceHash, nameof(sourceHash), IndexingConsts.ContentHashLength, IndexingConsts.ContentHashLength);
}

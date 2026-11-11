using System;
using System.Collections.Generic;
using System.Text.Json;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents.Integration;

/// <summary>
/// A Ready document as <see cref="IDocumentIntegrationService.FindReadyAsync"/> returns it: what a module that
/// derives data from it needs, and nothing about its pipeline or review state, which a Ready document has settled.
/// </summary>
public class DocumentIntegrationDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The confirmed type's id. Always set: a document without one is never Ready for consumers. Key derived data by
    /// this id, not by the type code, which an administrator may rename.
    /// </summary>
    public Guid DocumentTypeId { get; set; }

    /// <summary>
    /// The user who uploaded the document: the owner the read rule's owner arm compares with
    /// <see cref="DocumentReadScopeDto.OwnerId"/>. <c>null</c> for a row created with no user behind it.
    /// </summary>
    public Guid? OwnerId { get; set; }

    public string? Title { get; set; }

    /// <summary>Document language (ISO 639-1 / IETF tag); <c>null</c> when it was not detected.</summary>
    public string? Language { get; set; }

    /// <summary>The document's text, whole: the one text payload of the channel.</summary>
    public string Markdown { get; set; } = default!;

    /// <summary>
    /// Type-bound field values keyed by field name, in the same wire shape as <c>DocumentDto.ExtractedFields</c>;
    /// <c>null</c> when the document has none.
    /// </summary>
    public Dictionary<string, JsonElement>? ExtractedFields { get; set; }
}

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// A document the caller processed itself: its text as Markdown and, when the caller also classified it and
/// extracted its fields, its type and field values. Each stage the caller supplies is completed with what it supplied
/// instead of running on the server; a stage it leaves out runs as for an uploaded file.
/// </summary>
public class SubmitDocumentInput
{
    /// <summary>
    /// The document's text, as Markdown. Pages may be marked the way Azure Document Intelligence marks them
    /// (<c>&lt;!-- PageBreak --&gt;</c> between two pages; <c>PageHeader</c> / <c>PageFooter</c> / <c>PageNumber</c>
    /// comments for running headers, footers and page numbers): the pages then reach the document's layout, and the
    /// markers do not reach its text. At most <c>DocumentConsts.MaxSubmittedMarkdownLength</c> characters.
    /// </summary>
    [Required]
    public string Markdown { get; set; } = default!;

    /// <summary>The title; the first heading of the Markdown when left out.</summary>
    public string? Title { get; set; }

    /// <summary>The text's language, as a BCP 47 tag such as <c>ja</c> or <c>en-US</c>; ignored when not one.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// The type the caller classified the document as, by its code in the caller's layer. Classification then runs
    /// no model call, as for an upload that declares its type. Left out, the server classifies the text. Judged like
    /// an upload's declared type: <c>Documents.Upload</c>, or an Upload grant on this type.
    /// </summary>
    public string? DocumentTypeCode { get; set; }

    /// <summary>
    /// The field values the caller extracted, keyed by field name, in the shape each field's type reads (the same
    /// values the operator's field edit takes). Present, even empty, field extraction runs no model call and these
    /// are the document's values; left out (<c>null</c>), the server extracts them. Needs
    /// <see cref="DocumentTypeCode"/>.
    /// </summary>
    public Dictionary<string, JsonElement>? Fields { get; set; }

    /// <summary>
    /// Where each submitted value appears in the document: per field name, up to three short passages copied
    /// character for character from <see cref="Markdown"/>. A passage found once in the stored text becomes the
    /// value's source; one not found, or found more than once, is dropped, as is a passage for a field with no value.
    /// </summary>
    public Dictionary<string, List<string>>? FieldSources { get; set; }

    /// <summary>Optional cabinet, validated as on upload.</summary>
    public Guid? CabinetId { get; set; }
}

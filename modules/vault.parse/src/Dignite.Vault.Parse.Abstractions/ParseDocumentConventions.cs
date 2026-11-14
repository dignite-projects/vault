namespace Dignite.Vault.Parse;

/// <summary>
/// The roles Vault Parse gives the elements of an <see cref="Microsoft.Extensions.Documents.ExtractionDocument"/>. The
/// first three are the shared ones the Documents abstractions name; the rest are Vault's, for what the shared model has
/// no element of its own. A consumer that does not know a role reads the element as ordinary text.
/// </summary>
public static class ParseElementRoles
{
    /// <summary>A heading; its level is <c>ExtractionDocumentTextElement.HeadingLevel</c>.</summary>
    public const string Heading = "heading";

    /// <summary>Text repeated at the top of every page. Kept in the tree, never rendered into Markdown.</summary>
    public const string PageHeader = "pageHeader";

    /// <summary>Text repeated at the foot of every page, page numbers included. Kept in the tree, never rendered.</summary>
    public const string PageFooter = "pageFooter";

    /// <summary>One item of a list: its nesting and marker are in <see cref="ParseContentProperties"/>.</summary>
    public const string ListItem = "listItem";

    /// <summary>Preformatted text, rendered as a fenced block.</summary>
    public const string Code = "code";

    /// <summary>A quotation, rendered with <c>&gt;</c> before each line.</summary>
    public const string Quote = "quote";

    /// <summary>A block kept as the Markdown it was read from (raw HTML, a rule, anything the tree does not model).</summary>
    public const string Markdown = "markdown";

    /// <summary>The role of a table cell that heads its column.</summary>
    public const string ColumnHeader = "columnHeader";
}

/// <summary>Keys in a text element's <c>TextContent.AdditionalProperties</c>.</summary>
public static class ParseContentProperties
{
    /// <summary>A list item's nesting depth, 0 for a top-level item (<see cref="int"/>).</summary>
    public const string ListLevel = "vault.listLevel";

    /// <summary>A list item's marker as written: <c>-</c>, <c>*</c>, <c>1.</c>, <c>2)</c> (<see cref="string"/>).</summary>
    public const string ListMarker = "vault.listMarker";

    /// <summary>A code block's language tag (<see cref="string"/>).</summary>
    public const string CodeLanguage = "vault.codeLanguage";
}

/// <summary>Keys in <c>ExtractionDocument.Metadata</c>.</summary>
public static class ParseDocumentMetadata
{
    /// <summary>The engine that produced the document, such as <c>PdfPig</c> (<see cref="string"/>).</summary>
    public const string Provider = "vault.provider";

    /// <summary>The detected language, as a BCP 47 tag (<see cref="string"/>).</summary>
    public const string Language = "vault.language";

    /// <summary>
    /// Why the document is known to be incomplete: content was dropped, cut or could not be read (<see cref="string"/>).
    /// Absent when the engine believes it captured everything.
    /// </summary>
    public const string IncompleteReason = "vault.incompleteReason";
}

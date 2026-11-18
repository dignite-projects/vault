using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents.Integration;

/// <summary>The ranges of a document's Markdown read from one page of its file.</summary>
public class DocumentPageSpansDto
{
    /// <summary>The page number, from 1.</summary>
    public int PageNumber { get; set; }

    /// <summary>In Markdown order. A table read across two pages is in both pages' ranges.</summary>
    public List<DocumentTextSpanDto> Spans { get; set; } = new();
}

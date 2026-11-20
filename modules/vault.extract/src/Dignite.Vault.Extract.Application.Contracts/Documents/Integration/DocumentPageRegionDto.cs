namespace Dignite.Vault.Extract.Documents.Integration;

/// <summary>
/// Where one element of a document's Markdown (a paragraph, heading, list item, table cell or figure) was read on a page
/// of its file: the Markdown range and a box on the page. The box's origin is the page's top-left corner and each value
/// is a fraction of the page's width (<see cref="Left"/>, <see cref="Width"/>) or height (<see cref="Top"/>,
/// <see cref="Height"/>), so it is drawn on the page at whatever size the page is rendered.
/// </summary>
public class DocumentPageRegionDto
{
    /// <summary>Where the element starts in the Markdown.</summary>
    public int Offset { get; set; }

    public int Length { get; set; }

    /// <summary>The page number, from 1.</summary>
    public int PageNumber { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

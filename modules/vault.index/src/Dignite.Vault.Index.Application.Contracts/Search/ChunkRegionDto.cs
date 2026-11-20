namespace Dignite.Vault.Index.Search;

/// <summary>
/// A box on a page of the document's file that a chunk's text was read from. Its origin is the page's top-left corner
/// and each value is a fraction of the page's width (<see cref="Left"/>, <see cref="Width"/>) or height
/// (<see cref="Top"/>, <see cref="Height"/>): draw it as <c>left * pageWidth</c>, <c>top * pageHeight</c> on the page at
/// whatever size it is rendered.
/// </summary>
public class ChunkRegionDto
{
    /// <summary>The page number, from 1.</summary>
    public int Page { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

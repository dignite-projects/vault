namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// A box on a page of the document's file that a chunk's text was read from. Its origin is the page's top-left corner and
/// each value is a fraction of the page's width (<see cref="Left"/>, <see cref="Width"/>) or height (<see cref="Top"/>,
/// <see cref="Height"/>), as Extract hands it out (<c>DocumentIntegrationDto.Regions</c>).
/// </summary>
public record ChunkRegion(int Page, double Left, double Top, double Width, double Height);

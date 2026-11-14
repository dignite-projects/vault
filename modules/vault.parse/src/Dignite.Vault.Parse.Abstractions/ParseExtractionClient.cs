using System;
using System.IO;
using Microsoft.Extensions.DocumentExtraction;

namespace Dignite.Vault.Parse;

/// <summary>
/// An extraction engine Parse can route a file to: a <see cref="DocumentExtractionClient"/> that says which files it
/// reads. Engines coexist; for each file Parse's router picks the highest <see cref="Priority"/> among those that
/// <see cref="CanExtract"/> it. A consumer does not see engines: it asks the router, registered as
/// <see cref="DocumentExtractionClient"/>.
/// <para>
/// An engine returns the document it read: sections (one per page when it knows pages, each referring to its page),
/// headings with levels, paragraphs, list items, tables and images, with the roles and properties of
/// <see cref="ParseElementRoles"/> and <see cref="ParseContentProperties"/>, and in its metadata
/// (<see cref="ParseDocumentMetadata"/>) its name, the detected language and, when content is known to be missing,
/// why. A file it reads but finds nothing in gives a document with no elements, never an exception.
/// </para>
/// </summary>
public abstract class ParseExtractionClient : DocumentExtractionClient
{
    /// <summary>The engine's name, as written to <see cref="ParseDocumentMetadata.Provider"/>.</summary>
    public abstract string ProviderName { get; }

    /// <summary>Selection order when more than one engine reads a file; the highest wins.</summary>
    public virtual int Priority => ParseExtractionPriorities.Default;

    /// <param name="fileExtension">The file's extension with its dot, lower case, such as <c>.pdf</c>; empty when unknown.</param>
    /// <param name="mediaType">The file's media type, such as <c>application/pdf</c>.</param>
    public abstract bool CanExtract(string fileExtension, string mediaType);
}

/// <summary>Well-known <see cref="ParseExtractionClient.Priority"/> values.</summary>
public static class ParseExtractionPriorities
{
    /// <summary>The catch-all engine (MarkItDown): below every engine that owns a format.</summary>
    public const int Fallback = int.MinValue;

    /// <summary>An engine that owns its formats (PdfPig for PDF, OpenXML for DOCX and PPTX).</summary>
    public const int Default = 0;

    /// <summary>An OCR engine, which reads a PDF only when no engine with a text layer to read does.</summary>
    public const int Ocr = -1000;
}

/// <summary>
/// An OCR engine: it reads images, and PDFs page by page as images. The host enables exactly one. Parse's router
/// sends it images, and a PDF whose text-layer engine found no text; the PDF and OpenXML engines send it the images
/// embedded in a document.
/// </summary>
public abstract class ParseOcrClient : ParseExtractionClient
{
    public override int Priority => ParseExtractionPriorities.Ocr;

    public override bool CanExtract(string fileExtension, string mediaType)
        => IsImage(fileExtension, mediaType)
            || fileExtension == ".pdf"
            || string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsImage(string fileExtension, string mediaType)
        => fileExtension is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" or ".webp" or ".gif"
            || (fileExtension.Length == 0 && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using NSubstitute;

namespace Dignite.Vault.Parse;

/// <summary>
/// What a test reads off an extracted document: the stored Markdown (rendered once) and the Vault metadata.
/// </summary>
internal sealed class ParseResult
{
    public ParseResult(ExtractionDocument document)
    {
        Document = document;
        Markdown = ExtractionDocumentMarkdown.Render(document);
    }

    public ExtractionDocument Document { get; }

    public string Markdown { get; }

    public string? ProviderName => Document.GetProvider();

    public string? DetectedLanguage => Document.GetLanguage();

    public bool IsComplete => Document.IsComplete();

    public string? IncompleteReason => Document.GetIncompleteReason();
}

/// <summary>
/// Shared by the Parse test projects (linked into each): one call that extracts a file the way Extract does, and a
/// substitute OCR engine.
/// </summary>
internal static class ParseTestKit
{
    /// <summary>Extracts <paramref name="stream"/> as the file <paramref name="fileName"/>, the way Extract's parse job does.</summary>
    public static async Task<ParseResult> ParseAsync(
        this DocumentExtractionClient client,
        Stream stream,
        string fileName,
        string mediaType,
        IEnumerable<string>? languageHints = null,
        CancellationToken cancellationToken = default)
        => new(await client.ExtractAsync(
            stream, fileName, mediaType, ParseExtractionOptions.Create(fileName, languageHints), cancellationToken));

    /// <summary>
    /// An OCR engine substitute answering every call with <paramref name="markdown"/> read into a document. A partial
    /// substitute, so <see cref="ParseOcrClient.CanExtract"/> and <see cref="ParseExtractionClient.Priority"/> keep
    /// their real behavior; record calls with <c>Received()</c> on <c>ExtractAsync</c>.
    /// </summary>
    public static ParseOcrClient Ocr(
        string markdown = "ocr markdown",
        string provider = "FakeOcr",
        string? incompleteReason = null,
        string? language = null)
    {
        var ocr = Substitute.ForPartsOf<ParseOcrClient>();
        ocr.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => OcrDocument(call.ArgAt<string>(1), markdown, provider, incompleteReason, language));
        return ocr;
    }

    /// <summary>The document an OCR engine returns for <paramref name="markdown"/>.</summary>
    public static ExtractionDocument OcrDocument(
        string identifier,
        string markdown,
        string provider = "FakeOcr",
        string? incompleteReason = null,
        string? language = null)
    {
        var document = MarkdownDocumentReader.Read(markdown, identifier).SetProvider(provider).SetLanguage(language);
        return incompleteReason is null ? document : document.MarkIncomplete(incompleteReason);
    }

    /// <summary>Whether <paramref name="options"/> carries exactly <paramref name="hints"/>, in order.</summary>
    public static bool HasLanguageHints(DocumentExtractionOptions? options, params string[] hints)
        => options.GetLanguageHints().SequenceEqual(hints);
}

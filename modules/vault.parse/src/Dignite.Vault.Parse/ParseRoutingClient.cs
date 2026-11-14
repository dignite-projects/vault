using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse;

/// <summary>
/// Parse as one <see cref="DocumentExtractionClient"/>: it sends each file to the engine that reads it and returns that
/// engine's document. Consumers inject <see cref="DocumentExtractionClient"/> and get this.
/// <para>
/// The engine is the highest <see cref="ParseExtractionClient.Priority"/> among those that
/// <see cref="ParseExtractionClient.CanExtract"/> the file, judged by the extension of
/// <see cref="ParseExtractionOptions.FileName"/> and the media type. Images go to the OCR engine. A PDF whose engine
/// finds no text (a scan, an image-only PDF) goes to the OCR engine as well: the PDF engine does not OCR whole pages
/// itself, so nothing is read twice. The document always names its engine in <see cref="ParseDocumentMetadata.Provider"/>.
/// </para>
/// </summary>
[ExposeServices(typeof(DocumentExtractionClient), typeof(ParseRoutingClient))]
public class ParseRoutingClient : DocumentExtractionClient, ITransientDependency
{
    private readonly IReadOnlyList<ParseExtractionClient> _engines;

    public ParseRoutingClient(IEnumerable<ParseExtractionClient> engines)
    {
        _engines = engines.ToList();
    }

    public ILogger<ParseRoutingClient> Logger { get; set; } = NullLogger<ParseRoutingClient>.Instance;

    public override async Task<ExtractionDocument> ExtractAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = options?.Clone();
        var extension = options.GetFileExtension();
        var engine = Select(extension, mediaType);

        // One seekable copy for the engine and a possible OCR fallback, both of which read it: a blob stream may not seek,
        // and PdfPig and the OpenXML SDK need to. A very large file stays in memory; a temporary file would lift that.
        using var seekable = new MemoryStream();
        await document.CopyToAsync(seekable, cancellationToken);
        seekable.Position = 0;

        var extracted = await engine.ExtractAsync(seekable, identifier, mediaType, options, cancellationToken);
        if (engine is not ParseOcrClient && IsPdf(extension, mediaType) && !HasText(extracted) && Ocr() is { } ocr)
        {
            Logger.LogDebug("{Engine} found no text in the PDF; reading it with {Ocr}.", engine.ProviderName, ocr.ProviderName);
            seekable.Position = 0;
            engine = ocr;
            extracted = await ocr.ExtractAsync(seekable, identifier, mediaType, options, cancellationToken);
        }

        if (extracted.GetProvider() is null)
        {
            extracted.SetProvider(engine.ProviderName);
        }

        return extracted;
    }

    /// <summary>The engine for a file: the highest priority among those that read it.</summary>
    protected virtual ParseExtractionClient Select(string extension, string mediaType)
    {
        ParseExtractionClient? selected = null;
        foreach (var engine in _engines.Where(engine => engine.CanExtract(extension, mediaType)))
        {
            if (selected is null || engine.Priority > selected.Priority)
            {
                selected = engine;
            }
            else if (engine.Priority == selected.Priority)
            {
                // Two engines claim the same file at the same priority: a composition mistake. Keep the first, loudly.
                Logger.LogWarning(
                    "Engines {Incumbent} and {Candidate} both read '{Extension}' ({MediaType}) at priority {Priority}; using {Incumbent}.",
                    selected.ProviderName, engine.ProviderName, extension, mediaType, engine.Priority, selected.ProviderName);
            }
        }

        // Cannot happen while the catch-all engine (MarkItDown) is installed: a composition error, not a document's.
        return selected ?? throw new InvalidOperationException(
            $"No Parse engine reads '{extension}' ({mediaType}). Install the catch-all engine module (ElBruno MarkItDown) in the host.");
    }

    private ParseOcrClient? Ocr() => _engines.OfType<ParseOcrClient>().OrderByDescending(engine => engine.Priority).FirstOrDefault();

    private static bool IsPdf(string extension, string mediaType)
        => extension == ".pdf" || string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the document holds a letter or a digit anywhere: an empty or whitespace-only read is no text.</summary>
    protected static bool HasText(ExtractionDocument document)
        => document.EnumerateContent().Any(element => element switch
        {
            ExtractionDocumentTextElement { Content: TextContent text } => text.Text.Any(char.IsLetterOrDigit),
            ExtractionDocumentImageElement image => image.Text?.Any(char.IsLetterOrDigit) == true,
            ExtractionDocumentTableElement table => table.Cells.Any(cell => cell?.Content is ExtractionDocumentTextElement { Content: TextContent cellText } && cellText.Text.Any(char.IsLetterOrDigit)),
            _ => false
        });
}

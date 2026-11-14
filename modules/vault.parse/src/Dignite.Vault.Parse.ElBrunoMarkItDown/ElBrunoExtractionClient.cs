using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ElBruno.MarkItDotNet;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse.ElBrunoMarkItDown;

/// <summary>
/// The catch-all engine: ElBruno MarkItDotNet converts what no other engine owns (XLSX, CSV, TSV, TXT and more) to
/// Markdown, which <see cref="MarkdownDocumentReader"/> reads into the document. It has no notion of pages.
/// </summary>
[ExposeServices(typeof(ParseExtractionClient), typeof(ElBrunoExtractionClient))]
public class ElBrunoExtractionClient : ParseExtractionClient, ITransientDependency
{
    public const string ProviderIdentifier = "ElBruno.MarkItDotNet";

    private readonly MarkdownService _markdownService;

    public ILogger<ElBrunoExtractionClient> Logger { get; set; } = NullLogger<ElBrunoExtractionClient>.Instance;

    public ElBrunoExtractionClient(MarkdownService markdownService)
    {
        _markdownService = markdownService;
    }

    public override string ProviderName => ProviderIdentifier;

    public override int Priority => ParseExtractionPriorities.Fallback;

    /// <summary>Catch-all: reads every file, so a format no engine claims degrades here.</summary>
    public override bool CanExtract(string fileExtension, string mediaType) => true;

    public override async Task<ExtractionDocument> ExtractAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var extension = options.GetFileExtension();
        var isXlsx = extension == ".xlsx";
        if (isXlsx)
        {
            await XlsxSafetyGuard.ValidatePackageAsync(document, cancellationToken: cancellationToken);
        }

        // ElBruno's text converters decode a raw byte stream as UTF-8 with BOM sniffing only, so a
        // legacy-encoded CSV/TSV/TXT would land in Document.Markdown as U+FFFD (#493). Hand them UTF-8.
        await using var normalized = TextEncodingNormalizer.AppliesTo(extension)
            ? await TextEncodingNormalizer.ToUtf8Async(document, extension, Logger, cancellationToken)
            : (Stream?)null;

        var conversion = await _markdownService.ConvertAsync(normalized ?? document, extension, cancellationToken);
        if (!conversion.Success)
        {
            Logger.LogWarning("ElBruno conversion failed for {Extension}: {Error}", extension, conversion.ErrorMessage);

            // XLSX is an explicitly supported upload format (#471), not an opportunistic catch-all format.
            // A corrupt/encrypted workbook must fail the parse run instead of being persisted as a successful
            // document with empty Markdown. PDF keeps its empty result because the router uses that signal to
            // read a scanned PDF with OCR.
            if (isXlsx)
            {
                throw new InvalidDataException(
                    $"Could not convert XLSX to Markdown: {conversion.ErrorMessage ?? "unknown conversion error"}");
            }

            // An empty document from a failed conversion is not a complete read of an empty file (#268), the stance the
            // OpenXML engines take for a file they cannot open. The reason is a fixed string: the converter's own error
            // message comes from a third-party library and must not travel on the egress.
            return new ExtractionDocument(identifier)
                .SetProvider(ProviderIdentifier)
                .MarkIncomplete("The file could not be converted (corrupt or unsupported format).");
        }

        var markdown = conversion.Markdown ?? string.Empty;
        if (isXlsx)
        {
            XlsxSafetyGuard.ValidateMarkdown(markdown);
        }

        return MarkdownDocumentReader.Read(markdown, identifier).SetProvider(ProviderIdentifier);
    }
}

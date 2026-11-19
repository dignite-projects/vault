using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse;

/// <summary>
/// Reads a Markdown file (<c>.md</c>, <c>.markdown</c>, <c>text/markdown</c>) into the document tree with
/// <see cref="MarkdownDocumentReader"/>: Markdown a client produced from a document itself, which Vault keeps as the
/// document's text.
/// <para>
/// Pages come from <see cref="PageMarkup"/>. Markdown that marks page breaks gives one section per page, each
/// element referring to its page, the same tree a vision-LLM OCR engine builds from its per-page transcriptions; the
/// pages' running headers and footers stay in the tree and out of the rendered Markdown. Markdown that marks none
/// gives one section with no page, as from an engine with no notion of pages.
/// </para>
/// <para>
/// It lives beside the router rather than in an engine package of its own: it needs nothing but the reader the router
/// project already holds, and a host that routes at all can then always read Markdown.
/// </para>
/// </summary>
[ExposeServices(typeof(ParseExtractionClient), typeof(MarkdownExtractionClient))]
public class MarkdownExtractionClient : ParseExtractionClient, ITransientDependency
{
    public const string ProviderIdentifier = "Markdown";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public override string ProviderName => ProviderIdentifier;

    public override bool CanExtract(string fileExtension, string mediaType)
        => fileExtension is ".md" or ".markdown"
            || string.Equals(mediaType, "text/markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "text/x-markdown", StringComparison.OrdinalIgnoreCase);

    public override async Task<ExtractionDocument> ExtractAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var markdown = await ReadUtf8Async(document, cancellationToken);
        var extracted = new ExtractionDocument(identifier).SetProvider(ProviderIdentifier);
        var paged = PageMarkup.HasPageBreak(markdown);

        foreach (var page in PageMarkup.Split(markdown))
        {
            ExtractionDocumentSourcePage? source = null;
            var section = new ExtractionDocumentSection();
            if (paged)
            {
                source = new ExtractionDocumentSourcePage(
                    "page-" + page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), page.Number);
                extracted.SourcePages.Add(source);
                section.Sources.Add(new ExtractionDocumentSourceReference(source));
            }

            extracted.Sections.Add(section);
            AddFurniture(section, page.Headers, ParseElementRoles.PageHeader, source);
            MarkdownDocumentReader.ReadInto(section, page.Text, source);
            AddFurniture(section, page.Footers, ParseElementRoles.PageFooter, source);
        }

        return extracted;
    }

    /// <summary>
    /// The file as UTF-8 text, a byte order mark skipped. Markdown has no other encoding to sniff, so a file that is not
    /// UTF-8 is refused with <see cref="InvalidDataException"/> (a permanent failure) rather than stored as U+FFFD.
    /// </summary>
    protected virtual async Task<string> ReadUtf8Async(Stream document, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The Markdown file is not UTF-8 text.", exception);
        }
    }

    private static void AddFurniture(
        ExtractionDocumentSection section,
        System.Collections.Generic.IReadOnlyList<string> lines,
        string role,
        ExtractionDocumentSourcePage? source)
    {
        foreach (var line in lines)
        {
            var element = new ExtractionDocumentTextElement(new TextContent(line), role);
            if (source is not null)
            {
                element.Sources.Add(new ExtractionDocumentSourceReference(source));
            }

            section.Elements.Add(element);
        }
    }
}

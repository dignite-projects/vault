using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse.Ocr.AzureDocumentIntelligence;

[ExposeServices(typeof(ParseOcrClient), typeof(ParseExtractionClient), typeof(AzureDocumentIntelligenceOcrClient))]
public class AzureDocumentIntelligenceOcrClient : ParseOcrClient, ITransientDependency
{
    // The channel layer always uses prebuilt-layout because it outputs structured Markdown with
    // headings / tables and matches Markdown-first. This is intentionally not exposed as host
    // configuration: prebuilt-read outputs only plain text and would break Markdown-first, while
    // business prebuilts such as invoice / contract belong to downstream business modules. Neither is
    // a channel-layer OCR option.
    private const string ModelId = "prebuilt-layout";

    private readonly AzureDocumentIntelligenceOptions _options;

    public AzureDocumentIntelligenceOcrClient(IOptions<AzureDocumentIntelligenceOptions> options)
    {
        _options = options.Value;
    }

    public override string ProviderName => "AzureDocumentIntelligence";

    public override async Task<ExtractionDocument> ExtractAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var analyzeResult = await AnalyzeAsync(document, ModelId, cancellationToken);

        // Read from the Markdown the service writes. Its layout (paragraph roles, regions, table cells) is in the
        // AnalyzeResult, kept as the document's in-memory raw representation.
        var extracted = MarkdownDocumentReader.Read(BuildMarkdown(analyzeResult), identifier)
            .SetProvider(ProviderName)
            .SetLanguage(analyzeResult.Languages?.FirstOrDefault()?.Locale);
        extracted.RawRepresentation = analyzeResult;
        return extracted;
    }

    // Test seam: lets a stubbed DocumentIntelligenceClient (e.g. one configured with a fake
    // HttpPipelineTransport) be substituted so the provider can be exercised without contacting Azure.
    // Production builds the real client from the configured endpoint + key. Kept protected virtual per the
    // module-extensibility convention; it does not change the engine's contract or any egress boundary.
    protected virtual DocumentIntelligenceClient CreateClient()
    {
        return new DocumentIntelligenceClient(
            new Uri(_options.Endpoint),
            new AzureKeyCredential(_options.ApiKey));
    }

    private async Task<AnalyzeResult> AnalyzeAsync(
        Stream fileStream,
        string modelId,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();

        BinaryData binaryData;
        using (var ms = new MemoryStream())
        {
            await fileStream.CopyToAsync(ms, cancellationToken);
            binaryData = BinaryData.FromBytes(ms.ToArray());
        }

        var analyzeOptions = new AnalyzeDocumentOptions(modelId, binaryData)
        {
            // Markdown-first execution point: Azure DI OutputContentFormat defaults to Text, so
            // Markdown must be explicitly requested to get structured Content with headings / tables /
            // lists. Requires api-version 2024-11-30+ and SDK 1.0+. Do not remove this; removing it
            // makes prebuilt-layout degrade to a plain text stream and breaks Markdown-first.
            OutputContentFormat = DocumentContentFormat.Markdown
        };

        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions, cancellationToken);
        return operation.Value;
    }

    private static string BuildMarkdown(AnalyzeResult analyzeResult)
    {
        // analyzeResult.Content is already Markdown. If Azure returns empty content, fall back to line
        // text joined into flat Markdown paragraphs.
        var markdown = analyzeResult.Content;
        if (string.IsNullOrEmpty(markdown))
        {
            var paragraphs = (analyzeResult.Pages ?? [])
                .SelectMany(p => p.Lines ?? [])
                .Select(l => l.Content)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            markdown = string.Join("\n\n", paragraphs);
        }

        return markdown ?? string.Empty;
    }
}

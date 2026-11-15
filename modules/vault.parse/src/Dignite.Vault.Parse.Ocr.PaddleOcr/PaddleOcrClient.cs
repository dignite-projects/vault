using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse.Ocr.PaddleOcr;

[ExposeServices(typeof(ParseOcrClient), typeof(ParseExtractionClient), typeof(PaddleOcrClient))]
public class PaddleOcrClient : ParseOcrClient, ITransientDependency
{
    private readonly PaddleOcrOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public PaddleOcrClient(
        IOptions<PaddleOcrOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    public override string ProviderName => "PaddleOCR";

    public override async Task<ExtractionDocument> ExtractAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var modelName = _options.ModelName;
        var rawJson = await SendAsync(
            document,
            options.GetLanguageHints(),
            mediaType,
            modelName,
            cancellationToken);
        var result = JsonSerializer.Deserialize<PaddleOcrResponse>(rawJson)
            ?? throw new InvalidOperationException("PaddleOCR server returned an empty response.");

        // The sidecar answers in Markdown (or plain text, wrapped into paragraphs) with no usable regions: PP-StructureV3
        // and PaddleOCR-VL report page-level output only (see the sidecar's server.py). Its answer as it came is the
        // document's raw representation.
        var extracted = MarkdownDocumentReader.Read(BuildMarkdown(result), identifier)
            .SetProvider(result.ProviderName ?? ProviderName)
            .SetLanguage(result.DetectedLanguage);
        extracted.RawRepresentation = rawJson;
        return extracted;
    }

    private async Task<string> SendAsync(
        Stream fileStream,
        IReadOnlyList<string> languageHints,
        string contentType,
        string modelName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> languages = languageHints.Count > 0
            ? languageHints
            : _options.Languages.ToList();

        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, cancellationToken);
        var fileBytes = ms.ToArray();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        if (!string.IsNullOrEmpty(contentType))
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", "document");
        content.Add(new StringContent(string.Join(",", languages)), "languages");
        content.Add(new StringContent(modelName), "model_name");

        var client = _httpClientFactory.CreateClient(VaultParsePaddleOcrModule.HttpClientName);
        using var response = await client.PostAsync($"{_options.Endpoint.TrimEnd('/')}/ocr", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"PaddleOCR server returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string BuildMarkdown(PaddleOcrResponse result)
    {
        // Markdown-first: PP-StructureV3 / PaddleOCR-VL modes return structured Markdown. Modes such
        // as PP-OCRv4 that return only raw_text are wrapped by the Provider into flat Markdown
        // paragraphs, keeping plain-text-to-Markdown translation inside the engine.
        return !string.IsNullOrEmpty(result.Markdown)
            ? result.Markdown
            : WrapParagraphs(result.RawText);
    }

    private static string WrapParagraphs(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        // Convert newline-separated plain-text paragraphs into flat Markdown paragraphs separated by
        // blank lines. This is the Provider-side minimum implementation of the Markdown-first
        // contract.
        var paragraphs = rawText
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n\n", paragraphs);
    }

    private sealed class PaddleOcrResponse
    {
        /// <summary>Plain-text output. When the sidecar mode provides only raw_text, the Provider wraps it into flat Markdown.</summary>
        [JsonPropertyName("raw_text")]
        public string RawText { get; set; } = string.Empty;

        /// <summary>Populated by PP-StructureV3 / PaddleOCR-VL models; null in PP-OCRv4 mode.</summary>
        [JsonPropertyName("markdown")]
        public string? Markdown { get; set; }

        [JsonPropertyName("detected_language")]
        public string? DetectedLanguage { get; set; }

        [JsonPropertyName("page_count")]
        public int PageCount { get; set; }

        [JsonPropertyName("provider_name")]
        public string? ProviderName { get; set; }

        [JsonPropertyName("provider_model")]
        public string? ProviderModelName { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Dignite.Vault.Index.Reranking;

/// <summary>
/// <see cref="IReranker"/> over the rerank API that Cohere introduced and Jina, SiliconFlow and vLLM serve in the same
/// shape: <c>POST {endpoint}/rerank</c> with <c>model</c>, <c>query</c> and <c>documents</c>, answered by
/// <c>results[]</c> of <c>index</c> and <c>relevance_score</c>. Documents are never echoed back
/// (<c>return_documents: false</c>); the response is mapped onto the input order.
/// </summary>
public class HttpReranker : IReranker
{
    private readonly HttpClient _httpClient;
    private readonly HttpRerankerOptions _options;

    public HttpReranker(HttpClient httpClient, IOptions<HttpRerankerOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public virtual string ModelId => _options.ModelId;

    public virtual async Task<RerankResult> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            return new RerankResult([], 0);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint.TrimEnd('/') + "/rerank")
        {
            Content = JsonContent.Create(new RerankRequest(_options.ModelId, query, documents, documents.Count, false))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken)
                   ?? throw new InvalidOperationException("The rerank API returned an empty body.");

        var scores = new double[documents.Count];
        Array.Fill(scores, double.NegativeInfinity);
        foreach (var result in body.Results ?? [])
        {
            if (result.Index >= 0 && result.Index < scores.Length)
            {
                scores[result.Index] = result.RelevanceScore;
            }
        }

        if (scores.Any(double.IsNegativeInfinity))
        {
            throw new InvalidOperationException("The rerank API did not score every document.");
        }

        var tokens = body.Meta?.Tokens?.InputTokens ?? body.Meta?.BilledUnits?.InputTokens ?? body.Usage?.TotalTokens;
        return new RerankResult(scores, tokens);
    }

    private sealed record RerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents,
        [property: JsonPropertyName("top_n")] int TopN,
        [property: JsonPropertyName("return_documents")] bool ReturnDocuments);

    private sealed class RerankResponse
    {
        [JsonPropertyName("results")]
        public List<RerankItem>? Results { get; set; }

        /// <summary>Cohere and SiliconFlow.</summary>
        [JsonPropertyName("meta")]
        public RerankMeta? Meta { get; set; }

        /// <summary>Jina and vLLM.</summary>
        [JsonPropertyName("usage")]
        public RerankUsage? Usage { get; set; }
    }

    private sealed class RerankItem
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("relevance_score")]
        public double RelevanceScore { get; set; }
    }

    private sealed class RerankMeta
    {
        [JsonPropertyName("tokens")]
        public RerankTokens? Tokens { get; set; }

        [JsonPropertyName("billed_units")]
        public RerankTokens? BilledUnits { get; set; }
    }

    private sealed class RerankTokens
    {
        [JsonPropertyName("input_tokens")]
        public long? InputTokens { get; set; }
    }

    private sealed class RerankUsage
    {
        [JsonPropertyName("total_tokens")]
        public long? TotalTokens { get; set; }
    }
}

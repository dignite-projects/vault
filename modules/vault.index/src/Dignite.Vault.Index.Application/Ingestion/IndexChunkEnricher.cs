using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Index.Indexing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Adds keywords and the questions a chunk answers, with one model call per batch of chunks. A DataIngestion chunk
/// processor: it runs on the chunker's output, like DataIngestion's own enrichers, and writes its results to the
/// chunk's metadata under <see cref="KeywordsKey"/> and <see cref="QuestionsKey"/>.
/// <para>
/// Not DataIngestion's <c>KeywordEnricher</c>: it builds its prompt with the chunk text bare, while every document text
/// sent to a model here is wrapped with <see cref="PromptBoundary"/> under a boundary rule (the LLM call rules, A); and
/// one call that returns both kinds costs half of two. Every chunk sent is cut at <see cref="MaxChunkLength"/>: the
/// keywords and questions of a chunk do not need its last lines, and the batch stays bounded.
/// </para>
/// <para>
/// Best effort, as DataIngestion's enrichers are: a batch whose call fails or whose answer cannot be read leaves its
/// chunks without enrichment and is counted in <see cref="FailedChunkCount"/>; the index run still succeeds.
/// </para>
/// </summary>
public class IndexChunkEnricher : IngestionChunkProcessor<string>
{
    public const string KeywordsKey = "keywords";
    public const string QuestionsKey = "questions";

    /// <summary>Chunks per model call.</summary>
    public const int BatchSize = 10;

    /// <summary>The most of one chunk's text sent to the model.</summary>
    public const int MaxChunkLength = 6_000;

    public const int MaxKeywords = 8;
    public const int MaxKeywordLength = 64;
    public const int MaxQuestions = 5;
    public const int MaxQuestionLength = 200;

    private const string Instructions =
        "You prepare passages of a business document for search. For each passage, return its index and ";

    private const string KeywordsInstruction =
        "up to 8 keywords: the names, terms, identifiers and amounts a searcher would type to find it, each a word or " +
        "a short phrase taken from or implied by the passage";

    private const string QuestionsInstruction =
        "up to 5 questions the passage answers, phrased the way a person would ask them";

    private const string ContentInstruction =
        " Return only what the passage supports; for a passage with no content, return empty lists.";

    /// <summary>
    /// The language to write in, named from this fixed table by the document's language tag (from Extract), so the
    /// instructions stay constants. Without a known tag the model is asked for the passage's language, which a
    /// model may misread: kanji-heavy Japanese has come back as Chinese questions.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "Japanese",
        ["zh"] = "Chinese",
        ["zh-Hans"] = "Simplified Chinese",
        ["zh-CN"] = "Simplified Chinese",
        ["zh-Hant"] = "Traditional Chinese",
        ["zh-TW"] = "Traditional Chinese",
        ["en"] = "English",
        ["ko"] = "Korean"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly IChatClient _chatClient;
    private readonly IndexEnrichment _enrichment;
    private readonly ILogger _logger;
    private readonly string _languageInstruction;

    /// <param name="language">The document's language tag, such as <c>ja</c>; <c>null</c> when unknown.</param>
    public IndexChunkEnricher(IChatClient chatClient, IndexEnrichment enrichment, ILogger logger, string? language = null)
    {
        _chatClient = chatClient;
        _enrichment = enrichment;
        _logger = logger;
        _languageInstruction = LanguageName(language) is { } name
            ? ". Write them in " + name + ", the language of the document, whatever script a passage seems to use."
            : ". Write them in the language of the passage.";
    }

    private static string? LanguageName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim();
        if (LanguageNames.TryGetValue(trimmed, out var name))
        {
            return name;
        }

        var dash = trimmed.IndexOf('-');
        return dash > 0 && LanguageNames.TryGetValue(trimmed[..dash], out name) ? name : null;
    }

    public long InputTokenCount { get; private set; }

    public long OutputTokenCount { get; private set; }

    public int CallCount { get; private set; }

    public int FailedChunkCount { get; private set; }

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IAsyncEnumerable<IngestionChunk<string>> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var batch = new List<IngestionChunk<string>>(BatchSize);
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            batch.Add(chunk);
            if (batch.Count < BatchSize)
            {
                continue;
            }

            await EnrichAsync(batch, cancellationToken);
            foreach (var enriched in batch)
            {
                yield return enriched;
            }

            batch.Clear();
        }

        if (batch.Count > 0)
        {
            await EnrichAsync(batch, cancellationToken);
            foreach (var enriched in batch)
            {
                yield return enriched;
            }
        }
    }

    protected virtual async Task EnrichAsync(IReadOnlyList<IngestionChunk<string>> batch, CancellationToken cancellationToken)
    {
        CallCount++;
        try
        {
            var response = await _chatClient.GetResponseAsync(
                BuildMessages(batch),
                new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        AIJsonUtilities.CreateJsonSchema(typeof(EnrichmentAnswer)), "chunk_enrichment"),
                    MaxOutputTokens = 200 + batch.Count * 300
                },
                cancellationToken);
            InputTokenCount += response.Usage?.InputTokenCount ?? 0;
            OutputTokenCount += response.Usage?.OutputTokenCount ?? 0;

            var answer = Read(response.Text);
            var enriched = 0;
            foreach (var item in answer?.Chunks ?? [])
            {
                if (item.Index < 0 || item.Index >= batch.Count)
                {
                    continue;
                }

                var chunk = batch[item.Index];
                if (_enrichment.HasFlag(IndexEnrichment.Keywords))
                {
                    chunk.Metadata[KeywordsKey] = Clean(item.Keywords, MaxKeywords, MaxKeywordLength);
                }

                if (_enrichment.HasFlag(IndexEnrichment.Questions))
                {
                    chunk.Metadata[QuestionsKey] = Clean(item.Questions, MaxQuestions, MaxQuestionLength);
                }

                enriched++;
            }

            FailedChunkCount += batch.Count - Math.Min(enriched, batch.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Enriching {Count} chunks failed; they are indexed without enrichment.", batch.Count);
            FailedChunkCount += batch.Count;
        }
    }

    /// <summary>
    /// The instructions are constants chosen by the switches; each passage is cut and wrapped as untrusted document text.
    /// </summary>
    protected virtual List<ChatMessage> BuildMessages(IReadOnlyList<IngestionChunk<string>> batch)
    {
        var asks = new List<string>();
        if (_enrichment.HasFlag(IndexEnrichment.Keywords))
        {
            asks.Add(KeywordsInstruction);
        }

        if (_enrichment.HasFlag(IndexEnrichment.Questions))
        {
            asks.Add(QuestionsInstruction);
        }

        var user = new StringBuilder();
        for (var i = 0; i < batch.Count; i++)
        {
            user.Append("Passage ").Append(i).AppendLine(":");
            user.AppendLine(PromptBoundary.WrapDocument(TextTruncator.AtCharBoundary(batch[i].Content, MaxChunkLength)));
        }

        return
        [
            new(ChatRole.System, Instructions + string.Join("; and ", asks) + _languageInstruction + ContentInstruction + "\n\n" + PromptBoundary.BoundaryRule),
            new(ChatRole.User, user.ToString())
        ];
    }

    private static EnrichmentAnswer? Read(string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            json = start >= 0 && end > start ? json[start..(end + 1)] : json;
        }

        return JsonSerializer.Deserialize<EnrichmentAnswer>(json, JsonOptions);
    }

    /// <summary>One line each, trimmed, without duplicates, within the count and length caps.</summary>
    private static string[] Clean(IEnumerable<string>? values, int maxCount, int maxLength)
        => (values ?? [])
            .Select(v => Whitespace.Replace(v ?? string.Empty, " ").Trim())
            .Where(v => v.Length > 0)
            .Select(v => TextTruncator.AtCharBoundary(v, maxLength))
            .Distinct(StringComparer.Ordinal)
            .Take(maxCount)
            .ToArray();

    protected sealed class EnrichmentAnswer
    {
        public List<EnrichedPassage> Chunks { get; set; } = [];
    }

    protected sealed class EnrichedPassage
    {
        public int Index { get; set; }

        public List<string>? Keywords { get; set; }

        public List<string>? Questions { get; set; }
    }
}

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
using Dignite.Vault.Index.Ai;
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
/// An answer that cannot be read is asked for once more: in JSON schema mode a model can open the object and then emit
/// only whitespace until its token limit, which a second call usually does not repeat. Best effort beyond that, as
/// DataIngestion's enrichers are: a batch whose call fails, or whose answer cannot be read twice, leaves its chunks
/// without enrichment and is counted in <see cref="FailedChunkCount"/>; the index run still succeeds.
/// </para>
/// <para>
/// With a <see cref="ModelResultCache"/>, a chunk whose text the same model enriched under the same instructions takes
/// that result instead of a model call (<see cref="CachedChunkCount"/>), so a document re-indexed after a small edit is
/// enriched again only where it changed.
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

    /// <summary>Calls for one batch's answer when the first cannot be read.</summary>
    public const int MaxAttempts = 2;

    private const string Instructions =
        "You prepare passages of a business document for search. For each passage, return its index and ";

    private const string KeywordsInstruction =
        "up to 8 keywords: the names, terms, identifiers and amounts a searcher would type to find it, each a word or " +
        "a short phrase taken from or implied by the passage";

    private const string QuestionsInstruction =
        "up to 5 questions the passage answers, phrased the way a person would ask them";

    private const string ContentInstruction =
        " Return only what the passage supports; for a passage with no content, return empty lists.";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly IChatClient _chatClient;
    private readonly IndexEnrichment _enrichment;
    private readonly ILogger _logger;
    private readonly ModelResultCache? _cache;
    private readonly string _systemPrompt;

    /// <param name="language">The document's language tag, such as <c>ja</c>; <c>null</c> when unknown.</param>
    /// <param name="cache">Where results are reused from; <c>null</c> asks the model for every chunk.</param>
    public IndexChunkEnricher(
        IChatClient chatClient,
        IndexEnrichment enrichment,
        ILogger logger,
        string? language = null,
        ModelResultCache? cache = null)
    {
        _chatClient = chatClient;
        _enrichment = enrichment;
        _logger = logger;
        _cache = cache;

        var asks = new List<string>();
        if (enrichment.HasFlag(IndexEnrichment.Keywords))
        {
            asks.Add(KeywordsInstruction);
        }

        if (enrichment.HasFlag(IndexEnrichment.Questions))
        {
            asks.Add(QuestionsInstruction);
        }

        _systemPrompt = Instructions + string.Join("; and ", asks) + "." + PromptLanguage.Instruction(language, "them") +
            ContentInstruction + "\n\n" + PromptBoundary.BoundaryRule;
    }

    public long InputTokenCount { get; private set; }

    public long OutputTokenCount { get; private set; }

    public int CallCount { get; private set; }

    public int FailedChunkCount { get; private set; }

    /// <summary>Chunks that took a stored result instead of a model call.</summary>
    public int CachedChunkCount { get; private set; }

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
        // The key covers the instructions and the caps the stored result was cleaned to, so changing either asks again.
        var keys = _cache is null
            ? null
            : batch.Select(chunk => ModelResultCache.Key(
                _chatClient,
                $"{_systemPrompt}\n{MaxKeywords}/{MaxKeywordLength}/{MaxQuestions}/{MaxQuestionLength}",
                Passage(chunk))).ToArray();
        var pending = new List<int>();
        if (keys is not null && keys.All(key => key is not null))
        {
            var stored = await _cache!.GetManyAsync(keys!, cancellationToken);
            for (var i = 0; i < batch.Count; i++)
            {
                if (stored[i] is { } json && JsonSerializer.Deserialize<StoredEnrichment>(json) is { } result)
                {
                    Apply(batch[i], result.Keywords, result.Questions);
                    CachedChunkCount++;
                }
                else
                {
                    pending.Add(i);
                }
            }
        }
        else
        {
            keys = null;
            pending.AddRange(Enumerable.Range(0, batch.Count));
        }

        if (pending.Count == 0)
        {
            return;
        }

        var asked = pending.Select(i => batch[i]).ToList();
        var answer = await AskAsync(asked, cancellationToken);
        if (answer is null)
        {
            FailedChunkCount += asked.Count;
            return;
        }

        var results = new Dictionary<int, StoredEnrichment>();
        foreach (var item in answer.Chunks)
        {
            if (item.Index < 0 || item.Index >= asked.Count)
            {
                continue;
            }

            var result = new StoredEnrichment(
                Clean(item.Keywords, MaxKeywords, MaxKeywordLength),
                Clean(item.Questions, MaxQuestions, MaxQuestionLength));
            Apply(asked[item.Index], result.Keywords, result.Questions);
            results[item.Index] = result;
        }

        FailedChunkCount += asked.Count - results.Count;
        if (keys is not null)
        {
            await _cache!.SetManyAsync(
                results.Select(pair => new KeyValuePair<string, string>(keys[pending[pair.Key]]!, JsonSerializer.Serialize(pair.Value))),
                cancellationToken);
        }
    }

    /// <summary>
    /// One model call for the batch, asked again once when the answer cannot be read; <c>null</c> when the call fails or
    /// no answer can be read.
    /// </summary>
    protected virtual async Task<EnrichmentAnswer?> AskAsync(IReadOnlyList<IngestionChunk<string>> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
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
                return LlmJson.Read<EnrichmentAnswer>(response.Text) ?? throw new FormatException("The model returned no answer.");
            }
            catch (Exception exception) when (attempt < MaxAttempts && exception is System.Text.Json.JsonException or FormatException)
            {
                _logger.LogInformation("An enrichment answer for {Count} chunks could not be read; asking again.", batch.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Enriching {Count} chunks failed; they are indexed without enrichment.", batch.Count);
                return null;
            }
        }
    }

    /// <summary>
    /// The instructions are constants chosen by the switches; each passage is cut and wrapped as untrusted document text.
    /// </summary>
    protected virtual List<ChatMessage> BuildMessages(IReadOnlyList<IngestionChunk<string>> batch)
    {
        var user = new StringBuilder();
        for (var i = 0; i < batch.Count; i++)
        {
            user.Append("Passage ").Append(i).AppendLine(":");
            user.AppendLine(PromptBoundary.WrapDocument(Passage(batch[i])));
        }

        return
        [
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, user.ToString())
        ];
    }

    /// <summary>What the model reads of a chunk.</summary>
    private static string Passage(IngestionChunk<string> chunk) => TextTruncator.AtCharBoundary(chunk.Content, MaxChunkLength);

    private void Apply(IngestionChunk<string> chunk, string[] keywords, string[] questions)
    {
        if (_enrichment.HasFlag(IndexEnrichment.Keywords))
        {
            chunk.Metadata[KeywordsKey] = keywords;
        }

        if (_enrichment.HasFlag(IndexEnrichment.Questions))
        {
            chunk.Metadata[QuestionsKey] = questions;
        }
    }

    private sealed record StoredEnrichment(string[] Keywords, string[] Questions);

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

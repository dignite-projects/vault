using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Index.Indexing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// The chunk enricher against a scripted model: batches, the boundary around every passage, asking only for what is
/// switched on, cleaning the answer, and failures that leave chunks unenriched without stopping the rest.
/// </summary>
public class IndexChunkEnricher_Tests
{
    [Fact]
    public async Task Each_batch_is_one_call_with_every_passage_wrapped_and_the_boundary_rule()
    {
        var model = new ScriptedEnrichmentChatClient();
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords | IndexEnrichment.Questions, NullLogger.Instance);

        var chunks = await RunAsync(enricher, 23);

        model.Calls.Count.ShouldBe(3);
        model.Calls[0].System.ShouldContain(PromptBoundary.BoundaryRule);
        model.Calls[0].System.ShouldContain("keywords");
        model.Calls[0].System.ShouldContain("questions");
        Regex.Matches(model.Calls[0].User, "Passage \\d+:").Count.ShouldBe(IndexChunkEnricher.BatchSize);
        model.Calls[0].User.ShouldContain(PromptBoundary.WrapDocument("passage 0"));
        Regex.Matches(model.Calls[2].User, "Passage \\d+:").Count.ShouldBe(3);

        chunks.ShouldAllBe(c => c.Metadata.ContainsKey(IndexChunkEnricher.KeywordsKey) && c.Metadata.ContainsKey(IndexChunkEnricher.QuestionsKey));
        ((string[])chunks[12].Metadata[IndexChunkEnricher.KeywordsKey]).ShouldBe(["kw2", "term"]);
        ((string[])chunks[12].Metadata[IndexChunkEnricher.QuestionsKey]).ShouldBe(["What does passage 2 say?"]);
        enricher.InputTokenCount.ShouldBe(300);
        enricher.OutputTokenCount.ShouldBe(150);
        enricher.CallCount.ShouldBe(3);
        enricher.FailedChunkCount.ShouldBe(0);
    }

    [Fact]
    public async Task Only_what_is_switched_on_is_asked_for_and_kept()
    {
        var model = new ScriptedEnrichmentChatClient();
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords, NullLogger.Instance);

        var chunks = await RunAsync(enricher, 2);

        model.Calls[0].System.ShouldNotContain("questions the passage answers");
        chunks.ShouldAllBe(c => c.Metadata.ContainsKey(IndexChunkEnricher.KeywordsKey) && !c.Metadata.ContainsKey(IndexChunkEnricher.QuestionsKey));
    }

    [Fact]
    public async Task The_document_language_is_named_and_an_unknown_one_falls_back_to_the_passage()
    {
        foreach (var (tag, expected) in new[]
                 {
                     ("ja", "Write them in Japanese"),
                     ("zh-Hans", "Write them in Simplified Chinese"),
                     ("en-US", "Write them in English"),
                     ("xx", "Write them in the language of the passage"),
                     ((string?)null, "Write them in the language of the passage")
                 })
        {
            var model = new ScriptedEnrichmentChatClient();
            await RunAsync(new IndexChunkEnricher(model, IndexEnrichment.Questions, NullLogger.Instance, tag), 1);
            model.Calls[0].System.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task The_answer_is_cleaned_and_capped()
    {
        var model = new ScriptedEnrichmentChatClient
        {
            Answer = _ => """
                {"chunks":[{"index":0,
                  "keywords":["  契約  ","契約","","a\nb", "k1","k2","k3","k4","k5","k6","k7","k8","k9"],
                  "questions":["%LONG%"]},
                 {"index":7,"keywords":["out of range"]}]}
                """.Replace("%LONG%", new string('q', 500))
        };
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords | IndexEnrichment.Questions, NullLogger.Instance);

        var chunks = await RunAsync(enricher, 2);

        var keywords = (string[])chunks[0].Metadata[IndexChunkEnricher.KeywordsKey];
        keywords.Length.ShouldBe(IndexChunkEnricher.MaxKeywords);
        keywords.Take(2).ShouldBe(["契約", "a b"]);
        ((string[])chunks[0].Metadata[IndexChunkEnricher.QuestionsKey]).ShouldHaveSingleItem().Length.ShouldBe(IndexChunkEnricher.MaxQuestionLength);
        chunks[1].HasMetadata.ShouldBeFalse();
        enricher.FailedChunkCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_batch_leaves_its_chunks_unenriched_and_the_next_batch_runs()
    {
        var model = new ScriptedEnrichmentChatClient { FailOnCall = 1 };
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords, NullLogger.Instance);

        var chunks = await RunAsync(enricher, 12);

        chunks.Take(10).ShouldAllBe(c => !c.HasMetadata);
        chunks.Skip(10).ShouldAllBe(c => c.Metadata.ContainsKey(IndexChunkEnricher.KeywordsKey));
        enricher.FailedChunkCount.ShouldBe(10);
        enricher.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_twice_counts_as_a_failure()
    {
        var model = new ScriptedEnrichmentChatClient { Answer = _ => "Sure! Here are the keywords." };
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords, NullLogger.Instance);

        var chunks = await RunAsync(enricher, 3);

        chunks.ShouldAllBe(c => !c.HasMetadata);
        enricher.FailedChunkCount.ShouldBe(3);
        (model.Calls.Count, enricher.CallCount).ShouldBe((IndexChunkEnricher.MaxAttempts, IndexChunkEnricher.MaxAttempts));
    }

    [Fact]
    public async Task An_unreadable_answer_is_asked_for_once_more()
    {
        var model = new ScriptedEnrichmentChatClient { UnreadableOnCalls = [1] };
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords, NullLogger.Instance);

        var chunks = await RunAsync(enricher, 3);

        model.Calls[1].User.ShouldBe(model.Calls[0].User);
        chunks.ShouldAllBe(c => c.Metadata.ContainsKey(IndexChunkEnricher.KeywordsKey));
        (enricher.CallCount, enricher.FailedChunkCount).ShouldBe((2, 0));
    }

    [Fact]
    public async Task A_call_that_throws_is_not_asked_again()
    {
        var model = new ScriptedEnrichmentChatClient { FailOnCall = 1 };
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords, NullLogger.Instance);

        await RunAsync(enricher, 3);

        (model.Calls.Count, enricher.FailedChunkCount).ShouldBe((1, 3));
    }

    [Fact]
    public async Task A_long_passage_is_cut_before_it_is_sent()
    {
        var model = new ScriptedEnrichmentChatClient();
        var enricher = new IndexChunkEnricher(model, IndexEnrichment.Keywords, NullLogger.Instance);

        await RunAsync(enricher, 1, _ => new string('あ', IndexChunkEnricher.MaxChunkLength + 500));

        model.Calls[0].User.Count(c => c == 'あ').ShouldBe(IndexChunkEnricher.MaxChunkLength);
    }

    private static async Task<List<IngestionChunk<string>>> RunAsync(IndexChunkEnricher enricher, int count, Func<int, string>? text = null)
    {
        var document = new IngestionDocument("doc");
        var result = new List<IngestionChunk<string>>();
        await foreach (var chunk in enricher.ProcessAsync(Chunks(document, count, text ?? (i => "passage " + i))))
        {
            result.Add(chunk);
        }

        return result;
    }

    private static async IAsyncEnumerable<IngestionChunk<string>> Chunks(IngestionDocument document, int count, Func<int, string> text)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new IngestionChunk<string>(text(i), document);
        }
    }
}

/// <summary>
/// A chat model for enrichment that records each call and answers with one entry per passage, keywords "kw{index}" and
/// "term" and the question "What does passage {index} say?", unless told otherwise.
/// </summary>
public sealed class ScriptedEnrichmentChatClient : IChatClient
{
    public List<(string System, string User)> Calls { get; } = [];

    /// <summary>The call (from 1) that throws.</summary>
    public int? FailOnCall { get; set; }

    /// <summary>The calls (from 1) that answer an open object and whitespace, as a model in JSON schema mode can.</summary>
    public HashSet<int> UnreadableOnCalls { get; set; } = [];

    public Func<int, string>? Answer { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add((list.First(m => m.Role == ChatRole.System).Text, list.First(m => m.Role == ChatRole.User).Text));
        if (FailOnCall == Calls.Count)
        {
            throw new InvalidOperationException("model down");
        }

        var passages = Regex.Matches(Calls[^1].User, "Passage (\\d+):").Count;
        var text = UnreadableOnCalls.Contains(Calls.Count) ? "{  \n\t\t  \n" : Answer?.Invoke(passages) ?? JsonSerializer.Serialize(new {
            chunks = Enumerable.Range(0, passages).Select(i => new {
                index = i,
                keywords = new[] { "kw" + i, "term" },
                questions = new[] { $"What does passage {i} say?" }
            })
        });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

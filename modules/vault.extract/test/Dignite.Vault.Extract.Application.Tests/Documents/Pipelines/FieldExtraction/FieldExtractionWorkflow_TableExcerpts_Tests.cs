using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.Table;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// A table too long for one answer (2026-11-26: a 114-row bank statement ran Qwen3 out of output on most runs) is read in
/// excerpts, and the other fields in one call after them that also checks the merged rows. The model here is a script:
/// for an excerpt it transcribes the pipe-table rows (or flattened lines) of the excerpt it is shown, so what the workflow
/// assembles can be compared row for row with the document.
/// </summary>
public class FieldExtractionWorkflow_TableExcerpts_Tests
{
    private const string ExcerptSchemaName = "ExtractTableExcerpt";

    private static readonly FieldConfigurationDictionary TransactionColumns = new TableConfiguration
    {
        Columns = new List<InlineFieldDefinition>
        {
            new() { Name = "date", DisplayName = "Date", FieldTypeName = TextFieldType.ControlName, Required = true },
            new() { Name = "amount", DisplayName = "Amount", FieldTypeName = "Number" }
        }
    }.ConfigurationDictionary;

    private static FieldExtractionDescriptor Field(string name, string fieldTypeName, FieldConfigurationDictionary? configuration = null)
        => new(Guid.NewGuid(), name, $"Extract {name}.", fieldTypeName, configuration ?? new FieldConfigurationDictionary(), IsRequired: false);

    private static FieldExtractionDescriptor[] StatementFields() =>
    [
        Field("bank_name", TextFieldType.ControlName),
        Field("closing_balance", "Number"),
        Field("transactions", TableFieldType.ControlName, TransactionColumns)
    ];

    private static readonly Regex PipeRow = new(@"^\| (\d{4}/\d\d/\d\d) \| ([\d,]+) \|$", RegexOptions.Multiline);
    private static readonly Regex FlatRow = new(@"^(\d{4}/\d\d/\d\d) 振込 ([\d,]+)$", RegexOptions.Multiline);

    /// <summary>A statement whose table is <paramref name="rows"/> rows over pages of <paramref name="rowsPerPage"/>, the header repeated on each page.</summary>
    private static string Statement(int rows, int rowsPerPage = 40)
    {
        var sb = new StringBuilder("# 取引明細書\n\n銀行名 三井住友銀行\n\n");
        for (var i = 0; i < rows; i++)
        {
            if (i % rowsPerPage == 0)
            {
                sb.Append(i == 0 ? string.Empty : "\n次ページに続く\n\n").Append("| 日付 | 金額 |\n| --- | --- |\n");
            }

            sb.Append(PipeLine(i)).Append('\n');
        }

        return sb.Append("\n以下余白\n").ToString();
    }

    private static string Date(int i) => $"2025/{1 + (i / 28 % 12):00}/{1 + (i % 28):00}";

    private static string PipeLine(int i) => $"| {Date(i)} | {(i + 1) * 1000:N0} |";

    /// <summary>The same rows with the grid flattened into lines of text, as some parses leave a table.</summary>
    private static string FlattenedStatement(int rows)
        => "# 取引明細書\n\n銀行名 三井住友銀行\n日付 摘要 金額\n" +
           string.Join('\n', Enumerable.Range(0, rows).Select(i => $"{Date(i)} 振込 {(i + 1) * 1000:N0}")) + "\n";

    private static FieldExtractionWorkflow Workflow(IChatClient chatClient, VaultExtractBehaviorOptions? options = null)
        => new(
            chatClient,
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())),
            TestFieldTypeRegistry.Default,
            Options.Create(options ?? new VaultExtractBehaviorOptions()));

    private static bool IsExcerptCall(ChatOptions? options)
        => options?.ResponseFormat is ChatResponseFormatJson { SchemaName: ExcerptSchemaName };

    /// <summary>The excerpt the call shows, unwrapped from its document boundary.</summary>
    private static string ExcerptOf(IReadOnlyList<ChatMessage> messages)
    {
        var text = messages[2].Text;
        var start = text.IndexOf("Excerpt:\n<document>\n", StringComparison.Ordinal) + "Excerpt:\n<document>\n".Length;
        var end = text.IndexOf("\n</document>", start, StringComparison.Ordinal);
        return text[start..end];
    }

    /// <summary>What a faithful model answers for an excerpt: every row in it, in order, as the table schema asks.</summary>
    private static ChatResponse Transcribe(IReadOnlyList<ChatMessage> messages)
    {
        var excerpt = ExcerptOf(messages);
        var rows = new JsonArray();
        foreach (Match match in PipeRow.Matches(excerpt).Concat(FlatRow.Matches(excerpt)).OrderBy(m => m.Index))
        {
            rows.Add(new JsonObject
            {
                ["values"] = new JsonObject
                {
                    ["date"] = match.Groups[1].Value,
                    ["amount"] = decimal.Parse(match.Groups[2].Value.Replace(",", string.Empty))
                }
            });
        }

        var answer = new JsonObject
        {
            ["rows"] = new JsonObject { ["transactions"] = rows },
            ["sources"] = new JsonObject
            {
                ["transactions"] = rows.Count == 0 ? new JsonArray() : new JsonArray(excerpt.Split('\n').First(line => PipeRow.IsMatch(line) || FlatRow.IsMatch(line)))
            }
        };

        return Answer(answer.ToJsonString());
    }

    private const string RestAnswer =
        """
        { "values": { "bank_name": "三井住友銀行", "closing_balance": 1234567 },
          "validationWarnings": [ { "fieldName": "transactions", "message": "残高の推移が一致しません。" } ],
          "sources": { "bank_name": ["銀行名 三井住友銀行"], "closing_balance": [] } }
        """;

    private static ChatResponse Answer(string text, ChatFinishReason? finishReason = null)
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]);
        if (finishReason.HasValue)
        {
            response.FinishReason = finishReason.Value;
        }

        return response;
    }

    private static IReadOnlyList<string> Dates(JsonElement? rows)
        => rows!.Value.EnumerateArray().Select(row => row.GetProperty("values").GetProperty("date").GetString()!).ToList();

    [Fact]
    public async Task A_long_table_is_read_in_excerpts_and_every_row_arrives_once_in_document_order()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options) ? Transcribe(messages) : Answer(RestAnswer));
        var markdown = Statement(rows: 300);

        var result = await Workflow(model).ExtractAsync(StatementFields(), markdown);

        Dates(result.Values["transactions"]).ShouldBe(Enumerable.Range(0, 300).Select(Date).ToList());
        result.Values["bank_name"]!.Value.GetString().ShouldBe("三井住友銀行");
        result.IsIncomplete.ShouldBeFalse();
        model.Calls.Count(call => IsExcerptCall(call.Options)).ShouldBeGreaterThan(2);
        model.Calls.Count(call => !IsExcerptCall(call.Options)).ShouldBe(1);
    }

    [Fact]
    public async Task Each_excerpt_starting_inside_the_table_is_shown_its_header()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options) ? Transcribe(messages) : Answer(RestAnswer));

        await Workflow(model).ExtractAsync(StatementFields(), Statement(rows: 300, rowsPerPage: 150));

        var excerptMessages = model.Calls.Where(call => IsExcerptCall(call.Options)).Select(call => call.Messages[2].Text).ToList();
        var midTable = excerptMessages.Where(text => ExcerptOfText(text).StartsWith("| 20", StringComparison.Ordinal)).ToList();
        midTable.ShouldNotBeEmpty();
        midTable.ShouldAllBe(text => text.StartsWith(
            "Header of the table the excerpt starts inside:\n<document>\n| 日付 | 金額 |\n| --- | --- |\n</document>", StringComparison.Ordinal));
    }

    private static string ExcerptOfText(string text) => ExcerptOf([new ChatMessage(), new ChatMessage(), new ChatMessage(ChatRole.User, text)]);

    [Fact]
    public async Task The_excerpt_calls_ask_for_the_table_rows_and_their_sources_only()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options) ? Transcribe(messages) : Answer(RestAnswer));

        await Workflow(model).ExtractAsync(StatementFields(), Statement(rows: 300));

        var excerptCall = model.Calls.First(call => IsExcerptCall(call.Options));
        var schema = ((ChatResponseFormatJson)excerptCall.Options!.ResponseFormat!).Schema!.Value;
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldBe(["rows", "sources"]);
        schema.GetProperty("properties").GetProperty("rows").GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ShouldBe(["transactions"]);
        excerptCall.Messages[1].Text.ShouldNotContain("bank_name");
        excerptCall.Options.MaxOutputTokens.ShouldBe(new VaultExtractBehaviorOptions().MaxFieldExtractionOutputTokens);
    }

    [Fact]
    public async Task The_last_call_extracts_the_other_fields_and_checks_the_merged_rows()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options) ? Transcribe(messages) : Answer(RestAnswer));

        var result = await Workflow(model).ExtractAsync(StatementFields(), Statement(rows: 300));

        var last = model.Calls.Single(call => !IsExcerptCall(call.Options));
        var schema = ((ChatResponseFormatJson)last.Options!.ResponseFormat!).Schema!.Value;
        schema.GetProperty("properties").GetProperty("values").GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ShouldBe(["bank_name", "closing_balance"], ignoreOrder: true);
        schema.GetProperty("properties").GetProperty("validationWarnings").GetProperty("items").GetProperty("properties")
            .GetProperty("fieldName").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["bank_name", "closing_balance", "transactions"], ignoreOrder: true);

        last.Messages.Count.ShouldBe(4);
        last.Messages[1].Text.ShouldContain("Already extracted");
        last.Messages[1].Text.ShouldContain("\"transactions\"");
        var rows = JsonNode.Parse(Unwrap(last.Messages[3].Text))!["transactions"]!.AsArray();
        rows.Count.ShouldBe(300);
        rows[0]!["date"]!.GetValue<string>().ShouldBe(Date(0));
        rows[299]!["amount"]!.GetValue<decimal>().ShouldBe(300_000m);

        result.ValidationWarnings.Single().FieldName.ShouldBe("transactions");
        result.Sources["transactions"].Count.ShouldBeGreaterThan(1);
        result.Sources["bank_name"].ShouldBe(["銀行名 三井住友銀行"]);
    }

    private static string Unwrap(string text)
    {
        var start = text.IndexOf("<document>\n", StringComparison.Ordinal) + "<document>\n".Length;
        return text[start..text.LastIndexOf("\n</document>", StringComparison.Ordinal)].Replace("&lt;", "<");
    }

    [Fact]
    public async Task A_short_table_is_extracted_with_the_other_fields_in_one_call()
    {
        var model = new ScriptedChatClient((_, _) => Answer(
            """{ "values": { "bank_name": "x", "closing_balance": 1, "transactions": [ { "values": { "date": "2025/01/01", "amount": 1000 } } ] }, "validationWarnings": [], "sources": {} }"""));

        var result = await Workflow(model).ExtractAsync(StatementFields(), Statement(rows: 12));

        model.Calls.Count.ShouldBe(1);
        IsExcerptCall(model.Calls.Single().Options).ShouldBeFalse();
        result.Values["transactions"]!.Value.GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task An_excerpt_whose_answer_was_cut_off_is_read_again_in_two_halves()
    {
        var cutOnce = 0;
        var model = new ScriptedChatClient((messages, options) =>
        {
            if (!IsExcerptCall(options))
            {
                return Answer(RestAnswer);
            }

            // The first time the excerpt holding row 100 is read, the model runs away into whitespace.
            return ExcerptOf(messages).Contains(PipeLine(100), StringComparison.Ordinal) && Interlocked.Exchange(ref cutOnce, 1) == 0
                ? Answer("{\n\n\n          ", ChatFinishReason.Length)
                : Transcribe(messages);
        });
        var markdown = Statement(rows: 300);
        var excerpts = MarkdownExcerptSplitter.Split(markdown, 2048);

        var result = await Workflow(model).ExtractAsync(StatementFields(), markdown);

        Dates(result.Values["transactions"]).ShouldBe(Enumerable.Range(0, 300).Select(Date).ToList());
        result.IsIncomplete.ShouldBeFalse();
        model.Calls.Count(call => IsExcerptCall(call.Options)).ShouldBe(excerpts.Count + 2);
    }

    [Fact]
    public async Task An_excerpt_answer_short_of_its_table_rows_is_read_again_and_the_fuller_reading_kept()
    {
        var skipped = 0;
        var model = new ScriptedChatClient((messages, options) =>
        {
            if (!IsExcerptCall(options))
            {
                return Answer(RestAnswer);
            }

            var answer = Transcribe(messages);
            if (!ExcerptOf(messages).Contains(PipeLine(100), StringComparison.Ordinal) || Interlocked.Exchange(ref skipped, 1) == 1)
            {
                return answer;
            }

            // The first reading of that excerpt stops after its first three rows, the way DeepSeek-V3 returned 8 of 27.
            var root = JsonNode.Parse(answer.Text)!;
            var rows = root["rows"]!["transactions"]!.AsArray();
            while (rows.Count > 3)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            return Answer(root.ToJsonString());
        });
        var markdown = Statement(rows: 300);

        var result = await Workflow(model).ExtractAsync(StatementFields(), markdown);

        Dates(result.Values["transactions"]).ShouldBe(Enumerable.Range(0, 300).Select(Date).ToList());
        model.Calls.Count(call => IsExcerptCall(call.Options)).ShouldBe(MarkdownExcerptSplitter.Split(markdown, 2048).Count + 2);
    }

    [Fact]
    public async Task A_reading_short_by_a_table_no_field_asks_for_is_kept_after_one_more_reading()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options) ? Transcribe(messages) : Answer(RestAnswer));
        // An opening summary table whose rows the transactions field does not ask for, and the model rightly leaves out.
        var markdown = "| 期首残高 | 期末残高 |\n| --- | --- |\n| 100,000 | 239,373 |\n\n" + Statement(rows: 300);

        var result = await Workflow(model).ExtractAsync(StatementFields(), markdown);

        Dates(result.Values["transactions"]).ShouldBe(Enumerable.Range(0, 300).Select(Date).ToList());
        model.Calls.Count(call => IsExcerptCall(call.Options)).ShouldBe(MarkdownExcerptSplitter.Split(markdown, 2048).Count + 2);
    }

    [Fact]
    public async Task An_excerpt_that_cannot_be_read_leaves_the_table_unfinished_and_still_extracts_the_rest()
    {
        var model = new ScriptedChatClient((messages, options) =>
            !IsExcerptCall(options) ? Answer(RestAnswer)
            : ExcerptOf(messages).Contains(PipeLine(100), StringComparison.Ordinal) ? Answer("not json")
            : Transcribe(messages));

        var result = await Workflow(model).ExtractAsync(StatementFields(), Statement(rows: 300));

        result.Values["transactions"].ShouldBeNull();
        result.UnfinishedFields.ShouldBe(["transactions"]);
        result.IsIncomplete.ShouldBeTrue();
        result.Values["bank_name"]!.Value.GetString().ShouldBe("三井住友銀行");

        // With no rows to check, the last call neither checks the table nor names it in a warning.
        var last = model.Calls.Single(call => !IsExcerptCall(call.Options));
        last.Messages.Count.ShouldBe(3);
        last.Messages[1].Text.ShouldNotContain("transactions");
        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_excerpt_answer_cut_off_only_in_its_sources_keeps_its_rows()
    {
        var model = new ScriptedChatClient((messages, options) =>
        {
            if (!IsExcerptCall(options))
            {
                return Answer(RestAnswer);
            }

            var whole = Transcribe(messages).Text;
            return Answer(whole[..(whole.IndexOf("\"sources\"", StringComparison.Ordinal) + 20)], ChatFinishReason.Length);
        });

        var result = await Workflow(model).ExtractAsync(StatementFields(), Statement(rows: 300));

        Dates(result.Values["transactions"]).Count.ShouldBe(300);
        result.IsIncomplete.ShouldBeFalse();
    }

    [Fact]
    public async Task A_single_answer_cut_off_inside_a_table_is_redone_with_the_table_in_excerpts()
    {
        // The flattened table is not recognised as one, so nothing predicts its length: the one answer runs out inside it.
        var model = new ScriptedChatClient((messages, options) =>
            IsExcerptCall(options) ? Transcribe(messages)
            : messages.Count == 4 ? Answer(RestAnswer)
            : Answer("""{ "values": { "bank_name": "三井住友銀行", "closing_balance": 1234567, "transactions": [ { "values": { "date": "2025/0""",
                ChatFinishReason.Length));
        var markdown = FlattenedStatement(rows: 200);

        var result = await Workflow(model).ExtractAsync(StatementFields(), markdown);

        Dates(result.Values["transactions"]).ShouldBe(Enumerable.Range(0, 200).Select(Date).ToList());
        result.IsIncomplete.ShouldBeFalse();
        IsExcerptCall(model.Calls.First().Options).ShouldBeFalse();
        // Every excerpt but the first starts at a cut between two lines of text. The calls run in parallel, so they are
        // told apart by what they show, not by when they finished.
        model.Calls.Where(call => IsExcerptCall(call.Options) && !ExcerptOf(call.Messages).StartsWith("# 取引明細書", StringComparison.Ordinal))
            .ShouldAllBe(call => call.Messages[2].Text.Contains("Context before the excerpt"));
    }

    [Fact]
    public async Task Merged_rows_over_the_markdown_ceiling_leave_the_table_unfinished()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options) ? Transcribe(messages) : Answer(RestAnswer));
        var markdown = Statement(rows: 300);

        var result = await Workflow(model, new VaultExtractBehaviorOptions { MaxFieldExtractionMarkdownLength = 5_000 })
            .ExtractAsync(StatementFields(), markdown);

        result.UnfinishedFields.ShouldBe(["transactions"]);
        result.Values["transactions"].ShouldBeNull();
        model.Calls.Single(call => !IsExcerptCall(call.Options)).Messages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_type_with_only_a_table_still_has_its_rows_checked()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options)
            ? Transcribe(messages)
            : Answer("""{ "values": {}, "validationWarnings": [ { "fieldName": "transactions", "message": "合計が一致しません。" } ], "sources": {} }"""));

        var result = await Workflow(model).ExtractAsync([Field("transactions", TableFieldType.ControlName, TransactionColumns)], Statement(rows: 300));

        model.Calls.Single(call => !IsExcerptCall(call.Options)).Messages[1].Text.ShouldStartWith("Fields to extract: none.");
        result.ValidationWarnings.Single().FieldName.ShouldBe("transactions");
        Dates(result.Values["transactions"]).Count.ShouldBe(300);
    }

    [Fact]
    public async Task An_excerpt_with_none_of_the_tables_rows_contributes_none()
    {
        var model = new ScriptedChatClient((messages, options) => IsExcerptCall(options) ? Transcribe(messages) : Answer(RestAnswer));
        var markdown = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"前文の段落 {i}。" + new string('文', 60))) + "\n\n" + Statement(rows: 120);

        var result = await Workflow(model).ExtractAsync(StatementFields(), markdown);

        Dates(result.Values["transactions"]).ShouldBe(Enumerable.Range(0, 120).Select(Date).ToList());
    }

    /// <summary>An <see cref="IChatClient"/> that answers each call from a script and records it; safe for the parallel excerpt calls.</summary>
    private sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> answer) : IChatClient
    {
        private readonly ConcurrentQueue<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> _calls = new();

        public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls => _calls.ToList();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var sent = messages.ToList();
            _calls.Enqueue((sent, options));
            return Task.FromResult(answer(sent, options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

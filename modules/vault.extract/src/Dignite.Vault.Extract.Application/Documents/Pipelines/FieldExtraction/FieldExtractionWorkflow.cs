using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields.CKEditor;
using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Select;
using Dignite.Abp.FlexFields.Table;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.FlexFields;
using Dignite.Vault.Extract.FlexFields.Tags;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Unified field extraction workflow (field architecture v2). Uses one LLM call to extract field values according to
/// a <see cref="FieldExtractionDescriptor"/> list, without distinguishing Host fields from tenant fields; the caller decides the source.
/// <para>
/// Design points:
/// <list type="bullet">
///   <item>Extract all fields in one call, reducing LLM round-trips and repeated context. The exception is a table too
///         long for one answer: the <c>Table</c> fields are then read in excerpts (<see cref="MarkdownExcerptSplitter"/>),
///         and one call extracts the rest.</item>
///   <item>The model judges no validation rules: a field's rules are evaluated in code over the stored values
///         (<c>FieldValidationRuleEvaluator</c>). Measured on a bank statement, the model warned on correct values and
///         missed broken ones; the same checks in code did neither.</item>
///   <item>Constrain output schema with <c>ChatResponseFormat.ForJsonSchema</c>.</item>
///   <item>Normalization is requested in the prompt: the AI should output canonical shapes by field type
///         (bare JSON number for numbers, ISO-8601 string for dates, JSON true/false for booleans). Parsing then runs strict validation through
///         <c>FlexFieldValueReader</c> (v3's successor to v2's now-removed <c>ExtractedFieldValueValidator</c>, #593). Values that do not match
///         the declared type are written as null and logged, ensuring <c>ExtractedFields</c> type consistency (Issue #204: typed queries are
///         built on clean data).</item>
///   <item>Prompts for all fields, including Host-origin fields, are uniformly wrapped with <c>PromptBoundary.WrapField</c>.
///         This is more conservative than v1's Host/Tenant distinction and has no functional loss.</item>
/// </list>
/// </para>
/// </summary>
public class FieldExtractionWorkflow : ITransientDependency
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<FieldExtractionWorkflow> _logger;
    private readonly FieldSchemaPromptBudgetGuard _schemaPromptBudget;
    private readonly IVaultExtractFieldTypeRegistry _registry;
    private readonly VaultExtractBehaviorOptions _behaviorOptions;

    public FieldExtractionWorkflow(
        [FromKeyedServices(VaultExtractConsts.StructuredChatClientKey)] IChatClient chatClient,
        ILogger<FieldExtractionWorkflow> logger,
        FieldSchemaPromptBudgetGuard schemaPromptBudget,
        IVaultExtractFieldTypeRegistry registry,
        IOptions<VaultExtractBehaviorOptions> behaviorOptions)
    {
        _chatClient = chatClient;
        _logger = logger;
        _schemaPromptBudget = schemaPromptBudget;
        _registry = registry;
        _behaviorOptions = behaviorOptions.Value;
    }

    /// <summary>
    /// Extracts fields in batch from one LLM call, returning the field values and the passages they were found in.
    /// Missing or unparseable values appear as null. When the Markdown's table rows are longer than one answer is sized
    /// for, or the one answer was cut off inside a <c>Table</c> field, the <c>Table</c> fields are read in excerpts and the
    /// other fields in one call (see <see cref="ExtractTablesInExcerptsAsync"/>); the result has the same shape either way.
    /// </summary>
    public virtual async Task<FieldExtractionWorkflowResult> ExtractAsync(
        IReadOnlyList<FieldExtractionDescriptor> fields,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (fields.Count == 0)
        {
            return new FieldExtractionWorkflowResult(new Dictionary<string, JsonElement?>());
        }

        // Observability: after removing truncation, field extraction feeds full text. Log input size here (character count + field count)
        // to compensate for the blind spot left by the old "truncation warning". If an oversized document hits the provider context window
        // and throws a provider exception, this nearby log gives a local clue about whether the document was too large.
        // Debug level avoids polluting normal logs for per-document calls; real token usage is recorded separately by OTel gen_ai.* spans.
        _logger.LogDebug(
            "Field extraction over {CharCount} characters across {FieldCount} fields (full document, no truncation).",
            markdown.Length, fields.Count);

        // Field extraction feeds **complete Markdown** and never truncates. Type-bound fields such as contract amount,
        // invoice number, and expiration date may appear anywhere in the document; character-based tail truncation silently misses key fields.
        // This intentionally differs from classification: DocumentClassificationWorkflow can usually classify from front-section semantics
        // and therefore truncates by MaxTextLengthPerExtraction. Field extraction needs full coverage.
        // The body is not truncated here because a field can sit anywhere; the caller gates the call on
        // VaultExtractBehaviorOptions.MaxFieldExtractionMarkdownLength (#491, FieldExtractionService) instead.
        //
        // Keep the system role as a **compile-time constant** to prevent prompt injection
        // (CLAUDE.md "Security covenant / Description and Instructions must be compile-time constants").
        // Field schema, including tenant-user input f.Name / f.Prompt, goes into the first user-role message so the model treats
        // "instructions" and "user data" separately, together with PromptBoundary.WrapField + BoundaryRule.
        // FieldDefinition.Name is already entity-layer allow-list validated as [A-Za-z0-9_-]{1,64}
        // (see FieldDefinitionConsts.NamePattern), so it cannot contain line breaks, quotes, or Markdown control characters.
        // f.Prompt is admin-authored configuration and remains uncapped per field (#447). The persistence paths enforce
        // MaxFieldSchemaPromptLength over the whole type (#468); assert it again here so a repository bypass, seed, or
        // future migration can never hand an unbounded schema message to the LLM.
        _schemaPromptBudget.AssertWithinBudget(fields.Select(f => f.Prompt));

        // Shortest answers first, the open-ended ones (a Table above all) last, in the field list and in the schema. A
        // provider that writes the schema's keys in order then finishes every short value before the long ones, so an
        // answer cut off at the output limit loses the fewest fields. OrderBy is stable: equal estimates keep the
        // configured order.
        fields = fields.OrderBy(EstimateFieldChars).ToList();
        var tables = fields.Where(IsTable).ToList();
        if (tables.Count == 0)
        {
            return await CallAsync(fields, markdown, cancellationToken);
        }

        // A table's answer transcribes its rows, so rows longer than one excerpt are longer than one answer can be relied
        // on to hold. Reading them in excerpts is decided here, before any call, the way Extend sizes its chunks by table
        // size; only rows in a table the parse recognised are counted.
        var excerptChars = ExcerptChars(tables.Count);
        var tableRowChars = MarkdownExcerptSplitter.TableRowChars(markdown);
        if (tableRowChars > excerptChars)
        {
            _logger.LogInformation(
                "Field extraction reads {TableFieldCount} table field(s) in excerpts: the document's table rows run to " +
                "{TableRowChars} characters, over the {ExcerptChars} one answer is sized for.",
                tables.Count, tableRowChars, excerptChars);
            return await ExtractTablesInExcerptsAsync(fields, tables, markdown, cancellationToken);
        }

        var result = await CallAsync(fields, markdown, cancellationToken);
        if (!tables.Any(table => result.UnfinishedFields.Contains(table.Name)))
        {
            return result;
        }

        // The estimate missed: a table the parse did not recognise (its grid flattened into lines of text), or one whose
        // rows cost more output than their characters suggest. The answer is redone with the tables read in excerpts.
        _logger.LogWarning(
            "Field extraction answer was cut off inside a table field ({TableFields}); reading the table fields again in excerpts.",
            string.Join(", ", tables.Where(table => result.UnfinishedFields.Contains(table.Name)).Select(table => table.Name)));
        return await ExtractTablesInExcerptsAsync(fields, tables, markdown, cancellationToken);
    }

    /// <summary>
    /// One call: the values and sources of <paramref name="fields"/> over the whole document. Every field when no table was
    /// read in excerpts; the fields other than the tables when they were.
    /// </summary>
    private async Task<FieldExtractionWorkflowResult> CallAsync(
        IReadOnlyList<FieldExtractionDescriptor> fields,
        string markdown,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemInstructions + "\n\n" + PromptBoundary.BoundaryRule),
            new(ChatRole.User, BuildSchemaUserMessage(fields)),
            new(ChatRole.User, PromptBoundary.WrapDocument(markdown))
        };

        // The schema's worst case, capped at what the host says the structured model accepts: a max-tokens value above
        // the model's limit is refused outright (400) on every retry, which is worse than a rare Length cut-off below.
        var estimatedOutputTokens = EstimateMaxOutputTokens(fields);
        var outputTokenCeiling = MaxOutputTokenCeiling;
        if (estimatedOutputTokens > outputTokenCeiling)
        {
            _logger.LogDebug(
                "Field extraction output estimate of {EstimatedTokens} tokens capped at {MaxOutputTokens} " +
                "(Vault:ExtractBehavior:MaxFieldExtractionOutputTokens).",
                estimatedOutputTokens, outputTokenCeiling);
        }

        var options = new ChatOptions
        {
            ResponseFormat = BuildResponseFormat(fields),
            MaxOutputTokens = (int)Math.Min(estimatedOutputTokens, outputTokenCeiling)
        };

        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
        var rawJson = response.Text?.Trim() ?? string.Empty;

        if (response.FinishReason == ChatFinishReason.Length)
        {
            // The provider stopped generation at the output limit: a long Table field written out row by row is the
            // usual cause (a 114-row bank statement ran past 8,192 and 16,384 tokens), and reasoning tokens count
            // against the same limit. The answer is incomplete, never an empty extraction.
            return ParseCutOffResult(rawJson, fields, options.MaxOutputTokens!.Value);
        }

        return ParseResult(rawJson, fields);
    }

    /// <summary>
    /// The table fields read in excerpts, the rest in one call after them.
    /// <para>
    /// Each excerpt is one call for every table field: the rows that begin in it, in order, and up to three quoted
    /// passages per field, with no validation (Reducto's array extraction and LlamaExtract's per-row target likewise
    /// extract the repeated entries apart from the document-level fields). The excerpts run in parallel, a few at a time.
    /// An excerpt whose answer was cut off or could not be read is read once more, in two halves where it can be cut: a
    /// fresh call also gets past a model that ran away into whitespace. An excerpt that still has no answer leaves every
    /// table field unfinished, so the document keeps the rows it had: a table is never stored with a part missing.
    /// </para>
    /// <para>
    /// The rows are merged in document order, with no deduplication: the excerpts do not overlap. Then one call extracts
    /// the other fields over the whole document, as before. It is not given the merged rows: a rule over the whole table
    /// (a running balance, the first and last rows against the opening and closing balance) is a validation rule judged in
    /// code over the stored values, where Extend verifies its merged array with code blocks. Given the rows, the model
    /// copied a misread row's balance into the closing balance (2026-11-26: 4 of 10 runs on DeepSeek-V3, 7 of 10 on
    /// Qwen3-30B-A3B-Instruct), making the error agree with itself.
    /// </para>
    /// </summary>
    private async Task<FieldExtractionWorkflowResult> ExtractTablesInExcerptsAsync(
        IReadOnlyList<FieldExtractionDescriptor> fields,
        IReadOnlyList<FieldExtractionDescriptor> tables,
        string markdown,
        CancellationToken cancellationToken)
    {
        var excerpts = MarkdownExcerptSplitter.Split(markdown, ExcerptChars(tables.Count));
        var answers = new IReadOnlyList<ExcerptAnswer>?[excerpts.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, excerpts.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentExcerptCalls, CancellationToken = cancellationToken },
            async (index, token) => answers[index] = await ReadExcerptAsync(tables, markdown, excerpts[index], token));

        var extracted = new List<ExtractedTable>();
        var unread = answers.Count(answer => answer == null);
        if (unread > 0)
        {
            _logger.LogWarning(
                "Field extraction could not read {UnreadCount} of {ExcerptCount} excerpts, even after a second call: the table " +
                "fields ({TableFields}) are unfinished and keep the document's earlier values.",
                unread, excerpts.Count, string.Join(", ", tables.Select(table => table.Name)));
        }
        else
        {
            var read = answers.SelectMany(answer => answer!).ToList();
            extracted.AddRange(tables.Select(table => MergeRows(table, read)));
        }

        var others = fields.Where(field => !IsTable(field)).ToList();
        var rest = others.Count == 0
            ? new FieldExtractionWorkflowResult(new Dictionary<string, JsonElement?>())
            : await CallAsync(others, markdown, cancellationToken);

        var values = new Dictionary<string, JsonElement?>(rest.Values);
        var sources = new Dictionary<string, IReadOnlyList<string>>(rest.Sources, StringComparer.Ordinal);
        var unfinished = new HashSet<string>(rest.UnfinishedFields, StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var merged = extracted.FirstOrDefault(candidate => candidate.Field.Name == table.Name);
            values[table.Name] = merged?.Rows;
            if (merged == null)
            {
                unfinished.Add(table.Name);
            }
            else if (merged.Sources.Count > 0)
            {
                sources[table.Name] = merged.Sources;
            }
        }

        return new FieldExtractionWorkflowResult(values, sources)
        {
            UnfinishedFields = unfinished
        };
    }

    /// <summary>
    /// The answers for <paramref name="excerpt"/>: one, or two when it was read again in halves; <c>null</c> when it could
    /// not be read.
    /// <para>
    /// It is read again, once, in halves, when the answer was cut off or unreadable, and when it returned fewer rows than
    /// the excerpt's recognised tables hold. Models skip rows (2026-11-26, DeepSeek-V3: 8 of an excerpt's 27), and a
    /// smaller excerpt is read more faithfully. Checking the count follows Reducto's advice for Deep Extract (verify that
    /// the entries match a known count), re-reading its loop, run once here. The fuller of the two readings is kept. A
    /// reading that still returns fewer rows is kept without a review signal: the excerpt may hold a table no field asks for.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ExcerptAnswer>?> ReadExcerptAsync(
        IReadOnlyList<FieldExtractionDescriptor> tables,
        string markdown,
        MarkdownExcerpt excerpt,
        CancellationToken cancellationToken)
    {
        var answer = await CallExcerptAsync(tables, excerpt, cancellationToken);
        if (answer != null && RowCount(answer) >= excerpt.TableRows)
        {
            return [answer];
        }

        var parts = MarkdownExcerptSplitter.Halve(markdown, excerpt);
        var again = new List<ExcerptAnswer>(parts.Count);
        foreach (var part in parts)
        {
            var partAnswer = await CallExcerptAsync(tables, part, cancellationToken);
            if (partAnswer == null)
            {
                return answer == null ? null : [answer];
            }

            again.Add(partAnswer);
        }

        if (answer == null)
        {
            return again;
        }

        var rowsAgain = again.Sum(RowCount);
        _logger.LogInformation(
            "Field extraction answer for the excerpt at {Offset} returned {Rows} rows of its {TableRows} table rows; read " +
            "again in {Parts} parts, it returned {RowsAgain}. Keeping the {Kept} reading.",
            excerpt.Offset, RowCount(answer), excerpt.TableRows, again.Count, rowsAgain, rowsAgain > RowCount(answer) ? "second" : "first");
        return rowsAgain > RowCount(answer) ? again : [answer];
    }

    /// <summary>The rows one excerpt answer returned, over every table field.</summary>
    private static int RowCount(ExcerptAnswer answer)
        => answer.Rows.Values.Sum(rows => rows?.GetArrayLength() ?? 0);

    /// <summary>
    /// One excerpt's call. <c>null</c> when its rows are not known: the answer was cut off before its values closed, is
    /// not JSON, or gives a field something other than an array of rows. An answer cut off only in its sources is read.
    /// </summary>
    private async Task<ExcerptAnswer?> CallExcerptAsync(
        IReadOnlyList<FieldExtractionDescriptor> tables,
        MarkdownExcerpt excerpt,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ExcerptInstructions + "\n\n" + PromptBoundary.BoundaryRule),
            new(ChatRole.User, BuildSchemaUserMessage(tables)),
            new(ChatRole.User, BuildExcerptMessage(excerpt))
        };

        // The excerpt is sized to need a fraction of this; the ceiling only keeps an unexpected answer from being cut off.
        var options = new ChatOptions
        {
            ResponseFormat = BuildExcerptResponseFormat(tables),
            MaxOutputTokens = MaxOutputTokenCeiling
        };

        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
        var rawJson = response.Text?.Trim() ?? string.Empty;

        JsonElement root;
        if (response.FinishReason == ChatFinishReason.Length)
        {
            var answer = TruncatedJsonObject.Read(rawJson);
            if (!answer.ClosedMembers.Contains(ExcerptRowsKey))
            {
                _logger.LogWarning(
                    "Field extraction answer for the excerpt at {Offset} ({Length} characters) was cut off at the output limit " +
                    "(MaxOutputTokens={MaxOutputTokens}) before its rows closed.",
                    excerpt.Offset, excerpt.Length, options.MaxOutputTokens);
                return null;
            }

            root = JsonSerializer.SerializeToElement(answer.Members);
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(rawJson);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                _logger.LogWarning(
                    "Field extraction answer for the excerpt at {Offset} ({Length} characters) is not JSON.",
                    excerpt.Offset, excerpt.Length);
                return null;
            }
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(ExcerptRowsKey, out var values)
            || values.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning(
                "Field extraction answer for the excerpt at {Offset} ({Length} characters) has no rows object.",
                excerpt.Offset, excerpt.Length);
            return null;
        }

        var rows = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            if (!values.TryGetProperty(table.Name, out var tableRows) || tableRows.ValueKind == JsonValueKind.Null)
            {
                rows[table.Name] = null;
            }
            else if (tableRows.ValueKind == JsonValueKind.Array)
            {
                rows[table.Name] = tableRows.Clone();
            }
            else
            {
                _logger.LogWarning(
                    "Field extraction answer for the excerpt at {Offset} gives table field {FieldName} a {JsonValueKind}, not rows.",
                    excerpt.Offset, table.Name, tableRows.ValueKind);
                return null;
            }
        }

        return new ExcerptAnswer(rows, ParseSources(root, tables));
    }

    /// <summary>
    /// <paramref name="table"/>'s rows from every excerpt, in document order. No row from any excerpt is an empty table when
    /// some excerpt answered with an array, and no value when none did, as one answer over the whole document would say.
    /// </summary>
    private static ExtractedTable MergeRows(FieldExtractionDescriptor table, IReadOnlyList<ExcerptAnswer> answers)
    {
        var rows = new JsonArray();
        var answered = false;
        var sources = new List<string>();
        foreach (var answer in answers)
        {
            if (answer.Rows[table.Name] is { } excerptRows)
            {
                answered = true;
                foreach (var row in excerptRows.EnumerateArray())
                {
                    rows.Add(JsonNode.Parse(row.GetRawText()));
                }
            }

            if (answer.Sources.TryGetValue(table.Name, out var passages))
            {
                sources.AddRange(passages.Where(passage => !sources.Contains(passage, StringComparer.Ordinal)));
            }
        }

        return new ExtractedTable(table, answered ? JsonSerializer.SerializeToElement(rows) : null, sources);
    }

    private static string BuildExcerptMessage(MarkdownExcerpt excerpt)
    {
        var sb = new StringBuilder();
        void Part(string label, string text) => sb.Append(label).Append('\n').Append(PromptBoundary.WrapDocument(text)).Append('\n');

        if (excerpt.TableHeader != null)
        {
            Part(ExcerptTableHeaderLabel, excerpt.TableHeader);
        }

        if (excerpt.ContextBefore != null)
        {
            Part(ExcerptContextBeforeLabel, excerpt.ContextBefore);
        }

        Part(ExcerptLabel, excerpt.Text);

        if (excerpt.ContextAfter != null)
        {
            Part(ExcerptContextAfterLabel, excerpt.ContextAfter);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The excerpt answer's member holding the rows. Not <c>"values"</c>: SiliconFlow writes an answer's keys in
    /// alphabetical order whatever order the schema gives (#836 saw <c>"sources"</c> and <c>"validationWarnings"</c> come
    /// before <c>"values"</c>), and an excerpt answer that quoted its three source passages first went on to return only
    /// the three rows it had quoted (2026-11-26, Qwen3-30B-A3B-Instruct and DeepSeek-V3: 15 to 62 of 114 rows). Named
    /// <c>"rows"</c>, the rows come first in either order.
    /// </summary>
    private const string ExcerptRowsKey = "rows";

    private ChatResponseFormat BuildExcerptResponseFormat(IReadOnlyList<FieldExtractionDescriptor> tables)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [ExcerptRowsKey] = BuildValuesSchema(tables),
                ["sources"] = BuildSourcesSchema(tables)
            },
            ["required"] = new JsonArray { ExcerptRowsKey, "sources" },
            ["additionalProperties"] = false
        };

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return ChatResponseFormat.ForJsonSchema(
            document.RootElement.Clone(),
            schemaName: "ExtractTableExcerpt",
            schemaDescription: "The rows of table fields that begin in one excerpt of a document.");
    }

    private static bool IsTable(FieldExtractionDescriptor field)
        => string.Equals(field.FieldTypeName, TableFieldType.ControlName, StringComparison.Ordinal);

    /// <summary>
    /// The characters of Markdown one excerpt may hold. Its answer transcribes the rows that begin in it, for every table
    /// field, so the excerpt is sized for half the output the host allows, at <see cref="CjkTokenSafetyMultiplier"/> tokens
    /// a character, shared among the table fields: 2,048 characters at the default 16,384 tokens for one table. The other
    /// half is room for reasoning tokens and an estimate that runs short. Sizing low costs only a few more small calls.
    /// </summary>
    private int ExcerptChars(int tableCount)
        => Math.Max(MinExcerptChars, MaxOutputTokenCeiling / 2 / CjkTokenSafetyMultiplier / tableCount);

    /// <summary>The smallest excerpt, so a host's very low output ceiling cannot turn every line into a call of its own.</summary>
    private const int MinExcerptChars = 512;

    /// <summary>Excerpt calls in flight at once for one document.</summary>
    private const int MaxConcurrentExcerptCalls = 4;

    /// <summary>A table field whose rows were read in excerpts: the merged rows (<c>null</c> when no excerpt had the table) and the passages quoted for them.</summary>
    private sealed record ExtractedTable(FieldExtractionDescriptor Field, JsonElement? Rows, IReadOnlyList<string> Sources);

    /// <summary>One excerpt's answer: per table field, its rows there (an array, or <c>null</c>) and the passages quoted for them.</summary>
    private sealed record ExcerptAnswer(
        IReadOnlyDictionary<string, JsonElement?> Rows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Sources);

    /// <summary>
    /// Conservative worst-case output-token ceiling for the requested schema, so the provider's own default
    /// output cap (observed at ~4096 tokens on SiliconFlow when the caller sends none) can never silently cut
    /// the JSON off mid-generation for a schema that is itself within the documented per-field limits. One
    /// <c>AllowMultiple</c> field alone can legitimately need up to <c>MaxMultiValueCount</c> ×
    /// <c>MaxTextValueLength</c> characters.
    /// <para>
    /// <b>Text-bearing content is budgeted at <see cref="CjkTokenSafetyMultiplier"/> tokens per character, not
    /// 1:1.</b> <see cref="VaultExtractBehaviorOptions.DefaultLanguage"/> defaults to <c>"ja"</c> and field
    /// values preserve the document's original wording (see that option's remarks), so this product's real
    /// output is routinely Japanese/CJK. An initial 1-char-≈-1-token cut (and an earlier, even more optimistic
    /// 1.5-2-chars-per-token guess) both under-allocated in practice: replaying the real prompt for a production
    /// bank-statement document against SiliconFlow's DeepSeek-V3 showed ~3.5-3.8 tokens spent per character of
    /// generated Japanese table content (BPE tokenizers not tuned for Japanese routinely split one multi-byte
    /// kana/kanji character into 2+ tokens), which reproduced the exact mid-JSON truncation this method exists
    /// to prevent even though the estimate looked generous in characters. <c>MaxOutputTokens</c> is a ceiling
    /// the model can stop well short of, not a target it is asked to fill, so erring high here is cheap; erring
    /// low cuts the answer off and leaves the document's extraction incomplete (see <see cref="ParseCutOffResult"/>).
    /// </para>
    /// <para>
    /// <b>Erring high has a limit, though:</b> the provider refuses a max-tokens value above its model's own limit, on
    /// every retry. <see cref="ExtractAsync"/> sends this estimate only up to
    /// <see cref="VaultExtractBehaviorOptions.MaxFieldExtractionOutputTokens"/> — one default-configured Tags field
    /// estimated 177,664 tokens and SiliconFlow refused it for DeepSeek-V3 (max 163,840).
    /// </para>
    /// </summary>
    private static long EstimateMaxOutputTokens(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        long chars = 0;
        foreach (var field in fields)
        {
            chars += EstimateFieldChars(field);
        }

        // The quoted sources: up to MaxSourcesPerField passages per field, each a JSON string of quoted document text.
        chars += fields.Count * MaxSourcesPerField * ((MaxSourceLength * CjkTokenSafetyMultiplier) + 4);

        return chars + 2048; // + fixed margin for JSON structure (braces, field-name keys repeated per property).
    }

    /// <summary>
    /// Worst-case output characters for one field, from its field type and configuration.
    /// <para>
    /// The ceilings come from the same configuration the extraction schema and the value reader enforce,
    /// so a field whose limits an admin has widened is budgeted for what it can actually return rather
    /// than for a constant this method happens to hold.
    /// </para>
    /// </summary>
    private static int EstimateFieldChars(FieldExtractionDescriptor field)
    {
        // A table has no row limit, so its worst case is whatever the host's ceiling allows.
        if (string.Equals(field.FieldTypeName, TableFieldType.ControlName, StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        if (string.Equals(field.FieldTypeName, TagsFieldType.ControlName, StringComparison.Ordinal))
        {
            var tags = new TagsConfiguration(field.Configuration);
            // + 4 for the quotes, comma and spacing around each element.
            return tags.MaxCount * ((tags.MaxLength * CjkTokenSafetyMultiplier) + 4);
        }

        if (string.Equals(field.FieldTypeName, CKEditorFieldType.ControlName, StringComparison.Ordinal))
        {
            return DocumentExtractedFieldConsts.MaxLongTextValueLength * CjkTokenSafetyMultiplier;
        }

        if (string.Equals(field.FieldTypeName, TextFieldType.ControlName, StringComparison.Ordinal))
        {
            return new TextConfiguration(field.Configuration).CharLimit * CjkTokenSafetyMultiplier;
        }

        if (string.Equals(field.FieldTypeName, SelectFieldType.ControlName, StringComparison.Ordinal))
        {
            var select = new SelectConfiguration(field.Configuration);
            var longest = select.Options.Count == 0
                ? DocumentExtractedFieldConsts.MaxTextValueLength
                : select.Options.Max(o => o.Value?.Length ?? 0);

            // A multi-Select can return every option; a single one returns at most the longest.
            return select.Multiple
                ? select.Options.Count * ((longest * CjkTokenSafetyMultiplier) + 4)
                : longest * CjkTokenSafetyMultiplier;
        }

        // Number / Boolean / DateTime: a short scalar plus its JSON key and quotes, all ASCII.
        return 40;
    }

    /// <summary>
    /// Empirically measured worst-case tokens-per-character ratio for Japanese/CJK output on the structured
    /// extraction model (see <see cref="EstimateMaxOutputTokens"/>), rounded up from the ~3.5-3.8 observed for
    /// safety margin.
    /// </summary>
    private const int CjkTokenSafetyMultiplier = 4;

    /// <summary>Passages the model may quote per field as where it found the value.</summary>
    public const int MaxSourcesPerField = 3;

    /// <summary>Characters one quoted passage may hold: a value with its label, not a paragraph.</summary>
    public const int MaxSourceLength = 200;

    private int MaxOutputTokenCeiling {
        get {
            if (_behaviorOptions.MaxFieldExtractionOutputTokens <= 0)
            {
                throw new InvalidOperationException(
                    "Vault:ExtractBehavior:MaxFieldExtractionOutputTokens must be greater than zero.");
            }

            return _behaviorOptions.MaxFieldExtractionOutputTokens;
        }
    }

    /// <summary>
    /// Compile-time constant system instructions. **Do not** concatenate any runtime string into this value.
    /// </summary>
    private const string SystemInstructions =
        "You extract structured fields from a Markdown document. " +
        "The first user message lists the fields to extract (schema), each annotated with its data type. " +
        "The second user message contains the document body. " +
        "Respond with JSON only. Produce one normalized value for each requested field. " +
        ValueNormalizationInstructions +
        "A field whose schema type is array accepts multiple values: return a JSON array of strings (each a distinct value found in the document), or an empty array when none apply. " +
        TableReadingInstructions +
        "Set a field to null only when its information is genuinely absent from the document — not merely because the text is unformatted, split across lines, or lacks table syntax. " +
        "Return the result as an object with two keys, in this order: \"values\" (one key per requested field, as described above) and \"sources\". " +
        "Return each value as found in the document: never invent, alter, or fabricate data, and never drop a value because it looks inconsistent with another. " +
        "\"sources\" has one key per requested field: an array of up to three short passages copied character for character from the document body where that field's value appears, " +
        "as written in the document and not normalized (the date, amount or name exactly as printed, with its label when the label fits), one passage per distinct place, " +
        "and an empty array when the value is null.";

    /// <summary>The instructions for one excerpt's call: the rows that begin in it, nothing reordered or checked.</summary>
    private const string ExcerptInstructions =
        "You transcribe the rows of table fields from one excerpt of a longer Markdown document; the rest of the document is read in other calls. " +
        "The first user message lists the table fields to extract (schema), each annotated with its data type. " +
        "The second user message holds the excerpt. When the excerpt starts inside a table, that table's header is given before it, " +
        "and the lines just before and after the excerpt may be given as context. " +
        "Respond with JSON only: an object with two keys, in this order: \"rows\" (one key per requested field: an array with one object per row of that table that begins inside the excerpt, " +
        "or an empty array when none does) and \"sources\". " +
        "Return every such row, however many there are, exactly once and in the order the rows appear in the excerpt: do not skip, sample or summarize rows. " +
        "A table often runs over several pages: after a page break (a page number, \"continued on the next page\", the document's title again) its header is repeated and its rows go on. " +
        "The rows after such a break belong to the same table: return the rows of every part of it in the excerpt, not only of the first part. " +
        "A row that begins in the context before the excerpt was read with the previous excerpt: leave it out. " +
        "A row that begins inside the excerpt and continues into the context after it: return it whole. Never return a table header as a row. " +
        "Rules in a field's prompt about the order of the whole table (sorting or reversing it) or about checking its values (totals, running balances, the first and last rows) " +
        "apply to the whole table, not to one excerpt: do not reorder, reverse, deduplicate or check the rows here. " +
        "Follow the rest of the prompt (which table, which columns, how to read each cell) as written. " +
        ValueNormalizationInstructions +
        TableReadingInstructions +
        "Write \"rows\" in full before \"sources\". \"sources\" has one key per requested field: an array of up to three short passages copied character for character from the excerpt where that field's rows appear, " +
        "and an empty array when no row of it begins in the excerpt.";

    private const string ExcerptTableHeaderLabel = "Header of the table the excerpt starts inside:";

    private const string ExcerptContextBeforeLabel =
        "Context before the excerpt (rows that begin here were read with the previous excerpt):";

    private const string ExcerptLabel = "Excerpt:";

    private const string ExcerptContextAfterLabel =
        "Context after the excerpt (read it only to finish a row that begins in the excerpt):";

    private const string ValueNormalizationInstructions =
        "Normalize each value to its declared data type: " +
        "Number as a bare JSON number, integer or decimal (strip currency symbols, thousands separators, and units; " +
        "use '.' as the decimal point and '-' for negatives); " +
        "Date as an ISO-8601 \"YYYY-MM-DD\" JSON string; " +
        "DateTime as an offset-free ISO-8601 \"YYYY-MM-DDThh:mm:ss\" JSON string (local wall-clock time, no timezone offset or trailing Z); " +
        "Month as an ISO-8601 \"YYYY-MM\" JSON string, with no day; " +
        "Boolean as JSON true or false; " +
        "Text as the original text. " +
        "When a value cannot be normalized to its declared data type, set that field to null. ";

    private const string TableReadingInstructions =
        "The document is Markdown produced by automated extraction (digital parsing or OCR), so its layout is best-effort and table grids are frequently flattened into plain lines: " +
        "a line of column labels followed by one or more lines of space- or tab-separated values is still a table — read each such line as a row and align its cells to the column labels from left to right. " +
        "Use headings, tables, and lists as structure signals, but never require Markdown table syntax: extract tabular values even when the pipes and separators have been stripped during extraction. ";

    /// <summary>The schema message: the fields to extract, each with its type and prompt.</summary>
    private static string BuildSchemaUserMessage(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fields to extract:");
        foreach (var f in fields)
        {
            // f.Name has already been allow-list regex validated in the FieldDefinition entity layer and contains only [A-Za-z0-9_-].
            // f.Prompt comes from Host compile-time constants or tenant user input. PromptBoundary.WrapField explicitly marks it as data,
            // and BoundaryRule tells the model to treat it as non-instruction content.
            // A multi-value field is told apart by its schema (an array), not by its label. The label is a
            // system-controlled value from a fixed set, never user input, so it goes into the prompt raw.
            var typeLabel = PromptTypeLabel(f);
            var header = $"- \"{f.Name}\" ({typeLabel}, {(f.IsRequired ? "required" : "optional")})";
            // Prompt is optional. When empty, provide only "field name + type" and let the model infer what to extract from Name semantics.
            // Never output an empty PromptBoundary-wrapped block: WrapField("") would produce an empty data boundary marker,
            // polluting the schema and adding noise for the model.
            sb.AppendLine(string.IsNullOrWhiteSpace(f.Prompt)
                ? header
                : $"{header}: {PromptBoundary.WrapField(f.Prompt)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The type the instructions name for <paramref name="field"/>: its field type's key, except for a <c>DateTime</c>
    /// field, which is one type with three formats and is named by its input mode (<c>Date</c>, <c>DateTime</c> or
    /// <c>Month</c>). Labelled <c>DateTime</c>, a date field was answered in the date-time format, which the date reader
    /// rejects; the schema's pattern says otherwise, but not every provider enforces a pattern.
    /// </summary>
    protected static string PromptTypeLabel(FieldExtractionDescriptor field)
    {
        if (!string.Equals(field.FieldTypeName, DateTimeFieldType.ControlName, StringComparison.Ordinal))
        {
            return field.FieldTypeName;
        }

        return new DateTimeConfiguration(field.Configuration).InputMode switch
        {
            DateTimeInputMode.DateTime => "DateTime",
            DateTimeInputMode.Month => "Month",
            _ => "Date"
        };
    }

    private ChatResponseFormat BuildResponseFormat(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        // Values, then sources: a provider that writes the schema's keys in order finishes every value before the sources,
        // and an answer cut off in its sources is complete, because they only locate values (see
        // FieldExtractionWorkflowResult.IsIncomplete). SiliconFlow writes an answer's keys alphabetically whatever the
        // schema's order (2026-11-26: "sources" first in every answer), so there a cut lands in the values.
        // additionalProperties:false + `required` at every level keeps the shape strict.
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["values"] = BuildValuesSchema(fields),
                ["sources"] = BuildSourcesSchema(fields)
            },
            ["required"] = new JsonArray { "values", "sources" },
            ["additionalProperties"] = false
        };

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return ChatResponseFormat.ForJsonSchema(
            document.RootElement.Clone(),
            schemaName: "ExtractFieldExtraction",
            schemaDescription: "Extracted field values and the passages they were found in.");
    }

    private JsonObject BuildValuesSchema(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var field in fields)
        {
            properties[field.Name] = FlexFieldValueSchemaBuilder.Build(field.FieldTypeName, field.Configuration, _registry);
            required.Add(field.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private static JsonObject BuildSourcesSchema(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Name] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = MaxSourcesPerField,
                ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxSourceLength }
            };
            required.Add(field.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Per field, passages copied verbatim from the document where its value was found.",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private FieldExtractionWorkflowResult ParseResult(
        string rawJson,
        IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return AllNull(fields);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Field extraction returned non-JSON output: {Raw}", rawJson);
            return AllNull(fields);
        }

        // Envelope: { "values": { ... }, "sources": { ... } }, parsed independently, so a malformed source can never drop a
        // valid value.
        return new FieldExtractionWorkflowResult(ParseValues(root, fields), ParseSources(root, fields));
    }

    /// <summary>
    /// An answer the provider stopped at the output limit. What closed before the cut is kept: every field whose value
    /// finished, and each field's sources whose list closed. The field the cut fell inside, and every field after it, is
    /// unfinished: not a <c>null</c> the model wrote but no answer at all, so a half-written table never reaches the
    /// document. A cut that spared every value (only sources were lost) is a complete extraction.
    /// <para>
    /// Instructor raises on this finish reason and keeps nothing; LangExtract and Reducto split the document so no answer
    /// grows that long, which <see cref="ExtractAsync"/> also does for tables. Keeping the closed fields is what lets a
    /// cut cost only what it cut: in the order the schema asks for, the short fields close before a long one.
    /// </para>
    /// </summary>
    /// <param name="rawJson">The answer as far as it was written.</param>
    /// <param name="fields">The fields whose values the call asked for.</param>
    /// <param name="maxOutputTokens">The output limit the answer was cut off at.</param>
    protected virtual FieldExtractionWorkflowResult ParseCutOffResult(
        string rawJson,
        IReadOnlyList<FieldExtractionDescriptor> fields,
        int maxOutputTokens)
    {
        var answer = TruncatedJsonObject.Read(rawJson);
        var root = JsonSerializer.SerializeToElement(answer.Members);

        var valuesClosed = answer.ClosedMembers.Contains("values");
        var writtenValues = answer.Members["values"] as JsonObject;
        var unfinished = valuesClosed
            ? new HashSet<string>(StringComparer.Ordinal)
            : fields.Where(f => writtenValues == null || !writtenValues.ContainsKey(f.Name))
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

        var result = new FieldExtractionWorkflowResult(ParseValues(root, fields), ParseSources(root, fields))
        {
            UnfinishedFields = unfinished
        };

        _logger.LogWarning(
            "Field extraction answer was cut off at the output limit (MaxOutputTokens={MaxOutputTokens}): " +
            "{UnfinishedCount} of {FieldCount} fields unfinished ({UnfinishedFields}). " +
            "Unfinished fields keep the document's earlier values. If the structured model allows more output, " +
            "raise Vault:ExtractBehavior:MaxFieldExtractionOutputTokens.",
            maxOutputTokens, unfinished.Count, fields.Count, string.Join(", ", unfinished));

        return result;
    }

    private static FieldExtractionWorkflowResult AllNull(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var values = new Dictionary<string, JsonElement?>(fields.Count);
        foreach (var f in fields) values[f.Name] = null;
        return new FieldExtractionWorkflowResult(values);
    }

    private IReadOnlyDictionary<string, JsonElement?> ParseValues(
        JsonElement root,
        IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var result = new Dictionary<string, JsonElement?>(fields.Count);

        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            foreach (var f in fields) result[f.Name] = null;
            return result;
        }

        foreach (var field in fields)
        {
            if (!values.TryGetProperty(field.Name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                result[field.Name] = null;
                continue;
            }

            // Deliberately NOT validated here. Under v2 this method checked the value against the declared
            // FieldDataType and the service then converted it separately, so the same rules lived in two
            // places that only agreed because both switched on the same enum. v3's FlexFieldValueReader
            // validates and converts in one step against the field type's configuration, and the service
            // has to call it anyway — a second gate here could only diverge from it.
            //
            // The raw JsonElement is kept as-is: it is already the canonical JSON shape, and re-encoding it
            // would cost precision on decimals for nothing.
            result[field.Name] = prop;
        }

        return result;
    }

    /// <summary>
    /// The quoted passages per declared field, untrusted model output: undeclared fields, non-strings and blanks are
    /// dropped, and the count and length are held to the schema's bounds whether or not the provider enforced them.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseSources(
        JsonElement root,
        IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var field in fields)
        {
            if (!sources.TryGetProperty(field.Name, out var quoted) || quoted.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var passages = quoted.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(passage => passage.Length > 0)
                .Select(passage => TextTruncator.AtCharBoundary(passage, MaxSourceLength))
                .Take(MaxSourcesPerField)
                .ToList();
            if (passages.Count > 0)
            {
                result[field.Name] = passages;
            }
        }

        return result;
    }

}

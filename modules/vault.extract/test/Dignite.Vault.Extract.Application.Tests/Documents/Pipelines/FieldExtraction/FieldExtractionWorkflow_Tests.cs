using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.CKEditor;
using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Table;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.FlexFields.Tags;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Contract + normalization semantics for <see cref="FieldExtractionWorkflow"/> (#204 typed values, #527 §1/§3
/// value+warning envelope). IChatClient is replaced with NSubstitute — no real LLM. The workflow now returns a
/// <see cref="FieldExtractionWorkflowResult"/> (<c>{ values, validationWarnings }</c>): values keep the strict
/// typed-validation behavior (matching types kept, mismatches nulled, now under the <c>values</c> key), and warnings are
/// defensively normalized (undeclared / blank / malformed discarded, deduped per field, truncated, capped) without ever
/// dropping a valid value. These tests exercise the server-side parser, not the JSON schema (the mock returns raw JSON).
/// </summary>
public class FieldExtractionWorkflow_Tests
{
    private static FieldExtractionWorkflow CreateWorkflow(string jsonResponse)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)])));

        return new FieldExtractionWorkflow(
            chatClient,
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())),
            TestFieldTypeRegistry.Default,
            Options.Create(new VaultExtractBehaviorOptions()));
    }

    private static FieldExtractionDescriptor Field(
        string name, string fieldTypeName, FieldConfigurationDictionary? configuration = null)
        => new(System.Guid.NewGuid(), name, $"Extract {name}.", fieldTypeName,
            configuration ?? new FieldConfigurationDictionary(), IsRequired: false);

    /// <summary>Open-vocabulary multi-value — what v2 expressed as a Text field with AllowMultiple.</summary>
    private static FieldExtractionDescriptor MultiField(string name)
        => Field(name, TagsFieldType.ControlName);

    // Date and DateTime share one field type in v3 and are told apart by InputMode, so the two v2 data
    // types become two configurations of the same descriptor.
    private static FieldExtractionDescriptor DateField(string name)
        => Field(name, DateTimeFieldType.ControlName,
            new DateTimeConfiguration { InputMode = DateTimeInputMode.Date }.ConfigurationDictionary);

    private static FieldExtractionDescriptor DateTimeField(string name)
        => Field(name, DateTimeFieldType.ControlName,
            new DateTimeConfiguration { InputMode = DateTimeInputMode.DateTime }.ConfigurationDictionary);

    private static (FieldExtractionWorkflow Workflow, Func<ChatOptions?> CapturedOptions) CreateWorkflowCapturingOptions(
        string jsonResponse, ChatFinishReason? finishReason = null, VaultExtractBehaviorOptions? behaviorOptions = null)
    {
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? captured = null;
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)]);
        if (finishReason.HasValue)
        {
            response.FinishReason = finishReason.Value;
        }

        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var workflow = new FieldExtractionWorkflow(
            chatClient,
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())),
            TestFieldTypeRegistry.Default,
            Options.Create(behaviorOptions ?? new VaultExtractBehaviorOptions()));

        return (workflow, () => captured);
    }

    // ─── values: typed validation (unchanged behavior, now under the `values` key) ───

    [Fact]
    public async Task Keeps_values_matching_declared_type()
    {
        var json = """
        {
          "values": {
            "amount": 1500.50,
            "count": 3,
            "active": true,
            "signed_on": "2024-01-15",
            "party": "Acme Corp"
          },
          "validationWarnings": []
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[]
            {
                Field("amount", "Number"),
                Field("count", "Number"),
                Field("active", "Boolean"),
                DateField("signed_on"),
                Field("party", "Text"),
            },
            "# doc");

        result.Values["amount"]!.Value.GetDecimal().ShouldBe(1500.50m);
        result.Values["count"]!.Value.GetInt64().ShouldBe(3);
        result.Values["active"]!.Value.GetBoolean().ShouldBeTrue();
        result.Values["signed_on"]!.Value.GetString().ShouldBe("2024-01-15");
        result.Values["party"]!.Value.GetString().ShouldBe("Acme Corp");
        result.ValidationWarnings.ShouldBeEmpty();
    }

    /// <summary>
    /// The workflow passes values through as the model returned them and does <b>not</b> type-check.
    /// <para>
    /// v2 validated here and converted in the service, so the same rules lived in two places that agreed
    /// only because both switched on the same enum. v3 has one gate — <c>FlexFieldValueReader</c>, which the
    /// service must call anyway — and the assertions that used to live in this test now live in
    /// <c>FlexFieldValueReader_Tests</c>, against the field type rather than a data-type enum.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Passes_values_through_without_type_checking_them()
    {
        var json = """
        {
          "values": {
            "amount": "about 100k",
            "party": 123
          },
          "validationWarnings": []
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[] { Field("amount", "Number"), Field("party", "Text") },
            "# doc");

        // Shapes that do not match their field type survive this far; the reader is what rejects them.
        result.Values["amount"]!.Value.GetString().ShouldBe("about 100k");
        result.Values["party"]!.Value.GetInt32().ShouldBe(123);
    }

    [Fact]
    public async Task DateTime_must_be_offset_free_wall_clock()
    {
        var json = """
        {
          "values": {
            "offset_free": "2024-01-01T10:00:00",
            "with_offset": "2024-01-01T10:00:00+08:00",
            "utc_z": "2024-01-01T10:00:00Z"
          },
          "validationWarnings": []
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[]
            {
                DateTimeField("offset_free"),
                DateTimeField("with_offset"),
                DateTimeField("utc_z"),
            },
            "# doc");

        // All three come back untouched: rejecting the offset-bearing ones is the reader's job now, and
        // FlexFieldValueReader_Tests covers it. What this still pins is that the workflow does not mangle
        // the string on the way through.
        result.Values["offset_free"]!.Value.GetString().ShouldBe("2024-01-01T10:00:00");
        result.Values["with_offset"]!.Value.GetString().ShouldBe("2024-01-01T10:00:00+08:00");
        result.Values["utc_z"]!.Value.GetString().ShouldBe("2024-01-01T10:00:00Z");
    }

    [Fact]
    public async Task Missing_or_explicit_null_becomes_null()
    {
        var workflow = CreateWorkflow(
            """{ "values": { "present": "x", "explicit_null": null }, "validationWarnings": [] }""");

        var result = await workflow.ExtractAsync(
            new[]
            {
                Field("present", "Text"),
                Field("explicit_null", "Text"),
                Field("absent", "Text"),
            },
            "# doc");

        result.Values["present"]!.Value.GetString().ShouldBe("x");
        result.Values["explicit_null"].ShouldBeNull();
        result.Values["absent"].ShouldBeNull();
    }

    [Fact]
    public async Task Non_json_output_nulls_all_fields_and_has_no_warnings()
    {
        var workflow = CreateWorkflow("sorry, I can't do that");

        var result = await workflow.ExtractAsync(
            new[] { Field("amount", "Number") },
            "# doc");

        result.Values["amount"].ShouldBeNull();
        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_values_key_nulls_all_fields()
    {
        // Schema drift: no `values` key -> every field is null (graceful degradation, like the old flat-shape miss).
        var workflow = CreateWorkflow("""{ "validationWarnings": [] }""");

        var result = await workflow.ExtractAsync(
            new[] { Field("amount", "Number"), Field("party", "Text") },
            "# doc");

        result.Values["amount"].ShouldBeNull();
        result.Values["party"].ShouldBeNull();
    }

    /// <summary>
    /// The workflow hands multi-valued JSON through untouched — array shape, element types and the count
    /// cap are all <see cref="FlexFieldValueReader"/>'s to enforce, and it runs on both the extraction and
    /// the operator-edit path. Under v2 this method validated as well, so the same rules lived in two
    /// places that agreed only because both switched on the same enum. What must survive here is that the
    /// raw <c>JsonElement</c> reaches the service undamaged: re-encoding it would cost decimal precision
    /// for nothing, and dropping it would hide a rejection the service reports per field.
    /// <para>
    /// The rejections themselves are pinned in <c>FlexFieldValueReader_Tests</c>
    /// (<c>Tags_rejects_a_scalar</c> / <c>Tags_rejects_a_non_string_element</c> /
    /// <c>Tags_over_the_count_cap_is_rejected_whole</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Multi_value_json_reaches_the_service_verbatim()
    {
        var json = """
        {
          "values": {
            "tags": ["urgent", "legal", "2026"],
            "scalar_tags": "urgent"
          },
          "validationWarnings": []
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[] { MultiField("tags"), MultiField("scalar_tags"), MultiField("absent_tags") },
            "# doc");

        result.Values["tags"]!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Array);
        result.Values["tags"]!.Value.EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "urgent", "legal", "2026" });

        // Off-shape, but still passed on: the workflow is not the gate, and swallowing it here would turn a
        // reported per-field rejection into a silently missing field.
        result.Values["scalar_tags"]!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);

        // A field the model omitted is the one case the workflow does decide: absent means null.
        result.Values["absent_tags"].ShouldBeNull();
    }

    [Fact]
    public async Task Empty_field_list_short_circuits_without_calling_llm()
    {
        var workflow = CreateWorkflow("{}");

        var result = await workflow.ExtractAsync(Array.Empty<FieldExtractionDescriptor>(), "# doc");

        result.Values.ShouldBeEmpty();
        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Over_budget_schema_assertion_blocks_the_llm_call()
    {
        var chatClient = Substitute.For<IChatClient>();
        var workflow = new FieldExtractionWorkflow(
            chatClient,
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions
            {
                MaxFieldSchemaPromptLength = 4
            })),
            TestFieldTypeRegistry.Default,
            Options.Create(new VaultExtractBehaviorOptions()));
        var fields = new[]
        {
            new FieldExtractionDescriptor(
                Guid.NewGuid(), "body", "12345", TextFieldType.ControlName, new FieldConfigurationDictionary(), IsRequired: false)
        };

        // InvalidDataException: over a configured limit on every retry, so the job ends instead of being retried.
        var error = await Should.ThrowAsync<System.IO.InvalidDataException>(() => workflow.ExtractAsync(fields, "# doc"));
        Dignite.Vault.Extract.Documents.Pipelines.ProviderFailureClassifier.IsInvalidInput(error).ShouldBeTrue();

        await chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── validationWarnings: server-side normalization (#527 §3) ───

    [Fact]
    public async Task Warning_is_returned_and_the_field_value_is_kept()
    {
        // The core #527 contract: a warned field KEEPS its value (never nulled), and the warning is returned separately.
        var json = """
        {
          "values": { "transactions": "| Date | Balance |" },
          "validationWarnings": [
            { "fieldName": "transactions", "message": "Row 4 balance does not reconcile." }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("transactions", "Text") }, "# doc");

        result.Values["transactions"].ShouldNotBeNull();   // value preserved despite the warning
        result.ValidationWarnings.Count.ShouldBe(1);
        result.ValidationWarnings[0].FieldName.ShouldBe("transactions");
        result.ValidationWarnings[0].Message.ShouldBe("Row 4 balance does not reconcile.");
    }

    [Fact]
    public async Task Warning_for_undeclared_field_is_discarded()
    {
        var json = """
        {
          "values": { "amount": 100 },
          "validationWarnings": [
            { "fieldName": "amount", "message": "problem" },
            { "fieldName": "not_a_field", "message": "should be dropped" }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.ValidationWarnings.Select(w => w.FieldName).ShouldBe(new[] { "amount" });
    }

    [Fact]
    public async Task Blank_message_is_discarded()
    {
        var workflow = CreateWorkflow(
            """{ "values": { "amount": 100 }, "validationWarnings": [ { "fieldName": "amount", "message": "   " } ] }""");

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Duplicate_warnings_for_one_field_are_merged_to_one()
    {
        var json = """
        {
          "values": { "amount": 100 },
          "validationWarnings": [
            { "fieldName": "amount", "message": "first" },
            { "fieldName": "amount", "message": "second" }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.ValidationWarnings.Count.ShouldBe(1);
        result.ValidationWarnings[0].Message.ShouldBe("first");   // first wins
    }

    [Fact]
    public async Task Overlong_message_is_truncated_at_char_boundary()
    {
        var longMessage = new string('x', DocumentFieldValidationWarningConsts.MaxMessageLength + 50);
        var workflow = CreateWorkflow(
            $$"""{ "values": { "amount": 100 }, "validationWarnings": [ { "fieldName": "amount", "message": "{{longMessage}}" } ] }""");

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.ValidationWarnings.Single().Message.Length
            .ShouldBe(DocumentFieldValidationWarningConsts.MaxMessageLength);
    }

    [Fact]
    public async Task Excess_warnings_are_capped()
    {
        // More distinct warned fields than the cap -> only MaxWarningsPerExtraction warnings are kept.
        var fields = Enumerable.Range(0, DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction + 5)
            .Select(i => Field($"f{i}", "Text")).ToArray();
        var values = string.Join(",", fields.Select(f => $$""" "{{f.Name}}": "v" """));
        var warnings = string.Join(",", fields.Select(f => $$"""{ "fieldName": "{{f.Name}}", "message": "bad" }"""));
        var workflow = CreateWorkflow($$"""{ "values": { {{values}} }, "validationWarnings": [ {{warnings}} ] }""");

        var result = await workflow.ExtractAsync(fields, "# doc");

        result.ValidationWarnings.Count.ShouldBe(DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction);
    }

    [Fact]
    public async Task Malformed_warning_entries_do_not_drop_valid_values_or_the_valid_warning()
    {
        // A non-object entry, a missing-message entry, and a wrong-typed fieldName are all discarded; the valid value and
        // the valid warning survive — a malformed warning never corrupts the values half (#527 §3).
        var json = """
        {
          "values": { "amount": 100 },
          "validationWarnings": [
            "not-an-object",
            { "fieldName": "amount" },
            { "fieldName": 42, "message": "x" },
            { "fieldName": "amount", "message": "valid" }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.Values["amount"]!.Value.GetInt64().ShouldBe(100);
        result.ValidationWarnings.Count.ShouldBe(1);
        result.ValidationWarnings[0].Message.ShouldBe("valid");
    }

    [Fact]
    public async Task Missing_validationWarnings_key_yields_empty()
    {
        var workflow = CreateWorkflow("""{ "values": { "amount": 100 } }""");

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.Values["amount"].ShouldNotBeNull();
        result.ValidationWarnings.ShouldBeEmpty();
    }

    // ─── MaxOutputTokens: reproduces a real silent-truncation incident on a many-row AllowMultiple field ───
    // (SiliconFlow's own default output cap, observed at ~4096 tokens, applies whenever the caller sends none;
    // the JSON gets cut off mid-generation and fails to parse, silently nulling every field on the call).

    // A host whose structured model allows effectively unlimited output, so the schema estimate is sent uncapped.
    private static VaultExtractBehaviorOptions UncappedOutput => new() { MaxFieldExtractionOutputTokens = int.MaxValue };

    [Fact]
    public async Task MaxOutputTokens_covers_an_AllowMultiple_fields_own_worst_case_when_the_model_allows_it()
    {
        var (workflow, capturedOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "tags": [] }, "validationWarnings": [] }""", behaviorOptions: UncappedOutput);

        await workflow.ExtractAsync(new[] { MultiField("tags") }, "# doc");

        // A hard floor, not an exact figure: below the host's ceiling, MaxOutputTokens must at least cover every
        // array slot at its max length, or a full multi-value field can never finish generating before the request's
        // own ceiling truncates it — the same failure the estimate targets, just self-inflicted instead of
        // provider-inflicted.
        capturedOptions()!.MaxOutputTokens.ShouldNotBeNull();
        capturedOptions()!.MaxOutputTokens!.Value.ShouldBeGreaterThan(
            DocumentExtractedFieldConsts.MaxMultiValueCount * DocumentExtractedFieldConsts.MaxTextValueLength);
    }

    [Fact]
    public async Task MaxOutputTokens_scales_up_when_an_AllowMultiple_field_is_requested_and_the_model_allows_it()
    {
        var (scalarWorkflow, scalarOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "amount": 1 }, "validationWarnings": [] }""", behaviorOptions: UncappedOutput);
        await scalarWorkflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        var (multiWorkflow, multiOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "tags": [] }, "validationWarnings": [] }""", behaviorOptions: UncappedOutput);
        await multiWorkflow.ExtractAsync(new[] { MultiField("tags") }, "# doc");

        multiOptions()!.MaxOutputTokens!.Value.ShouldBeGreaterThan(scalarOptions()!.MaxOutputTokens!.Value);
    }

    // ─── MaxOutputTokens ceiling: reproduces the 2026-11-10 self-test failure ───
    // A contract type with one default-configured Tags field estimated 177,664 output tokens; SiliconFlow refused
    // it for DeepSeek-V3 ("max_tokens (177664) have exceeded max_seq_len (163840)") on every retry, so the document
    // ended Failed. OpenAI's gpt-4o-mini allows 16,384 completion tokens — below even the validation-warning
    // allowance alone — so with no ceiling every field extraction against it was refused.

    [Fact]
    public async Task MaxOutputTokens_never_exceeds_the_hosts_ceiling_for_a_default_Tags_schema()
    {
        var (workflow, capturedOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "contract_keywords": [], "party_a_name": null }, "validationWarnings": [] }""");

        await workflow.ExtractAsync(
            new[] { MultiField("contract_keywords"), Field("party_a_name", TextFieldType.ControlName) }, "# doc");

        capturedOptions()!.MaxOutputTokens.ShouldBe(new VaultExtractBehaviorOptions().MaxFieldExtractionOutputTokens);
        capturedOptions()!.MaxOutputTokens!.Value.ShouldBeLessThanOrEqualTo(16_384);
    }

    [Fact]
    public async Task MaxOutputTokens_follows_a_ceiling_the_host_configures()
    {
        var (workflow, capturedOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "amount": 1 }, "validationWarnings": [] }""",
            behaviorOptions: new VaultExtractBehaviorOptions { MaxFieldExtractionOutputTokens = 8_192 });

        await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        capturedOptions()!.MaxOutputTokens.ShouldBe(8_192);
    }

    [Fact]
    public async Task MaxOutputTokens_sends_an_estimate_below_the_ceiling_unchanged()
    {
        var (uncappedWorkflow, uncappedOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "amount": 1 }, "validationWarnings": [] }""", behaviorOptions: UncappedOutput);
        await uncappedWorkflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");
        var estimate = uncappedOptions()!.MaxOutputTokens!.Value;

        var (workflow, capturedOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "amount": 1 }, "validationWarnings": [] }""",
            behaviorOptions: new VaultExtractBehaviorOptions { MaxFieldExtractionOutputTokens = estimate + 1 });
        await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        capturedOptions()!.MaxOutputTokens.ShouldBe(estimate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_output_ceiling_fails_before_calling_the_model(int ceiling)
    {
        var (workflow, capturedOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "amount": 1 }, "validationWarnings": [] }""",
            behaviorOptions: new VaultExtractBehaviorOptions { MaxFieldExtractionOutputTokens = ceiling });

        await Should.ThrowAsync<InvalidOperationException>(
            () => workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc"));
        capturedOptions().ShouldBeNull();
    }

    [Fact]
    public async Task Response_cut_off_at_the_token_limit_degrades_without_throwing()
    {
        // finish_reason=length with an incomplete (unterminated) JSON body — the exact shape observed against a
        // real multi-page document once its AllowMultiple field grew past the provider's silent default cap.
        // ExtractAsync must not throw, and the cut field is unfinished rather than a null the model wrote.
        var (workflow, _) = CreateWorkflowCapturingOptions(
            """{ "values": { "amount": 1""",
            ChatFinishReason.Length);

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.Values["amount"].ShouldBeNull();
        result.UnfinishedFields.ShouldBe(["amount"]);
        result.IsIncomplete.ShouldBeTrue();
    }

    // ─── an answer cut off at the output limit ───
    // 2026-11-26, a bank statement with a 114-row transaction table: Qwen3-32B (8,192 output tokens) ran out on every run
    // and Qwen3-30B-A3B-Instruct (16,384) on two of three, and the unparseable answer nulled every field, the scalars too.

    private static readonly FieldConfigurationDictionary TransactionColumns = new TableConfiguration
    {
        Columns = new List<InlineFieldDefinition>
        {
            new() { Name = "date", DisplayName = "Date", FieldTypeName = TextFieldType.ControlName, Required = true },
            new() { Name = "amount", DisplayName = "Amount", FieldTypeName = "Number" }
        }
    }.ConfigurationDictionary;

    private static FieldExtractionDescriptor[] StatementFields() =>
    [
        Field("transactions", TableFieldType.ControlName, TransactionColumns),
        Field("bank_name", TextFieldType.ControlName),
        Field("closing_balance", "Number")
    ];

    [Fact]
    public async Task A_cut_off_answer_keeps_the_values_that_closed_and_none_of_the_cut_table()
    {
        var (workflow, _) = CreateWorkflowCapturingOptions(
            """
            { "values": { "bank_name": "三井住友銀行", "closing_balance": 1234567, "transactions": [
              { "values": { "date": "2026-10-01", "amount": 12000 } },
              { "values": { "date": "2026-10-0
            """,
            ChatFinishReason.Length);

        var result = await workflow.ExtractAsync(StatementFields(), "# doc");

        result.Values["bank_name"]!.Value.GetString().ShouldBe("三井住友銀行");
        result.Values["closing_balance"]!.Value.GetDecimal().ShouldBe(1234567m);
        result.Values["transactions"].ShouldBeNull();
        result.UnfinishedFields.ShouldBe(["transactions"]);
        result.ValidationWarningsUnfinished.ShouldBeTrue();
        result.IsIncomplete.ShouldBeTrue();
    }

    /// <summary>A number the cut fell right after may have had more digits: "12" could be the start of "1234567".</summary>
    [Fact]
    public async Task A_number_at_the_cut_is_unfinished_not_read_short()
    {
        var (workflow, _) = CreateWorkflowCapturingOptions(
            """{ "values": { "bank_name": "三井住友銀行", "closing_balance": 12""",
            ChatFinishReason.Length);

        var result = await workflow.ExtractAsync(StatementFields(), "# doc");

        result.Values["bank_name"]!.Value.GetString().ShouldBe("三井住友銀行");
        result.Values["closing_balance"].ShouldBeNull();
        result.UnfinishedFields.ShouldBe(["closing_balance", "transactions"], ignoreOrder: true);
    }

    /// <summary>The order the measured provider wrote the answer in: sources and warnings first, values last.</summary>
    [Fact]
    public async Task A_cut_off_answer_written_values_last_keeps_its_sources_and_warnings()
    {
        var (workflow, _) = CreateWorkflowCapturingOptions(
            """
            { "sources": { "bank_name": ["銀行名 三井住友銀行"], "closing_balance": ["残高 1,234,567"], "transactions": ["10/01 12,000"] },
              "validationWarnings": [ { "fieldName": "closing_balance", "message": "残高が取引の合計と一致しません。" } ],
              "values": { "bank_name": "三井住友銀行", "closing_balance": 1234567, "transactions": [ { "values": { "date": "2026-10-01"
            """,
            ChatFinishReason.Length);

        var result = await workflow.ExtractAsync(StatementFields(), "# doc");

        result.Values["closing_balance"]!.Value.GetDecimal().ShouldBe(1234567m);
        result.UnfinishedFields.ShouldBe(["transactions"]);
        result.ValidationWarningsUnfinished.ShouldBeFalse();
        result.ValidationWarnings.Single().FieldName.ShouldBe("closing_balance");
        result.Sources["bank_name"].ShouldBe(["銀行名 三井住友銀行"]);
    }

    /// <summary>
    /// Every value finished but the warnings did not: a balance check that failed may be among the missing warnings, so
    /// the extraction is still incomplete.
    /// </summary>
    [Fact]
    public async Task An_answer_cut_off_inside_its_warnings_has_every_value_and_is_still_incomplete()
    {
        var (workflow, _) = CreateWorkflowCapturingOptions(
            """
            { "values": { "bank_name": "三井住友銀行", "closing_balance": 1234567,
                          "transactions": [ { "values": { "date": "2026-10-01" } } ] },
              "validationWarnings": [ { "fieldName": "closing_balance", "message": "残高が
            """,
            ChatFinishReason.Length);

        var result = await workflow.ExtractAsync(StatementFields(), "# doc");

        result.Values["bank_name"]!.Value.GetString().ShouldBe("三井住友銀行");
        result.Values["transactions"]!.Value.GetArrayLength().ShouldBe(1);
        result.UnfinishedFields.ShouldBeEmpty();
        result.ValidationWarnings.ShouldBeEmpty();
        result.ValidationWarningsUnfinished.ShouldBeTrue();
        result.IsIncomplete.ShouldBeTrue();
    }

    /// <summary>Sources only locate values: an answer cut off inside them has still settled every value and warning.</summary>
    [Fact]
    public async Task An_answer_cut_off_inside_its_sources_is_complete()
    {
        var (workflow, _) = CreateWorkflowCapturingOptions(
            """
            { "values": { "bank_name": "三井住友銀行", "closing_balance": 1234567, "transactions": [] },
              "validationWarnings": [],
              "sources": { "bank_name": ["銀行名 三井住友銀行"], "closing_balance": ["残高 1,23
            """,
            ChatFinishReason.Length);

        var result = await workflow.ExtractAsync(StatementFields(), "# doc");

        result.IsIncomplete.ShouldBeFalse();
        result.Values["transactions"]!.Value.GetArrayLength().ShouldBe(0);
        result.Sources.Keys.ShouldBe(["bank_name"]);
    }

    [Theory]
    [InlineData("")]                                              // reasoning used up the whole output allowance
    [InlineData("{\n\n\n\n            ")]                         // Qwen3's whitespace runaway after the opening brace
    [InlineData("""{ "values": { "bank_name": "三井""")]           // cut inside the first value
    [InlineData("""{ "values": { "bank_name": "三井住友銀行", oops""")] // ran off into invalid text: nothing after the last close
    public async Task A_cut_off_answer_leaves_unfinished_every_field_whose_value_did_not_close(string answer)
    {
        var (workflow, _) = CreateWorkflowCapturingOptions(answer, ChatFinishReason.Length);

        var result = await workflow.ExtractAsync(StatementFields(), "# doc");

        var finished = StatementFields().Select(f => f.Name).Except(result.UnfinishedFields).ToList();
        finished.ShouldBe(answer.Contains("oops") ? new[] { "bank_name" } : Array.Empty<string>());
        finished.ShouldAllBe(name => result.Values[name] != null);
        result.IsIncomplete.ShouldBeTrue();
    }

    [Fact]
    public async Task The_schema_asks_for_values_then_warnings_then_sources_and_a_table_last()
    {
        var chatClient = Substitute.For<IChatClient>();
        List<ChatMessage>? sent = null;
        ChatOptions? options = null;
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => sent = messages.ToList()),
                Arg.Do<ChatOptions?>(o => options = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "{ \"values\": {} }")])));
        var workflow = new FieldExtractionWorkflow(
            chatClient,
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())),
            TestFieldTypeRegistry.Default,
            Options.Create(new VaultExtractBehaviorOptions()));

        await workflow.ExtractAsync(StatementFields(), "# doc");

        var schema = ((ChatResponseFormatJson)options!.ResponseFormat!).Schema!.Value;
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .ShouldBe(["values", "validationWarnings", "sources"]);
        schema.GetProperty("properties").GetProperty("values").GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).Last().ShouldBe("transactions");

        var schemaMessage = sent![1].Text;
        schemaMessage.IndexOf("\"transactions\"", StringComparison.Ordinal)
            .ShouldBeGreaterThan(schemaMessage.IndexOf("\"closing_balance\"", StringComparison.Ordinal));
        schemaMessage.IndexOf("\"transactions\"", StringComparison.Ordinal)
            .ShouldBeGreaterThan(schemaMessage.IndexOf("\"bank_name\"", StringComparison.Ordinal));
    }

    /// <summary>A table has no row limit, so a schema with one asks for all the output the host allows.</summary>
    [Fact]
    public async Task MaxOutputTokens_for_a_table_is_the_hosts_ceiling()
    {
        var ceiling = new VaultExtractBehaviorOptions { MaxFieldExtractionOutputTokens = 163_840 }; // DeepSeek-V3 on SiliconFlow

        var (scalarWorkflow, scalarOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "closing_balance": 1 }, "validationWarnings": [] }""", behaviorOptions: ceiling);
        await scalarWorkflow.ExtractAsync(new[] { Field("closing_balance", "Number") }, "# doc");

        var (tableWorkflow, tableOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "closing_balance": 1, "transactions": [] }, "validationWarnings": [] }""", behaviorOptions: ceiling);
        await tableWorkflow.ExtractAsync(
            new[] { Field("closing_balance", "Number"), Field("transactions", TableFieldType.ControlName, TransactionColumns) },
            "# doc");

        scalarOptions()!.MaxOutputTokens!.Value.ShouldBeLessThan(163_840);
        tableOptions()!.MaxOutputTokens.ShouldBe(163_840);
    }

    /// <summary>
    /// A DateTime field is named by its input mode in the schema message: labelled "DateTime", a date field was answered
    /// as "2026-11-20T00:00:00", which the date reader rejects (a provider need not enforce the schema's pattern).
    /// </summary>
    [Fact]
    public async Task The_schema_message_names_a_DateTime_field_by_its_input_mode()
    {
        var chatClient = Substitute.For<IChatClient>();
        List<ChatMessage>? sent = null;
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => sent = messages.ToList()),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "{ \"values\": {}, \"validationWarnings\": [] }")])));
        var workflow = new FieldExtractionWorkflow(
            chatClient,
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())),
            TestFieldTypeRegistry.Default,
            Options.Create(new VaultExtractBehaviorOptions()));

        await workflow.ExtractAsync(
            new[]
            {
                DateField("issue_date"),
                DateTimeField("received_at"),
                Field("billing_month", DateTimeFieldType.ControlName,
                    new DateTimeConfiguration { InputMode = DateTimeInputMode.Month }.ConfigurationDictionary),
                Field("amount", "Number")
            },
            "# doc");

        var schemaMessage = sent![1].Text;
        schemaMessage.ShouldContain("\"issue_date\" (Date, optional)");
        schemaMessage.ShouldContain("\"received_at\" (DateTime, optional)");
        schemaMessage.ShouldContain("\"billing_month\" (Month, optional)");
        schemaMessage.ShouldContain("\"amount\" (Number, optional)");
    }

    // ─── sources: the passages the model quotes as where it found each value ───

    [Fact]
    public async Task Sources_are_kept_per_declared_field_within_their_bounds()
    {
        var longPassage = new string('長', FieldExtractionWorkflow.MaxSourceLength + 50);
        var workflow = CreateWorkflow($$"""
            {
              "values": { "amount": 12000, "vendor": "株式会社サンプル", "memo": null },
              "sources": {
                "amount": ["合計 12,000円", "  ", 7, "{{longPassage}}", "小計 12,000円", "4つ目"],
                "vendor": [],
                "memo": ["何か"],
                "undeclared": ["x"]
              },
              "validationWarnings": []
            }
            """);

        var result = await workflow.ExtractAsync(
            new[] { Field("amount", "Number"), Field("vendor", "Text"), Field("memo", "Text") }, "# doc");

        result.Sources.Keys.ShouldBe(["amount", "memo"], ignoreOrder: true);
        result.Sources["amount"].ShouldBe(["合計 12,000円", longPassage[..FieldExtractionWorkflow.MaxSourceLength], "小計 12,000円"]);
    }

    [Fact]
    public async Task A_response_without_sources_still_yields_its_values()
    {
        var workflow = CreateWorkflow("""{ "values": { "amount": 1 }, "validationWarnings": [] }""");

        var result = await workflow.ExtractAsync(new[] { Field("amount", "Number") }, "# doc");

        result.Values["amount"]!.Value.GetDecimal().ShouldBe(1m);
        result.Sources.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_schema_asks_for_bounded_sources_for_every_field()
    {
        var (workflow, capturedOptions) = CreateWorkflowCapturingOptions(
            """{ "values": { "amount": 1, "vendor": "x" }, "sources": {}, "validationWarnings": [] }""");

        await workflow.ExtractAsync(new[] { Field("amount", "Number"), Field("vendor", "Text") }, "# doc");

        var schema = ((ChatResponseFormatJson)capturedOptions()!.ResponseFormat!).Schema!.Value;
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("sources");
        var sources = schema.GetProperty("properties").GetProperty("sources");
        sources.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["amount", "vendor"]);
        var amount = sources.GetProperty("properties").GetProperty("amount");
        amount.GetProperty("maxItems").GetInt32().ShouldBe(FieldExtractionWorkflow.MaxSourcesPerField);
        amount.GetProperty("items").GetProperty("maxLength").GetInt32().ShouldBe(FieldExtractionWorkflow.MaxSourceLength);
    }
}

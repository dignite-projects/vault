using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields.Number;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class FieldExtractionServiceTestModule : AbpModule
{
    /// <summary>#491: a small ceiling keeps the oversized-body test from allocating a 200k-char string.</summary>
    public const int MarkdownCeiling = 64;

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldRepository>());

        Configure<VaultExtractBehaviorOptions>(options =>
        {
            options.MaxFieldExtractionMarkdownLength = MarkdownCeiling;
        });

        var workflow = Substitute.ForPartsOf<FieldExtractionWorkflow>(
            Substitute.For<IChatClient>(),
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())),
            TestFieldTypeRegistry.Default,
            Options.Create(new VaultExtractBehaviorOptions()));
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// Contract tests for the bulk entry point of <see cref="FieldExtractionService"/>
/// (<c>expectedEventTypeCode = null</c>) (#289 step 1).
/// Cascade-path behavior (with the stale-reclassify hint) is guarded by <see cref="FieldExtractionCascade_Tests"/>;
/// this class focuses on bulk / single-document re-extraction semantics when calling the engine directly.
/// </summary>
public class FieldExtractionService_Tests
    : VaultExtractApplicationTestBase<FieldExtractionServiceTestModule>
{
    private readonly FieldExtractionService _service;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly FieldExtractionWorkflow _workflow;

    public FieldExtractionService_Tests()
    {
        _service = GetRequiredService<FieldExtractionService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _workflow = GetRequiredService<FieldExtractionWorkflow>();
    }

    [Fact]
    public async Task Batch_Path_Extracts_Against_Current_Type()
    {
        var doc = CreateClassifiedDocument(typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateField("contract.general", "amount", NumberFieldType.ControlName) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Extracted);
        result.FieldCount.ShouldBe(1);
        doc.FlexFields["amount"].ShouldBe(1500m);
    }

    [Fact]
    public async Task Batch_Path_With_No_Definitions_Clears_And_Returns_Cleared()
    {
        var doc = CreateClassifiedDocument(typeCode: "blank.type");
        doc.SetFlexFields(new Dictionary<string, object?> { ["old"] = 9m });
        SetupType("blank.type");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<Field>());

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Cleared);
        doc.FlexFields.ShouldBeEmpty();
    }

    [Fact]
    public async Task Batch_Path_On_Unclassified_Document_Returns_Skipped()
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            new FileOrigin("blobs/x.pdf", "u", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1, "x.pdf"));
        // No DocumentTypeId (unclassified).
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Skipped);
    }

    /// <summary>
    /// #491: the core gate. A body over MaxFieldExtractionMarkdownLength must not reach the LLM at all — no call —
    /// and must leave a blocking review reason behind. The outcome is terminal (Declined, not an
    /// exception), because throwing would hand the job back to the ABP job store, which would re-send the same
    /// oversized body on every retry.
    /// </summary>
    [Fact]
    public async Task Batch_Path_Over_Ceiling_Declines_Without_Calling_The_Llm()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling + 1));
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateField("contract.general", "amount", NumberFieldType.ControlName) });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Declined);
        await _workflow.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeTrue();
        ReviewReasonPolicy.HasBlocking(doc.ReviewReasons).ShouldBeTrue();
    }

    /// <summary>
    /// #491: declining must not destroy data. A host lowering the ceiling below an already-extracted document's length
    /// re-runs extraction (#289) and hits the gate; the values a previous in-budget run legitimately produced stay put.
    /// </summary>
    [Fact]
    public async Task Batch_Path_Over_Ceiling_Preserves_Previously_Extracted_Values()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling + 1));
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = 1500m });
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateField("contract.general", "amount", NumberFieldType.ControlName) });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Declined);
        doc.FlexFields["amount"].ShouldBe(1500m);
    }

    /// <summary>
    /// #491: a body exactly at the ceiling is in budget — the gate is <c>&gt;</c>, not <c>&gt;=</c>. Guards against an
    /// off-by-one that would decline the largest legitimately extractable document.
    /// </summary>
    [Fact]
    public async Task Batch_Path_Exactly_At_Ceiling_Still_Extracts()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling));
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateField("contract.general", "amount", NumberFieldType.ControlName) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Extracted);
    }

    /// <summary>
    /// #491: the decline write carries the same stale-reclassify guard as the extraction write. If the document was
    /// reclassified between the phase-1 capture and the decline, the run is stale and must pin nothing — otherwise a
    /// stale run would leave a blocking reason on a document that has since moved to a type it may well fit.
    /// </summary>
    [Fact]
    public async Task Decline_On_A_Document_Reclassified_In_Flight_Pins_Nothing()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling + 1));
        SetupType("contract.general");
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateField("contract.general", "amount", NumberFieldType.ControlName) });

        // Phase 1 sees contract.general; by the time the decline reloads, an operator has reclassified the document.
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => doc, _ =>
            {
                Invoke(doc, "ApplyAutomaticClassificationResult", TypeId("invoice.general"), 0.99);
                return doc;
            });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Skipped);
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    /// <summary>
    /// #491: once a run actually reaches the LLM — the host raised the ceiling — the blocking signal from the earlier
    /// declined run must be cleared, or the document would stay short of Ready forever.
    /// </summary>
    [Fact]
    public async Task Successful_Extraction_Clears_A_Previous_Decline()
    {
        var doc = CreateClassifiedDocument(typeCode: "contract.general");
        doc.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateField("contract.general", "amount", NumberFieldType.ControlName) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    /// <summary>
    /// #491: reclassifying a declined document to a type that declares no fields must clear the blocking signal — that
    /// type issues no LLM call, so nothing is "incomplete" and the operator has no action left to take.
    /// </summary>
    [Fact]
    public async Task Clearing_Path_For_A_Type_Without_Fields_Clears_A_Previous_Decline()
    {
        var doc = CreateClassifiedDocument(typeCode: "blank.type");
        doc.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        SetupType("blank.type");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<Field>());

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Cleared);
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    [Fact]
    public async Task Extraction_records_where_each_written_value_was_quoted_from()
    {
        const string markdown = "# 請求書\n\n合計 1,500 円";
        var doc = ArrangeAmountAndMemo(markdown);
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FieldExtractionWorkflowResult(
                new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement, ["memo"] = null },
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["amount"] = ["合計 1,500 円", "どこにもない"],
                    ["memo"] = ["請求書"]
                }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FieldSources.Keys.ShouldBe(["amount"]);
        doc.FieldSources["amount"].ShouldBe([new DocumentTextSpan(markdown.IndexOf("合計", StringComparison.Ordinal), 10)]);
    }

    [Fact]
    public async Task Sources_are_not_recorded_against_Markdown_corrected_while_the_model_ran()
    {
        var doc = ArrangeAmountAndMemo("# 請求書\n\n合計 1,500 円");
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                doc.CorrectMarkdown("# 請求書\n\n小計 1,000 円\n\n合計 1,500 円");
                return new FieldExtractionWorkflowResult(
                    new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement },
                    new Dictionary<string, IReadOnlyList<string>> { ["amount"] = ["合計 1,500 円"] });
            });

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FlexFields["amount"].ShouldBe(1500m);
        doc.FieldSources.ShouldBeEmpty();
    }

    // ─── an answer cut off at the output limit ───

    private const string CutOffMarkdown = "# 請求書\n\n合計 1,500 円\n\nメモ 前回の記載";

    private static FieldExtractionWorkflowResult CutOffAnswer(
        Dictionary<string, IReadOnlyList<string>>? sources = null) =>
        new(
            new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement, ["memo"] = null },
            sources)
        {
            UnfinishedFields = new HashSet<string> { "memo" }
        };

    [Fact]
    public async Task A_cut_off_answer_writes_its_finished_fields_keeps_the_rest_and_holds_the_document_from_Ready()
    {
        var doc = ArrangeAmountAndMemo(CutOffMarkdown);
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = 900m, ["memo"] = "前回の記載" });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CutOffAnswer());

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Incomplete);
        doc.FlexFields["amount"].ShouldBe(1500m);
        doc.FlexFields["memo"].ShouldBe("前回の記載");
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeTrue();
    }

    [Fact]
    public async Task A_field_a_cut_off_answer_did_not_finish_keeps_its_sources()
    {
        var doc = ArrangeAmountAndMemo(CutOffMarkdown);
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = 900m, ["memo"] = "前回の記載" });
        var memoSpan = new DocumentTextSpan(CutOffMarkdown.IndexOf("メモ", StringComparison.Ordinal), 8);
        doc.SetFieldSources(new Dictionary<string, IReadOnlyList<DocumentTextSpan>> { ["memo"] = [memoSpan] });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CutOffAnswer(sources: new Dictionary<string, IReadOnlyList<string>>
            {
                ["amount"] = ["合計 1,500 円"],
                ["memo"] = ["請求書"]  // quoted before the cut, for a value the document does not get
            }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FieldSources["memo"].ShouldBe([memoSpan]);
        doc.FieldSources["amount"].ShouldBe([new DocumentTextSpan(CutOffMarkdown.IndexOf("合計", StringComparison.Ordinal), 10)]);
    }

    [Fact]
    public async Task A_source_quoted_for_a_field_the_cut_off_answer_did_not_finish_is_not_recorded()
    {
        var doc = ArrangeAmountAndMemo(CutOffMarkdown);
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = 900m, ["memo"] = "前回の記載" });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CutOffAnswer(sources: new Dictionary<string, IReadOnlyList<string>> { ["memo"] = ["請求書"] }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FlexFields["memo"].ShouldBe("前回の記載");
        doc.FieldSources.ContainsKey("memo").ShouldBeFalse();
    }

    [Fact]
    public async Task The_rules_judge_the_value_a_cut_off_answer_kept_for_an_unfinished_field()
    {
        var doc = ArrangeAmountAndMemo(
            CutOffMarkdown,
            ("memo", Rule("メモは 3 文字未満", "value.length < 3")));
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = 900m, ["memo"] = "前回の記載" });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CutOffAnswer());

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FlexFields["memo"].ShouldBe("前回の記載");
        doc.FieldValidationWarnings.Single().FieldDefinitionId.ShouldBe(FieldId("memo"));
        doc.FieldValidationWarnings.Single().Message.ShouldBe("メモは 3 文字未満");
    }

    // ─── validation rules ───

    [Fact]
    public async Task A_value_that_breaks_its_fields_rule_is_kept_and_warned_with_the_rules_description()
    {
        var doc = ArrangeAmountAndMemo(
            CutOffMarkdown,
            ("amount", Rule("金額は 1,000 以下", "value <= 1000")));
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FlexFields["amount"].ShouldBe(1500m);
        doc.FieldValidationWarnings.Single().FieldDefinitionId.ShouldBe(FieldId("amount"));
        doc.FieldValidationWarnings.Single().Message.ShouldBe("金額は 1,000 以下");
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldValidationWarning).ShouldBeTrue();
    }

    [Fact]
    public async Task A_rule_reads_another_field_by_its_input_and_says_where_it_fails()
    {
        var doc = ArrangeAmountAndMemo(
            CutOffMarkdown,
            ("memo", Rule("メモに金額を書く", "value.includes(String(amount)) || [false, `金額 ${amount} がない`]", ("amount", "amount"))));
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement,
                ["memo"] = JsonDocument.Parse("\"前回の記載\"").RootElement
            }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FieldValidationWarnings.Single().FieldDefinitionId.ShouldBe(FieldId("memo"));
        doc.FieldValidationWarnings.Single().Message.ShouldContain("メモに金額を書く");
        doc.FieldValidationWarnings.Single().Message.ShouldContain("金額 1500 がない");
    }

    [Fact]
    public async Task An_extraction_whose_values_pass_their_rules_clears_the_warnings_the_document_had()
    {
        var doc = ArrangeAmountAndMemo(
            CutOffMarkdown,
            ("amount", Rule("金額は 1,000 以下", "value <= 1000")));
        doc.ReplaceFieldValidationWarnings([new FieldValidationWarning(FieldId("amount"), "previous amount warning")]);
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("900").RootElement }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FieldValidationWarnings.ShouldBeEmpty();
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldValidationWarning).ShouldBeFalse();
    }

    [Fact]
    public async Task A_rule_whose_value_is_absent_is_skipped()
    {
        var doc = ArrangeAmountAndMemo(
            CutOffMarkdown,
            ("memo", Rule("メモは空でない", "value.length > 0")));
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("900").RootElement, ["memo"] = null }));

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.FieldValidationWarnings.ShouldBeEmpty();
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private Document ArrangeAmountAndMemo(string markdown, params (string Field, FieldValidationRule Rule)[] rules)
    {
        var doc = CreateClassifiedDocument(typeCode: "invoice.sources", markdown: markdown);
        SetupType("invoice.sources");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        var fields = new List<Field>
        {
            CreateField("invoice.sources", "amount", NumberFieldType.ControlName),
            CreateField("invoice.sources", "memo", "Text")
        };
        foreach (var field in fields)
        {
            field.SetValidationRules(rules.Where(rule => rule.Field == field.Name).Select(rule => rule.Rule));
        }

        _fieldRepository.GetListAsync(TypeId("invoice.sources"), Arg.Any<CancellationToken>()).Returns(fields);
        return doc;
    }

    private static FieldValidationRule Rule(string description, string expression, params (string Alias, string FieldName)[] inputs) =>
        new(description, expression, inputs.Select(input => new FieldValidationRuleInput(input.Alias, FieldId(input.FieldName))).ToList());

    private static FieldExtractionWorkflowResult WorkflowResult(Dictionary<string, JsonElement?> values) => new(values);

    private void SetupType(string code, Guid? typeId = null)
    {
        var id = typeId ?? TypeId(code);
        var type = new DocumentType(id, tenantId: null, typeCode: code, displayName: code);
        _documentTypeRepository.FindByTypeCodeAsync(code, Arg.Any<CancellationToken>()).Returns(type);
        _documentTypeRepository.FindAsync(id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(type);
    }

    private static Document CreateClassifiedDocument(string typeCode, string markdown = "# Body")
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            new FileOrigin($"blobs/{Guid.NewGuid():N}.pdf", "u", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1, "x.pdf"));
        // Application.Tests has no InternalsVisibleTo; same as FieldExtractionCascade_Tests, use reflection
        // through the internal channel.
        Invoke(doc, "ApplyAutomaticClassificationResult", TypeId(typeCode), 0.99);
        Invoke(doc, "SetMarkdown", markdown);
        return doc;
    }

    private static void Invoke(Document doc, string method, params object[] args) =>
        typeof(Document)
            .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, args);

    private static Field CreateField(string typeCode, string name, string fieldTypeName) =>
        new(FieldId(name), tenantId: null, documentTypeId: TypeId(typeCode), name: name, displayName: name,
            fieldTypeName: fieldTypeName, description: $"Extract the {name}.");

    private static Guid TypeId(string code) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + code)));
    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
}

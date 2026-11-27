using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Table;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Documents.Fields.Validation;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// <see cref="FieldValidationRuleEvaluator"/>: rules decided in code over the values a document stores, in the shape the
/// egress carries, inside a sandbox an administrator's expression cannot leave.
/// </summary>
public class FieldValidationRuleEvaluator_Tests : VaultExtractApplicationTestBase<VaultExtractApplicationTestModule>
{
    private static readonly Guid TypeId = Guid.NewGuid();
    private readonly FieldValidationRuleEvaluator _evaluator;

    public FieldValidationRuleEvaluator_Tests()
    {
        _evaluator = GetRequiredService<FieldValidationRuleEvaluator>();
    }

    // ─── what a rule decides ───

    [Fact]
    public void A_rule_that_holds_draws_no_warning_and_one_that_does_not_is_worded_with_its_description()
    {
        var amount = Number("amount", Rule("金額は 1,000 以下", "value <= 1000"));

        Warnings(Doc(amount, 900m), amount).ShouldBeEmpty();
        var warning = Warnings(Doc(amount, 1500m), amount).Single();
        warning.FieldDefinitionId.ShouldBe(amount.Id);
        warning.Message.ShouldBe("金額は 1,000 以下");
    }

    [Fact]
    public void The_detail_a_failing_rule_returns_is_added_to_its_description()
    {
        var amount = Number("amount", Rule("金額は 1,000 以下", "value <= 1000 || [false, `${value} は上限を超える`]"));

        var message = Warnings(Doc(amount, 1500m), amount).Single().Message;

        message.ShouldContain("金額は 1,000 以下");
        message.ShouldContain("1500 は上限を超える");
    }

    [Fact]
    public void A_rule_returning_true_in_the_array_form_holds()
    {
        var amount = Number("amount", Rule("金額は正", "[value > 0, 'never shown']"));

        Warnings(Doc(amount, 5m), amount).ShouldBeEmpty();
    }

    [Fact]
    public void A_rule_whose_value_or_input_is_absent_is_skipped()
    {
        var total = Number("total");
        var amount = Number("amount", Rule("合計以下", "value <= total", ("total", total)));

        Warnings(DocWith((amount, null), (total, 10m)), amount, total).ShouldBeEmpty();
        Warnings(DocWith((amount, 20m), (total, null)), amount, total).ShouldBeEmpty();
        Warnings(DocWith((amount, 20m), (total, 10m)), amount, total).Single().FieldDefinitionId.ShouldBe(amount.Id);
    }

    [Fact]
    public void An_input_is_read_under_its_alias_and_bound_by_the_field_id_so_a_rename_leaves_it_working()
    {
        var opening = Number("openingBalance");
        var rows = Table("transactions", Rule("第一行余额等于期初余额", "value[0].values.balance === opening", ("opening", opening)));

        opening.SetName("startBalance");
        var doc = DocWith((rows, Rows((1, 100m), (2, 150m))), (opening, 100m));

        Warnings(doc, rows, opening).ShouldBeEmpty();
        doc.SetFlexFields(new Dictionary<string, object?> { ["transactions"] = doc.FlexFields["transactions"], ["startBalance"] = 99m });
        Warnings(doc, rows, opening).Single().FieldDefinitionId.ShouldBe(rows.Id);
    }

    [Fact]
    public void A_rule_reading_a_field_that_is_no_longer_defined_is_skipped()
    {
        var removed = Number("removed");
        var amount = Number("amount", Rule("x", "false", ("r", removed)));
        var doc = DocWith((amount, 1m), (removed, 1m));

        Warnings(doc, amount).ShouldBeEmpty();
    }

    [Fact]
    public void Several_failed_rules_of_one_field_share_one_warning_one_line_each()
    {
        var amount = Number("amount", Rule("一つ目", "false"), Rule("通る", "true"), Rule("二つ目", "value < 0"));

        Warnings(Doc(amount, 5m), amount).Single().Message.ShouldBe("一つ目\n二つ目");
    }

    [Fact]
    public void A_warning_is_held_to_the_message_ceiling()
    {
        var description = new string('長', FieldValidationRuleConsts.MaxDescriptionLength);
        var amount = Number("amount", Rule(description, "false"), Rule(description, "false"), Rule(description, "false"));

        Warnings(Doc(amount, 5m), amount).Single().Message.Length.ShouldBe(DocumentFieldValidationWarningConsts.MaxMessageLength);
    }

    [Fact]
    public void Only_the_named_fields_rules_are_evaluated_when_fields_are_named()
    {
        var amount = Number("amount", Rule("a", "false"));
        var total = Number("total", Rule("t", "false"));
        var doc = DocWith((amount, 1m), (total, 1m));

        _evaluator.Evaluate(doc, [amount, total], [total.Id]).Single().FieldDefinitionId.ShouldBe(total.Id);
    }

    [Fact]
    public void The_fields_reading_a_changed_value_are_its_own_and_those_whose_inputs_read_it()
    {
        var amount = Number("amount", Rule("a", "true"));
        var memo = Text("memo", Rule("m", "true", ("a", amount)));
        var note = Text("note");
        var other = Number("other", Rule("o", "true"));

        _evaluator.FieldsReading([amount, memo, note, other], [amount.Id]).ShouldBe([amount.Id, memo.Id], ignoreOrder: true);
        _evaluator.FieldsReading([amount, memo, note, other], [note.Id]).ShouldBeEmpty();
    }

    // ─── the values a rule sees: the egress shape ───

    [Fact]
    public void A_rule_sees_numbers_as_numbers_dates_as_iso_strings_and_table_rows_under_values()
    {
        var date = Date("statementEndDate");
        var rows = Table("transactions",
            Rule("末行の日付は期末日", "value.at(-1).values.date === end", ("end", date)),
            Rule("残高は数値", "value.every(r => typeof r.values.balance === 'number')"));
        var doc = DocWith((rows, Rows((1, 100m), (27, 239373m))), (date, new DateTime(2025, 8, 27)));

        Warnings(doc, rows, date).ShouldBeEmpty();
    }

    [Fact]
    public void The_bank_statement_checks_hold_on_correct_rows_and_catch_a_misread_last_balance()
    {
        var opening = Number("openingBalance");
        var closing = Number("closingBalance");
        var rows = Table("transactions",
            Rule("第一行余额等于期初余额", "value[0].values.balance === opening", ("opening", opening)),
            Rule("最后一行余额等于期末余额", "value.at(-1).values.balance === closing", ("closing", closing)),
            Rule("交易日期不递减", "value.every((r, i) => i === 0 || value[i - 1].values.date <= r.values.date)"));

        Warnings(DocWith((rows, Rows((1, 100000m), (5, 120000m), (27, 239373m))), (opening, 100000m), (closing, 239373m)), rows, opening, closing)
            .ShouldBeEmpty();
        Warnings(DocWith((rows, Rows((1, 100000m), (5, 120000m), (27, 239337m))), (opening, 100000m), (closing, 239373m)), rows, opening, closing)
            .Single().Message.ShouldBe("最后一行余额等于期末余额");
    }

    // ─── a rule that cannot be evaluated counts as failing, and the sandbox holds ───

    [Theory]
    [InlineData("value.missing.deeper")]                                       // throws a TypeError
    [InlineData("42")]                                                         // neither true, false nor [boolean, detail]
    [InlineData("(() => { while (true) {} })()")]                              // never ends
    [InlineData("(() => { const a = []; while (true) a.push('x'.repeat(100000)); })()")] // eats memory
    [InlineData("(function f() { return f(); })()")]                          // recurses without end
    public void A_rule_that_cannot_be_evaluated_warns_without_harming_the_host(string expression)
    {
        var amount = Number("amount", Rule("規則", expression));
        var watch = Stopwatch.StartNew();

        var warning = Warnings(Doc(amount, 5m), amount).Single();

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        warning.Message.ShouldStartWith("規則");
        warning.Message.Length.ShouldBeGreaterThan("規則".Length);
    }

    [Fact]
    public void A_rule_reaches_no_host_object()
    {
        var amount = Number("amount", Rule("隔離",
            "typeof System === 'undefined' && typeof importNamespace === 'undefined' && typeof require === 'undefined' && typeof fetch === 'undefined'"));

        Warnings(Doc(amount, 5m), amount).ShouldBeEmpty();
    }

    [Fact]
    public void Each_rule_runs_in_an_engine_of_its_own()
    {
        var amount = Number("amount", Rule("書く", "(globalThis.leaked = 1, true)"), Rule("読む", "typeof leaked === 'undefined'"));

        Warnings(Doc(amount, 5m), amount).ShouldBeEmpty();
    }

    // ─── checking an expression before it is saved ───

    [Theory]
    [InlineData("value > 0")]
    [InlineData("value > 0 // a trailing comment")]
    [InlineData("(() => { let n = 0; for (const r of value) n += r; return n > 0; })()")]
    public void An_expression_that_parses_is_accepted(string expression)
        => Should.NotThrow(() => _evaluator.CheckExpression("amount", 1, expression));

    [Theory]
    [InlineData("value >")]
    [InlineData("value > 0; value < 9")]   // a statement list, not one expression
    [InlineData("var x = 1")]
    public void An_expression_that_does_not_parse_as_one_expression_is_refused(string expression)
    {
        var ex = Should.Throw<BusinessException>(() => _evaluator.CheckExpression("amount", 2, expression));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.UnparsableValidationRule);
        ex.Data["Index"].ShouldBe(2);
    }

    // ─── the shape the aggregate requires ───

    public static TheoryData<string, FieldValidationRule[], string> Malformed()
    {
        var other = Guid.NewGuid();
        const string tooMany = VaultExtractErrorCodes.FieldDefinition.TooManyValidationRules;
        const string text = VaultExtractErrorCodes.FieldDefinition.InvalidValidationRule;
        const string input = VaultExtractErrorCodes.FieldDefinition.InvalidValidationRuleInput;
        return new TheoryData<string, FieldValidationRule[], string>
        {
            { "too many rules", Enumerable.Range(0, FieldValidationRuleConsts.MaxRulesPerField + 1).Select(_ => Rule("r", "true")).ToArray(), tooMany },
            { "blank description", [Rule(" ", "true")], text },
            { "two-line description", [Rule("a\nb", "true")], text },
            { "long description", [Rule(new string('a', FieldValidationRuleConsts.MaxDescriptionLength + 1), "true")], text },
            { "blank expression", [Rule("r", " ")], text },
            { "long expression", [Rule("r", new string('1', FieldValidationRuleConsts.MaxExpressionLength + 1))], text },
            { "too many inputs", [new FieldValidationRule("r", "true", Enumerable.Range(0, FieldValidationRuleConsts.MaxInputsPerRule + 1).Select(i => new FieldValidationRuleInput($"a{i}", Guid.NewGuid())).ToList())], input },
            { "alias not an identifier", [new FieldValidationRule("r", "true", [new FieldValidationRuleInput("1x", other)])], input },
            { "alias is value", [new FieldValidationRule("r", "true", [new FieldValidationRuleInput("value", other)])], input },
            { "alias twice", [new FieldValidationRule("r", "true", [new FieldValidationRuleInput("a", other), new FieldValidationRuleInput("a", Guid.NewGuid())])], input },
            { "same field twice", [new FieldValidationRule("r", "true", [new FieldValidationRuleInput("a", other), new FieldValidationRuleInput("b", other)])], input },
            { "empty field id", [new FieldValidationRule("r", "true", [new FieldValidationRuleInput("a", Guid.Empty)])], input },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_rule_is_refused_by_the_field(string because, FieldValidationRule[] rules, string code)
    {
        var field = Number("amount");

        var ex = Should.Throw<BusinessException>(() => field.SetValidationRules(rules), because);

        ex.Code.ShouldBe(code, because);
        field.ValidationRules.ShouldBeEmpty();
    }

    [Fact]
    public void A_rule_reading_its_own_field_as_an_input_is_refused()
    {
        var field = Number("amount");

        Should.Throw<BusinessException>(() => field.SetValidationRules(
                [new FieldValidationRule("r", "true", [new FieldValidationRuleInput("self", field.Id)])]))
            .Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.InvalidValidationRuleInput);
    }

    // ─── helpers ───

    private IReadOnlyList<FieldValidationWarning> Warnings(Document document, params Field[] definitions)
        => _evaluator.Evaluate(document, definitions);

    private static FieldValidationRule Rule(string description, string expression, params (string Alias, Field Field)[] inputs)
        => new(description, expression, inputs.Select(input => new FieldValidationRuleInput(input.Alias, input.Field.Id)).ToList());

    private static Field Number(string name, params FieldValidationRule[] rules)
        => WithRules(new Field(Guid.NewGuid(), null, TypeId, name, name, NumberFieldType.ControlName), rules);

    private static Field Text(string name, params FieldValidationRule[] rules)
        => WithRules(new Field(Guid.NewGuid(), null, TypeId, name, name, TextFieldType.ControlName,
            configuration: new TextConfiguration { Mode = TextMode.SingleLine }.ConfigurationDictionary), rules);

    private static Field Date(string name, params FieldValidationRule[] rules)
        => WithRules(new Field(Guid.NewGuid(), null, TypeId, name, name, DateTimeFieldType.ControlName,
            configuration: new DateTimeConfiguration { InputMode = DateTimeInputMode.Date }.ConfigurationDictionary), rules);

    private static readonly FieldConfigurationDictionary TransactionColumns = new TableConfiguration
    {
        Columns =
        [
            new InlineFieldDefinition
            {
                Name = "date", DisplayName = "Date", FieldTypeName = DateTimeFieldType.ControlName,
                Configuration = new DateTimeConfiguration { InputMode = DateTimeInputMode.Date }.ConfigurationDictionary
            },
            new InlineFieldDefinition { Name = "balance", DisplayName = "Balance", FieldTypeName = NumberFieldType.ControlName }
        ]
    }.ConfigurationDictionary;

    private static Field Table(string name, params FieldValidationRule[] rules)
        => WithRules(new Field(Guid.NewGuid(), null, TypeId, name, name, TableFieldType.ControlName, configuration: TransactionColumns), rules);

    private static Field WithRules(Field field, FieldValidationRule[] rules)
    {
        field.SetValidationRules(rules);
        return field;
    }

    /// <summary>Table rows on August 2025 days, read through the same reader the extraction stores values with.</summary>
    private static object Rows(params (int Day, decimal Balance)[] rows)
    {
        var json = JsonSerializer.Serialize(rows.Select(row => new
        {
            values = new { date = $"2025-08-{row.Day:00}", balance = row.Balance }
        }));
        FlexFieldValueReader.TryRead(
            JsonDocument.Parse(json).RootElement, TableFieldType.ControlName, TransactionColumns, TestFieldTypeRegistry.Default, out var read)
            .ShouldBeTrue();
        return read!;
    }

    private static Document Doc(Field field, object? value) => DocWith((field, value));

    private static Document DocWith(params (Field Field, object? Value)[] values)
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            new FileOrigin($"blobs/{Guid.NewGuid():N}.pdf", "u", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1, "x.pdf"));
        doc.SetFlexFields(values.Where(v => v.Value != null).ToDictionary(v => v.Field.Name, v => v.Value));
        return doc;
    }
}

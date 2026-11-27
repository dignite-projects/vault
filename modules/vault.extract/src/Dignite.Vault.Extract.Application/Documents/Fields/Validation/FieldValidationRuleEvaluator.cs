using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Acornima.Ast;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.FlexFields;
using Dignite.Vault.Extract.Localization;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Fields.Validation;

/// <summary>
/// Evaluates <see cref="Field.ValidationRules"/> over a document's stored values and turns the rules that do not hold into
/// the field validation warnings the document carries. Deterministic: no model is asked, so a correct value never draws a
/// warning and a broken rule always does (the reason this replaced the extraction model's own warnings, #527).
/// <para>
/// A rule's expression is JavaScript, run by Jint in a fresh, sandboxed engine per rule: strict mode, a statement budget,
/// a timeout, a memory limit and a recursion limit, and no .NET type reachable from the script. The values go in as JSON
/// text parsed inside the engine, in the shape the <c>ExtractedFields</c> egress carries (<see cref="FlexFieldValueJsonWriter"/>).
/// Rules are written by administrators of every tenant, so these limits are a security boundary and are constants, not
/// host configuration. The same shape as Extend's validation code blocks, which also return <c>true</c>, <c>false</c> or
/// <c>[false, "reason"]</c>.
/// </para>
/// </summary>
public class FieldValidationRuleEvaluator : ITransientDependency
{
    /// <summary>Statements one rule may execute: a few per row of a long table, far from what a loop that never ends reaches.</summary>
    public const int MaxStatements = 200_000;

    /// <summary>Wall-clock time one rule may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Memory one rule's engine may allocate, values included.</summary>
    public const long MemoryLimitBytes = 16_000_000;

    /// <summary>Call depth one rule may reach.</summary>
    public const int MaxRecursionDepth = 64;

    /// <summary>Characters kept of the detail a failing rule returns, or of the reason it could not be evaluated.</summary>
    public const int MaxDetailLength = 200;

    private readonly IVaultExtractFieldTypeRegistry _registry;
    private readonly IStringLocalizer<VaultExtractResource> _localizer;
    private readonly ILogger<FieldValidationRuleEvaluator> _logger;

    public FieldValidationRuleEvaluator(
        IVaultExtractFieldTypeRegistry registry,
        IStringLocalizer<VaultExtractResource> localizer,
        ILogger<FieldValidationRuleEvaluator> logger)
    {
        _registry = registry;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>
    /// The warnings <paramref name="document"/>'s values earn under the rules of <paramref name="definitions"/>: one per
    /// field with a rule that does not hold or could not be evaluated, none for a field whose rules all hold or were
    /// skipped. <paramref name="fieldIds"/> narrows it to those fields' rules; the other fields are not looked at.
    /// </summary>
    /// <param name="document">The document whose stored values are judged.</param>
    /// <param name="definitions">The active fields of the document's type: the rules, and the fields their inputs name.</param>
    /// <param name="fieldIds">When given, only these fields' rules are evaluated.</param>
    public virtual IReadOnlyList<FieldValidationWarning> Evaluate(
        Document document,
        IReadOnlyList<Field> definitions,
        IReadOnlyCollection<Guid>? fieldIds = null)
    {
        var values = Values(document, definitions);
        var warnings = new List<FieldValidationWarning>();
        foreach (var field in definitions)
        {
            if (field.ValidationRules.Count == 0 || (fieldIds != null && !fieldIds.Contains(field.Id)))
            {
                continue;
            }

            var lines = new List<string>();
            foreach (var rule in field.ValidationRules)
            {
                var result = EvaluateRule(rule, values.GetValueOrDefault(field.Id), id => values.GetValueOrDefault(id));
                if (result.Outcome == FieldValidationRuleOutcome.NotEvaluated)
                {
                    _logger.LogWarning(
                        "Validation rule '{Description}' of field {FieldName} ({FieldId}) could not be evaluated on document {DocumentId}: {Reason}",
                        rule.Description, field.Name, field.Id, document.Id, result.Detail);
                }

                if (MessageLine(rule, result) is { } line)
                {
                    lines.Add(line);
                }
            }

            if (lines.Count > 0)
            {
                warnings.Add(new FieldValidationWarning(
                    field.Id,
                    TextTruncator.AtCharBoundary(string.Join("\n", lines), DocumentFieldValidationWarningConsts.MaxMessageLength)));
            }
        }

        return warnings;
    }

    /// <summary>
    /// <paramref name="rules"/> over the stored values of <paramref name="document"/> as rules of the field
    /// <paramref name="fieldId"/>, one result each, in order: what a dry run shows before the rules are saved. Changes
    /// nothing on the document.
    /// </summary>
    /// <param name="document">The document whose stored values are read.</param>
    /// <param name="definitions">The active fields of the document's type, <paramref name="fieldId"/> among them.</param>
    /// <param name="fieldId">The field the rules belong to: its value is <c>value</c>.</param>
    /// <param name="rules">The rules, already checked as a field's rules are before they are saved.</param>
    public virtual IReadOnlyList<FieldValidationRuleResult> EvaluateRules(
        Document document,
        IReadOnlyList<Field> definitions,
        Guid fieldId,
        IReadOnlyList<FieldValidationRule> rules)
    {
        var values = Values(document, definitions);
        return rules
            .Select(rule => EvaluateRule(rule, values.GetValueOrDefault(fieldId), id => values.GetValueOrDefault(id)))
            .ToList();
    }

    /// <summary>
    /// The fields whose rules read any of <paramref name="changedFieldIds"/>: their own value, or an input. An operator's
    /// edit re-evaluates only these, so a warning the operator accepted on a field the edit did not touch stays accepted.
    /// </summary>
    public virtual IReadOnlyCollection<Guid> FieldsReading(IReadOnlyList<Field> definitions, IReadOnlyCollection<Guid> changedFieldIds)
    {
        return definitions
            .Where(field => field.ValidationRules.Count > 0
                && (changedFieldIds.Contains(field.Id)
                    || field.ValidationRules.Any(rule => rule.Inputs.Any(input => changedFieldIds.Contains(input.FieldId)))))
            .Select(field => field.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Each field's value as a rule reads it, as JSON text: what an edit compares before and after to know which values
    /// it changed. An absent value is <c>null</c>.
    /// </summary>
    public virtual IReadOnlyDictionary<Guid, string?> Snapshot(Document document, IReadOnlyList<Field> definitions)
        => Values(document, definitions).ToDictionary(pair => pair.Key, pair => pair.Value?.GetRawText());

    /// <summary>
    /// One rule over one value. Skipped when the value or any input is absent; otherwise the expression decides, and an
    /// expression that throws, runs past a limit or returns anything but <c>true</c>, <c>false</c> or
    /// <c>[boolean, detail]</c> is <see cref="FieldValidationRuleOutcome.NotEvaluated"/>.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="value">The rule's own field's value, in the egress shape; <c>null</c> when absent.</param>
    /// <param name="inputValue">The value of another field by id, in the egress shape; <c>null</c> when absent.</param>
    public virtual FieldValidationRuleResult EvaluateRule(
        FieldValidationRule rule,
        JsonElement? value,
        Func<Guid, JsonElement?> inputValue)
    {
        if (value == null)
        {
            return FieldValidationRuleResult.Skipped;
        }

        var inputs = new List<(string Alias, JsonElement Value)>(rule.Inputs.Count);
        foreach (var input in rule.Inputs)
        {
            if (inputValue(input.FieldId) is not { } inputJson)
            {
                return FieldValidationRuleResult.Skipped;
            }

            inputs.Add((input.Alias, inputJson));
        }

        try
        {
            var engine = CreateEngine();
            var parser = new JsonParser(engine);
            engine.SetValue(FieldValidationRuleConsts.ValueAlias, parser.Parse(value.Value.GetRawText()));
            foreach (var (alias, json) in inputs)
            {
                engine.SetValue(alias, parser.Parse(json.GetRawText()));
            }

            return Interpret(engine.Evaluate(AsExpression(rule.Expression)));
        }
        catch (Exception ex) when (ex is StatementsCountOverflowException or TimeoutException)
        {
            return FieldValidationRuleResult.NotEvaluated(_localizer["FieldValidationRule:TooLong"].Value);
        }
        catch (MemoryLimitExceededException)
        {
            return FieldValidationRuleResult.NotEvaluated(_localizer["FieldValidationRule:TooMuchMemory"].Value);
        }
        catch (RecursionDepthOverflowException)
        {
            return FieldValidationRuleResult.NotEvaluated(_localizer["FieldValidationRule:TooDeep"].Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The script's own error (a TypeError, a ReferenceError): the engine's words, which name what went wrong where.
            return FieldValidationRuleResult.NotEvaluated(Clip(ex.Message));
        }
    }

    /// <summary>
    /// Refuses an expression that does not parse as one JavaScript expression, so a rule is never saved that could only
    /// ever fail to evaluate. Runs nothing.
    /// </summary>
    /// <param name="fieldName">The field the rule belongs to, for the error.</param>
    /// <param name="position">The rule's one-based position among the field's rules, for the error.</param>
    /// <param name="expression">The expression.</param>
    public virtual void CheckExpression(string fieldName, int position, string expression)
    {
        string? error;
        try
        {
            var program = Engine.PrepareScript(AsExpression(expression), null, true).Program;

            // In parentheses a statement list does not parse, but text that closes the parenthesis itself can still add
            // statements: "true); (false" parses as two. A rule is exactly one expression statement, and not a function,
            // whose value is never true or false.
            error = program.Body.Count == 1 && program.Body[0] is ExpressionStatement statement
                ? statement.Expression is IFunction
                    ? _localizer["FieldValidationRule:IsAFunction"].Value
                    : null
                : _localizer["FieldValidationRule:NotOneExpression"].Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        if (error != null)
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.UnparsableValidationRule)
                .WithData("Name", fieldName)
                .WithData("Index", position)
                .WithData("Error", Clip(error));
        }
    }

    /// <summary>
    /// <paramref name="expression"/> called, when it is a function that takes no parameters: a rule's body written
    /// without the call that runs it, as <c>() =&gt; value.length &gt; 0</c>, or with a call an arrow function cannot
    /// take without parentheses, as <c>() =&gt; { ... }()</c>, which does not parse. Anything else is returned as it is.
    /// </summary>
    public virtual string CallIfFunction(string expression)
    {
        if (IsFunctionWithoutParameters(expression, out var parses))
        {
            // On a line of its own, so a line comment at the end of the function cannot swallow the call.
            return "(" + expression + "\n)()";
        }

        var trimmed = expression.TrimEnd();
        if (!parses
            && trimmed.EndsWith("()", StringComparison.Ordinal)
            && IsFunctionWithoutParameters(trimmed[..^2], out _))
        {
            return "(" + trimmed[..^2] + "\n)()";
        }

        return expression;
    }

    private static bool IsFunctionWithoutParameters(string expression, out bool parses)
    {
        try
        {
            var program = Engine.PrepareScript(AsExpression(expression), null, true).Program;
            parses = true;
            return program.Body.Count == 1
                && program.Body[0] is ExpressionStatement { Expression: IFunction { Params.Count: 0, Async: false, Generator: false } };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Does not parse: the checks that follow refuse it with the parser's words.
            parses = false;
            return false;
        }
    }

    /// <summary>A fresh engine per rule, so nothing one rule defines is seen by the next.</summary>
    protected virtual Engine CreateEngine()
        => new(options => options
            .Strict()
            .MaxStatements(MaxStatements)
            .TimeoutInterval(Timeout)
            .LimitMemory(MemoryLimitBytes)
            .LimitRecursion(MaxRecursionDepth));

    /// <summary>
    /// The line a rule contributes to its field's warning, or <c>null</c> when it holds or was skipped: the rule's
    /// description, with the detail it returned, or with why it could not be evaluated.
    /// </summary>
    protected virtual string? MessageLine(FieldValidationRule rule, FieldValidationRuleResult result)
        => result.Outcome switch
        {
            FieldValidationRuleOutcome.Failed when result.Detail != null
                => _localizer["FieldValidationRule:FailedWithDetail", rule.Description, result.Detail].Value,
            FieldValidationRuleOutcome.Failed => rule.Description,
            FieldValidationRuleOutcome.NotEvaluated
                => _localizer["FieldValidationRule:NotEvaluated", rule.Description, result.Detail ?? string.Empty].Value,
            _ => null
        };

    private Dictionary<Guid, JsonElement?> Values(Document document, IReadOnlyList<Field> definitions)
    {
        var values = new Dictionary<Guid, JsonElement?>(definitions.Count);
        foreach (var field in definitions)
        {
            values[field.Id] = document.FlexFields.TryGetValue(field.Name, out var stored)
                ? FlexFieldValueJsonWriter.Write(stored, field.FieldTypeName, field.Configuration, _registry)
                : null;
        }

        return values;
    }

    /// <summary>
    /// The expression in parentheses, on lines of its own: a statement list does not parse, and a line comment at its end
    /// cannot swallow the closing parenthesis. <see cref="CheckExpression"/> also refuses text that closes the parenthesis
    /// itself to add statements.
    /// </summary>
    private static string AsExpression(string expression) => "(\n" + expression + "\n)";

    private FieldValidationRuleResult Interpret(JsValue result)
    {
        if (result.IsBoolean())
        {
            return result.AsBoolean() ? FieldValidationRuleResult.Passed : FieldValidationRuleResult.Failed(null);
        }

        if (result.IsArray())
        {
            var array = result.AsArray();
            if (array.Length == 2 && array[0].IsBoolean())
            {
                if (array[0].AsBoolean())
                {
                    return FieldValidationRuleResult.Passed;
                }

                var detail = array[1].IsNull() || array[1].IsUndefined() ? null : array[1].ToString();
                return FieldValidationRuleResult.Failed(string.IsNullOrWhiteSpace(detail) ? null : Clip(detail));
            }
        }

        return FieldValidationRuleResult.NotEvaluated(
            Clip(_localizer["FieldValidationRule:UnexpectedResult", result.Type.ToString()].Value));
    }

    private static string Clip(string text)
    {
        var oneLine = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return TextTruncator.AtCharBoundary(oneLine, MaxDetailLength);
    }
}

/// <summary>One rule's outcome, with the detail a failing rule returned or why it could not be evaluated.</summary>
public sealed record FieldValidationRuleResult(FieldValidationRuleOutcome Outcome, string? Detail)
{
    public static readonly FieldValidationRuleResult Passed = new(FieldValidationRuleOutcome.Passed, null);

    public static readonly FieldValidationRuleResult Skipped = new(FieldValidationRuleOutcome.Skipped, null);

    public static FieldValidationRuleResult Failed(string? detail) => new(FieldValidationRuleOutcome.Failed, detail);

    public static FieldValidationRuleResult NotEvaluated(string reason) => new(FieldValidationRuleOutcome.NotEvaluated, reason);
}

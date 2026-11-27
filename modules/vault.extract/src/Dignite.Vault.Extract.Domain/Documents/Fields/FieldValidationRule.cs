using System;
using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// One validation rule of a <see cref="Field"/>: a sentence for the operator and a JavaScript expression over the field's
/// stored value, evaluated in code wherever the document's values are written. A rule that does not hold puts a blocking
/// validation warning on the field, worded with <see cref="Description"/>.
/// <para>
/// The expression sees the field's own value as <c>value</c> and each <see cref="Inputs">input</see> under its alias, in
/// the shape the <c>ExtractedFields</c> egress carries (a number, an ISO date string, table rows under <c>"values"</c>). It
/// returns <c>true</c> when the rule holds, <c>false</c> when it does not, or <c>[false, "detail"]</c> to say where, the
/// contract Extend's validation code blocks use. A rule whose value or any input is absent is skipped.
/// </para>
/// <para>
/// Replaces the rules administrators wrote into a field's prompt for the extraction model to judge (#527): measured on a
/// bank statement, the model warned on correct values and missed broken ones, while the same checks in code did neither.
/// </para>
/// </summary>
/// <param name="Description">What the rule requires, in the administrator's words; the text of the warning when it fails.</param>
/// <param name="Expression">The JavaScript expression that decides it.</param>
/// <param name="Inputs">The other fields of the type the expression reads, by alias.</param>
public sealed record FieldValidationRule(string Description, string Expression, IReadOnlyList<FieldValidationRuleInput> Inputs);

/// <summary>
/// Another field of the same type that a <see cref="FieldValidationRule"/> reads. Bound by the field's immutable id, so a
/// rename leaves the rule working; the alias is the name the expression gives it.
/// </summary>
/// <param name="Alias">The identifier the expression reads the field's value under.</param>
/// <param name="FieldId">The <see cref="Field"/> it is bound to.</param>
public sealed record FieldValidationRuleInput(string Alias, Guid FieldId);

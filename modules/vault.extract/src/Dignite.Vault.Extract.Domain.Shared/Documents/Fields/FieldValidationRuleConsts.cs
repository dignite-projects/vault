namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Limits on a field's validation rules: a sentence an operator reads, a JavaScript expression evaluated over the field's
/// stored value, and the other fields of its type the expression reads. The expression is written by an administrator (or
/// drafted for one) and runs in a sandbox, so these bound what one rule can bring to it.
/// </summary>
/// <remarks>
/// The limits are static properties, not constants, as in the module's other <c>*Consts</c>: <c>DynamicStringLength</c>
/// reads them by reflection as properties, and fails to construct when it finds none.
/// </remarks>
public static class FieldValidationRuleConsts
{
    /// <summary>Rules one field may carry.</summary>
    public static int MaxRulesPerField { get; set; } = 10;

    /// <summary>
    /// Characters of a rule's description. It is what an operator reads when the rule fails, and several failed rules of
    /// one field share one warning of <c>DocumentFieldValidationWarningConsts.MaxMessageLength</c> characters.
    /// </summary>
    public static int MaxDescriptionLength { get; set; } = 256;

    /// <summary>Characters of a rule's expression: one expression, which may hold an arrow function, not a script.</summary>
    public static int MaxExpressionLength { get; set; } = 4000;

    /// <summary>Other fields one rule may read.</summary>
    public static int MaxInputsPerRule { get; set; } = 10;

    /// <summary>
    /// What a rule may call another field in its expression: a JavaScript identifier. The field's own value is always
    /// <see cref="ValueAlias"/>, so that name is not available to an input.
    /// </summary>
    public const string AliasPattern = "^[A-Za-z_][A-Za-z0-9_]{0,63}$";

    /// <summary>The name the rule's own field's value has in its expression.</summary>
    public const string ValueAlias = "value";
}

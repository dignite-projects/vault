namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>One rule's outcome on the document it was tried on.</summary>
public class FieldValidationRuleTestResultDto
{
    public FieldValidationRuleOutcome Outcome { get; set; }

    /// <summary>
    /// For <see cref="FieldValidationRuleOutcome.Failed"/>, the detail the rule returned, if any; for
    /// <see cref="FieldValidationRuleOutcome.NotEvaluated"/>, why it could not be evaluated. Otherwise <c>null</c>.
    /// </summary>
    public string? Detail { get; set; }
}

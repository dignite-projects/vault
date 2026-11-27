namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>What became of one validation rule over one document's values.</summary>
public enum FieldValidationRuleOutcome
{
    /// <summary>The rule holds.</summary>
    Passed,

    /// <summary>The rule does not hold.</summary>
    Failed,

    /// <summary>The value or an input is absent, so the rule says nothing.</summary>
    Skipped,

    /// <summary>The expression threw, ran past a sandbox limit or returned something else: counted as not holding.</summary>
    NotEvaluated
}

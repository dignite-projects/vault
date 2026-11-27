using System;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>A saved field whose extraction instruction states checks to draft validation rules from.</summary>
public class DraftFieldValidationRulesFromPromptInput
{
    /// <summary>The field. Its saved extraction instruction is read, so save an edited instruction first.</summary>
    public Guid FieldId { get; set; }
}

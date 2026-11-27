using System;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>A requirement in words to draft a validation rule of a saved field from.</summary>
public class DraftFieldValidationRuleInput
{
    /// <summary>The field the rule belongs to. Its document type's fields are what the rule can read.</summary>
    public Guid FieldId { get; set; }

    /// <summary>What the rule requires, as the administrator would put it to an operator.</summary>
    [Required]
    [DynamicStringLength(typeof(FieldValidationRuleConsts), nameof(FieldValidationRuleConsts.MaxDescriptionLength))]
    public string Description { get; set; } = default!;
}

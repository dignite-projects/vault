using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>Validation rules not yet saved, to try on one document's stored values.</summary>
public class TestFieldValidationRulesInput
{
    /// <summary>The field the rules would belong to: its stored value is <c>value</c>.</summary>
    public Guid FieldId { get; set; }

    /// <summary>A document of the field's own document type.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The rules, checked as the field API checks them before they run.</summary>
    [Required]
    public List<FieldValidationRuleDto> Rules { get; set; } = new();
}

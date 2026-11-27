using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// One validation rule of a field: what it requires, in words an operator reads when it fails, and the JavaScript
/// expression that decides it over the field's stored value (<c>value</c>) and the other fields it reads (by alias). The
/// expression returns <c>true</c>, <c>false</c>, or <c>[false, "detail"]</c>; a rule whose value or input is absent is
/// skipped. See the Validation Rules section of the document types documentation.
/// </summary>
public class FieldValidationRuleDto
{
    [Required]
    [DynamicStringLength(typeof(FieldValidationRuleConsts), nameof(FieldValidationRuleConsts.MaxDescriptionLength))]
    public string Description { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldValidationRuleConsts), nameof(FieldValidationRuleConsts.MaxExpressionLength))]
    public string Expression { get; set; } = default!;

    /// <summary>The other fields of the same document type the expression reads.</summary>
    public List<FieldValidationRuleInputDto> Inputs { get; set; } = new();
}

/// <summary>Another field of the same document type a validation rule reads, under the name the expression uses.</summary>
public class FieldValidationRuleInputDto
{
    /// <summary>The identifier the expression reads the field's value under; not <c>value</c>.</summary>
    [Required]
    [RegularExpression(FieldValidationRuleConsts.AliasPattern)]
    public string Alias { get; set; } = default!;

    /// <summary>The field read, by its immutable id, so renaming it leaves the rule working.</summary>
    [Required]
    public Guid FieldId { get; set; }
}

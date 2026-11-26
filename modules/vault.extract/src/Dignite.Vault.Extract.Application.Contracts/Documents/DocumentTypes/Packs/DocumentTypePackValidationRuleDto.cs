using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dignite.Vault.Extract.Documents.Fields;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>
/// A field's validation rule inside a type pack: the same rule as <see cref="FieldValidationRuleDto"/>, except that each
/// input names the field it reads by <c>Name</c>, since a pack carries no ids. Resolved against the type's fields on import.
/// </summary>
public class DocumentTypePackValidationRuleDto
{
    [Required]
    [DynamicStringLength(typeof(FieldValidationRuleConsts), nameof(FieldValidationRuleConsts.MaxDescriptionLength))]
    public string Description { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldValidationRuleConsts), nameof(FieldValidationRuleConsts.MaxExpressionLength))]
    public string Expression { get; set; } = default!;

    public List<DocumentTypePackValidationRuleInputDto> Inputs { get; set; } = new();
}

/// <summary>Another field of the same type a pack's validation rule reads, by name.</summary>
public class DocumentTypePackValidationRuleInputDto
{
    [Required]
    [RegularExpression(FieldValidationRuleConsts.AliasPattern)]
    public string Alias { get; set; } = default!;

    [Required]
    [RegularExpression(FieldDefinitionConsts.NamePattern)]
    public string FieldName { get; set; } = default!;
}

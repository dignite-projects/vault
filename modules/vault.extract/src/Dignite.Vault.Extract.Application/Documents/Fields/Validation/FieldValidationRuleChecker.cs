using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Fields.Validation;

/// <summary>
/// Checks validation rules as a field will hold them, before they are saved or tried: every input is another active field
/// of the same document type (and layer, through the repository's filters), the rule's shape passes
/// <see cref="Field.CheckValidationRules"/>, and every expression parses. The one gate for every way a rule arrives: the
/// field API, a dry run, and a draft the model wrote.
/// </summary>
public class FieldValidationRuleChecker : ITransientDependency
{
    private readonly IFieldRepository _fieldRepository;
    private readonly FieldValidationRuleEvaluator _evaluator;

    public FieldValidationRuleChecker(IFieldRepository fieldRepository, FieldValidationRuleEvaluator evaluator)
    {
        _fieldRepository = fieldRepository;
        _evaluator = evaluator;
    }

    /// <summary>
    /// <paramref name="rules"/> as the field will hold them, once each is known to work there. Changes nothing, so the
    /// caller can check before it touches the entity.
    /// </summary>
    /// <param name="documentTypeId">The document type the field belongs to.</param>
    /// <param name="fieldId">The field the rules belong to.</param>
    /// <param name="fieldName">The field's name, for the errors.</param>
    /// <param name="rules">The rules.</param>
    public virtual async Task<IReadOnlyList<FieldValidationRule>> CheckAsync(
        Guid documentTypeId,
        Guid fieldId,
        string fieldName,
        IReadOnlyList<FieldValidationRuleDto> rules)
        => Check(await _fieldRepository.GetListAsync(documentTypeId), fieldId, fieldName, rules);

    /// <summary>
    /// The same check against fields already loaded, for a caller that checks several rule sets of one type.
    /// </summary>
    /// <param name="fieldsOfType">The active fields of the document type the field belongs to.</param>
    /// <param name="fieldId">The field the rules belong to.</param>
    /// <param name="fieldName">The field's name, for the errors.</param>
    /// <param name="rules">The rules.</param>
    public virtual IReadOnlyList<FieldValidationRule> Check(
        IReadOnlyList<Field> fieldsOfType,
        Guid fieldId,
        string fieldName,
        IReadOnlyList<FieldValidationRuleDto> rules)
    {
        var siblingIds = fieldsOfType
            .Where(field => field.Id != fieldId)
            .Select(field => field.Id)
            .ToHashSet();

        for (var i = 0; i < rules.Count; i++)
        {
            foreach (var input in rules[i]?.Inputs ?? new List<FieldValidationRuleInputDto>())
            {
                if (input != null && input.FieldId != fieldId && !siblingIds.Contains(input.FieldId))
                {
                    throw Field.InvalidInput(fieldName, i + 1, input.Alias);
                }
            }
        }

        var checkedRules = Field.CheckValidationRules(fieldName, fieldId, rules.Select(rule => rule == null
            ? null!
            : new FieldValidationRule(
                rule.Description,
                rule.Expression,
                (rule.Inputs ?? new List<FieldValidationRuleInputDto>())
                    .Select(input => input == null ? null! : new FieldValidationRuleInput(input.Alias, input.FieldId))
                    .ToList())));

        for (var i = 0; i < checkedRules.Count; i++)
        {
            _evaluator.CheckExpression(fieldName, i + 1, checkedRules[i].Expression);
        }

        return checkedRules;
    }
}

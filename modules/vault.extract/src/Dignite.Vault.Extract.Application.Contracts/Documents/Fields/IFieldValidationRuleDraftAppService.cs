using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Writing a field's validation rules: drafting them with the structured LLM client, from a requirement in words or from
/// the checks the field's extraction instruction states, and trying rules not yet saved on a document's stored values.
/// The model writes rules at configuration time only; at run time they are decided in code. A draft is a suggestion the
/// administrator reviews and saves through the field API, which checks it again.
/// <para>
/// All three need <c>FieldDefinitions.Create</c> or <c>.Update</c>; trying rules also needs read access to the document.
/// The drafting entries fail open: on a timeout, a provider error, a description over the type's prompt budget, or an
/// answer that does not pass the checks a saved rule passes, they return no draft.
/// </para>
/// </summary>
public interface IFieldValidationRuleDraftAppService : IApplicationService
{
    /// <summary>
    /// Drafts one rule of a field from a requirement in words. Returns the draft, or none when the model could not
    /// write one that passes the checks.
    /// </summary>
    Task<ListResultDto<FieldValidationRuleDto>> DraftAsync(
        DraftFieldValidationRuleInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drafts a rule for each check the field's saved extraction instruction states (totals, orderings, agreement with
    /// other fields), at most <see cref="FieldValidationRuleConsts.MaxRulesPerField"/>. Returns the drafts that pass
    /// the checks; none when the instruction states no check.
    /// </summary>
    Task<ListResultDto<FieldValidationRuleDto>> DraftFromPromptAsync(
        DraftFieldValidationRulesFromPromptInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs rules not yet saved over a document's stored values, as rules of the field, and returns each one's outcome
    /// in order. The rules are checked first as the field API checks them. Calls no model and changes nothing.
    /// </summary>
    Task<ListResultDto<FieldValidationRuleTestResultDto>> TestAsync(
        TestFieldValidationRulesInput input,
        CancellationToken cancellationToken = default);
}

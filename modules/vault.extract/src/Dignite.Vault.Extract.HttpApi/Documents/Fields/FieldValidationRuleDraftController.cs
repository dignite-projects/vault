using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.HttpApi.Documents.Fields;

[Area("vault-extract")]
[Route("api/vault-extract/field-validation-rule-draft")]
public class FieldValidationRuleDraftController : VaultExtractController, IFieldValidationRuleDraftAppService
{
    private readonly IFieldValidationRuleDraftAppService _draftAppService;

    public FieldValidationRuleDraftController(IFieldValidationRuleDraftAppService draftAppService)
    {
        _draftAppService = draftAppService;
    }

    [HttpPost("draft")]
    public virtual Task<ListResultDto<FieldValidationRuleDto>> DraftAsync(
        [FromBody] DraftFieldValidationRuleInput input,
        CancellationToken cancellationToken)
    {
        return _draftAppService.DraftAsync(input, cancellationToken);
    }

    [HttpPost("draft-from-prompt")]
    public virtual Task<ListResultDto<FieldValidationRuleDto>> DraftFromPromptAsync(
        [FromBody] DraftFieldValidationRulesFromPromptInput input,
        CancellationToken cancellationToken)
    {
        return _draftAppService.DraftFromPromptAsync(input, cancellationToken);
    }

    [HttpPost("test")]
    public virtual Task<ListResultDto<FieldValidationRuleTestResultDto>> TestAsync(
        [FromBody] TestFieldValidationRulesInput input,
        CancellationToken cancellationToken)
    {
        return _draftAppService.TestAsync(input, cancellationToken);
    }
}

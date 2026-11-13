using System;
using System.Threading.Tasks;
using Dignite.Vault.Index.Profiles;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Index.HttpApi.Profiles;

/// <summary>The index profile page over HTTP. Authorization is in the application service.</summary>
[Area("vault-index")]
[Route("api/vault-index/profiles")]
public class IndexProfileController : VaultIndexController, IIndexProfileAppService
{
    private readonly IIndexProfileAppService _profileAppService;

    public IndexProfileController(IIndexProfileAppService profileAppService)
    {
        _profileAppService = profileAppService;
    }

    [HttpGet]
    public virtual Task<IndexProfileListDto> GetListAsync()
    {
        return _profileAppService.GetListAsync();
    }

    [HttpGet("rebuild-estimate")]
    public virtual Task<IndexRebuildEstimateDto> GetRebuildEstimateAsync([FromQuery] IndexProfileInput input)
    {
        return _profileAppService.GetRebuildEstimateAsync(input);
    }

    [HttpPut]
    public virtual Task<IndexProfileDto> SetAsync([FromBody] IndexProfileInput input)
    {
        return _profileAppService.SetAsync(input);
    }

    [HttpDelete("{documentTypeId}")]
    public virtual Task ResetAsync(Guid documentTypeId)
    {
        return _profileAppService.ResetAsync(documentTypeId);
    }
}

using System;
using System.Threading.Tasks;
using Dignite.Vault.Index.Status;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Index.HttpApi.Status;

/// <summary>The index status page over HTTP. Authorization and the read scope are in the application service.</summary>
[Area("vault-index")]
[Route("api/vault-index/status")]
public class IndexStatusController : VaultIndexController, IIndexStatusAppService
{
    private readonly IIndexStatusAppService _statusAppService;

    public IndexStatusController(IIndexStatusAppService statusAppService)
    {
        _statusAppService = statusAppService;
    }

    [HttpGet]
    public virtual Task<IndexStatusSummaryDto> GetSummaryAsync()
    {
        return _statusAppService.GetSummaryAsync();
    }

    [HttpGet("documents")]
    public virtual Task<PagedResultDto<IndexedDocumentStatusDto>> GetDocumentListAsync(GetIndexedDocumentListInput input)
    {
        return _statusAppService.GetDocumentListAsync(input);
    }

    [HttpGet("documents/{documentId}/runs")]
    public virtual Task<ListResultDto<IndexRunDto>> GetRunsAsync(Guid documentId)
    {
        return _statusAppService.GetRunsAsync(documentId);
    }

    [HttpPost("documents/{documentId}/rebuild")]
    public virtual Task RebuildAsync(Guid documentId)
    {
        return _statusAppService.RebuildAsync(documentId);
    }
}

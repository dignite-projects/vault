using System;
using System.Threading.Tasks;
using Dignite.Vault.Index.Chunks;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Index.HttpApi.Chunks;

/// <summary>The chunk view over HTTP. Authorization and the read scope are in the application service.</summary>
[Area("vault-index")]
[Route("api/vault-index/chunks")]
public class IndexChunkController : VaultIndexController, IIndexChunkAppService
{
    private readonly IIndexChunkAppService _chunkAppService;

    public IndexChunkController(IIndexChunkAppService chunkAppService)
    {
        _chunkAppService = chunkAppService;
    }

    [HttpGet]
    public virtual Task<IndexChunkListDto> GetListAsync([FromQuery] GetIndexChunkListInput input)
    {
        return _chunkAppService.GetListAsync(input);
    }

    [HttpPost("{id}/disable")]
    public virtual Task DisableAsync(Guid id)
    {
        return _chunkAppService.DisableAsync(id);
    }

    [HttpPost("{id}/enable")]
    public virtual Task EnableAsync(Guid id)
    {
        return _chunkAppService.EnableAsync(id);
    }
}

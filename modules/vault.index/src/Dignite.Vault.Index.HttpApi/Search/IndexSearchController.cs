using System;
using System.Threading.Tasks;
using Dignite.Vault.Index.Search;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Index.HttpApi.Search;

/// <summary>Search, outline and read over HTTP. Authorization, the feature gate and the read scope are in the application service.</summary>
[Area("vault-index")]
[Route("api/vault-index")]
public class IndexSearchController : VaultIndexController, IIndexSearchAppService
{
    private readonly IIndexSearchAppService _searchAppService;

    public IndexSearchController(IIndexSearchAppService searchAppService)
    {
        _searchAppService = searchAppService;
    }

    [HttpGet("search")]
    public virtual Task<IndexSearchResultDto> SearchAsync([FromQuery] IndexSearchInput input)
    {
        return _searchAppService.SearchAsync(input);
    }

    [HttpGet("documents/{documentId}/outline")]
    public virtual Task<DocumentOutlineDto> GetOutlineAsync(Guid documentId)
    {
        return _searchAppService.GetOutlineAsync(documentId);
    }

    [HttpGet("read")]
    public virtual Task<IndexReadResultDto> ReadAsync([FromQuery] IndexReadInput input)
    {
        return _searchAppService.ReadAsync(input);
    }
}

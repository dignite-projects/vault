using System.Threading.Tasks;
using Dignite.Vault.Index.Search;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Index.HttpApi.Search;

/// <summary>
/// The retrieval test over HTTP. A POST, though it changes nothing: the input carries field conditions and settings,
/// and a run may call the rerank model, which costs.
/// </summary>
[Area("vault-index")]
[Route("api/vault-index/search-test")]
public class IndexSearchTestController : VaultIndexController, IIndexSearchTestAppService
{
    private readonly IIndexSearchTestAppService _searchTestAppService;

    public IndexSearchTestController(IIndexSearchTestAppService searchTestAppService)
    {
        _searchTestAppService = searchTestAppService;
    }

    [HttpPost]
    public virtual Task<IndexSearchTestResultDto> RunAsync([FromBody] IndexSearchTestInput input)
    {
        return _searchTestAppService.RunAsync(input);
    }
}

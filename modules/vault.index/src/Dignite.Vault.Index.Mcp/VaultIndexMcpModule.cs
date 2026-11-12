using Dignite.Abp.AspNetCore.Mcp;
using Dignite.Vault.Index.Mcp;
using Dignite.Vault.Index.Mcp.Search;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

/// <summary>
/// Vault Index's MCP exit, parallel to the REST <c>HttpApi</c> one. It contributes three tools to the application's
/// shared MCP server (<see cref="AbpAspNetCoreMcpModule"/>), next to Extract's under the same <c>/mcp</c> endpoint:
/// <c>vault_index_search</c>, <c>vault_index_outline</c> and <c>vault_index_read</c>, find first and read second. The
/// tools are thin shells over <c>IIndexSearchAppService</c>, which holds authorization, the feature gate and the read
/// scope; they add the transport's own duties: compile-time descriptions, boundary-wrapped document text, and caps
/// announced when they cut.
/// </summary>
[DependsOn(
    typeof(VaultIndexApplicationContractsModule),
    typeof(AbpAspNetCoreMcpModule))]
public class VaultIndexMcpModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpMcpModule(VaultIndexMcpConsts.ModuleName, mcp => mcp.AddTools<IndexSearchTools>());
    }
}

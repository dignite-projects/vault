using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Http.Client;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

/// <summary>
/// Registers a dynamic HTTP client proxy for every Index application service interface, so a remote application
/// injects <see cref="Search.IIndexSearchAppService"/> as if Index ran in-process. The base URL comes from
/// <c>RemoteServices:VaultIndex</c>, or <c>RemoteServices:Default</c> when that is absent.
/// </summary>
[DependsOn(
    typeof(VaultIndexApplicationContractsModule),
    typeof(AbpHttpClientModule))]
public class VaultIndexHttpApiClientModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddHttpClientProxies(
            typeof(VaultIndexApplicationContractsModule).Assembly,
            VaultIndexRemoteServiceConsts.RemoteServiceName);
    }
}

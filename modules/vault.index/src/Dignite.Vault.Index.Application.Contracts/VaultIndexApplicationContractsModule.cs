using Volo.Abp.Application;
using Volo.Abp.Authorization;
using Volo.Abp.Features;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

[DependsOn(
    typeof(VaultIndexDomainSharedModule),
    typeof(AbpDddApplicationContractsModule),
    typeof(AbpAuthorizationModule),
    typeof(AbpFeaturesModule)
)]
public class VaultIndexApplicationContractsModule : AbpModule
{
}

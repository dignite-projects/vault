using Volo.Abp.Domain;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

[DependsOn(
    typeof(AbpDddDomainModule),
    typeof(VaultIndexDomainSharedModule)
)]
public class VaultIndexDomainModule : AbpModule
{
}

using Volo.Abp.Modularity;

namespace Dignite.Vault.Parse;

[DependsOn(typeof(VaultParseAbstractionsModule))]
public class VaultParseModule : AbpModule
{
}

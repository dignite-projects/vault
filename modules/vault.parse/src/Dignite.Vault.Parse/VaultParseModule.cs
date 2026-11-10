using Dignite.Vault.Parse.Ocr;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Parse;

[DependsOn(typeof(VaultParseAbstractionsModule), typeof(VaultParseOcrModule))]
public class VaultParseModule : AbpModule
{
}

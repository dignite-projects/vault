using Dignite.Vault.Index.Localization;
using Localization.Resources.AbpUi;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

[DependsOn(
    typeof(VaultIndexApplicationContractsModule),
    typeof(AbpAspNetCoreMvcModule))]
public class VaultIndexHttpApiModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<IMvcBuilder>(mvcBuilder =>
        {
            mvcBuilder.AddApplicationPartIfNotExists(typeof(VaultIndexHttpApiModule).Assembly);
        });
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Get<VaultIndexResource>()
                .AddBaseTypes(typeof(AbpUiResource));
        });
    }
}

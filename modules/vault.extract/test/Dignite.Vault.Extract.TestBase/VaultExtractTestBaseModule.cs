using System.Linq;
using Dignite.Abp.BlobManagement.Files;
using Dignite.Vault.Extract.BlobManagement;
using Dignite.Vault.Extract.Features;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Autofac;
using Volo.Abp.Data;
using Volo.Abp.Features;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Threading;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpTestBaseModule),
    typeof(AbpAuthorizationModule),
    typeof(AbpFeaturesModule),
    typeof(AbpGuidsModule)
)]
public class VaultExtractTestBaseModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAlwaysAllowAuthorization();

        // Last registered, so a test can switch VaultExtract.Enable off for the tenant. Holding nothing by
        // default it changes no feature's value.
        Configure<AbpFeatureOptions>(options =>
        {
            options.ValueProviders.Add<TestFeatureValueProvider>();
        });
    }

    public override void PostConfigureServices(ServiceConfigurationContext context)
    {
        // Documents keep their files in Blob Management, which a host installs. A test module that installs the real
        // module, or registers a substitute, keeps it; every other one gets the in-memory service.
        if (context.Services.All(d => d.ServiceType != typeof(IBlobManagementFileService)))
        {
            context.Services.AddSingleton<InMemoryBlobManagementFileService>();
            context.Services.AddSingleton<IBlobManagementFileService>(sp => sp.GetRequiredService<InMemoryBlobManagementFileService>());
        }
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        SeedTestData(context);
    }

    private static void SeedTestData(ApplicationInitializationContext context)
    {
        AsyncHelper.RunSync(async () =>
        {
            using (var scope = context.ServiceProvider.CreateScope())
            {
                await scope.ServiceProvider
                    .GetRequiredService<IDataSeeder>()
                    .SeedAsync();
            }
        });
    }
}

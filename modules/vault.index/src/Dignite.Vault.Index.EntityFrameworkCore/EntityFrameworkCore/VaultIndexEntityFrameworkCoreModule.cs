using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index.EntityFrameworkCore;

[DependsOn(
    typeof(VaultIndexDomainModule),
    typeof(AbpEntityFrameworkCoreModule)
)]
public class VaultIndexEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<VaultIndexDbContext>(options =>
        {
            options.AddDefaultRepositories<IVaultIndexDbContext>();
        });
    }
}

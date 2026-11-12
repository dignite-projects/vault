using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore.SqlServer;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// Index's default storage on SQL Server 2025 or Azure SQL. A host that stores Index elsewhere leaves this module out
/// and registers its own <c>IChunkSearchIndex</c>.
/// </summary>
[DependsOn(
    typeof(VaultIndexEntityFrameworkCoreModule),
    typeof(AbpEntityFrameworkCoreSqlServerModule)
)]
public class VaultIndexEntityFrameworkCoreSqlServerModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<VaultIndexSqlServerDbContext>();
    }
}

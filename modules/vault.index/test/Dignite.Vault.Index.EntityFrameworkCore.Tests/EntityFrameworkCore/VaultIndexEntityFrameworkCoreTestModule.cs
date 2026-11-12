using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;

namespace Dignite.Vault.Index.EntityFrameworkCore;

[DependsOn(
    typeof(VaultIndexTestBaseModule),
    typeof(VaultIndexApplicationModule),
    typeof(VaultIndexEntityFrameworkCoreModule),
    typeof(AbpEntityFrameworkCoreSqliteModule)
)]
public class VaultIndexEntityFrameworkCoreTestModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<AbpSqliteOptions>(x => x.BusyTimeout = null);
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAlwaysDisableUnitOfWorkTransaction();

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        new VaultIndexDbContext(new DbContextOptionsBuilder<VaultIndexDbContext>().UseSqlite(connection).Options)
            .GetService<IRelationalDatabaseCreator>()
            .CreateTables();

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(c => c.UseSqlite(connection));
        });
    }
}

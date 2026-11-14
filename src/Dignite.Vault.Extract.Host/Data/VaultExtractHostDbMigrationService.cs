using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Identity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.TenantManagement;

namespace Dignite.Vault.Extract.Host.Data;

public class VaultExtractHostDbMigrationService : ITransientDependency
{
    public ILogger<VaultExtractHostDbMigrationService> Logger { get; set; }

    private readonly IDataSeeder _dataSeeder;
    private readonly VaultExtractHostDbSchemaMigrator _dbSchemaMigrator;
    private readonly ITenantRepository _tenantRepository;
    private readonly ICurrentTenant _currentTenant;

    public VaultExtractHostDbMigrationService(
        IDataSeeder dataSeeder,
        VaultExtractHostDbSchemaMigrator dbSchemaMigrator,
        ITenantRepository tenantRepository,
        ICurrentTenant currentTenant)
    {
        _dataSeeder = dataSeeder;
        _dbSchemaMigrator = dbSchemaMigrator;
        _tenantRepository = tenantRepository;
        _currentTenant = currentTenant;

        Logger = NullLogger<VaultExtractHostDbMigrationService>.Instance;
    }

    public async Task MigrateAsync()
    {
        Logger.LogInformation("Started database migrations...");

        await MigrateDatabaseSchemaAsync();
        await SeedDataAsync();

        // #640: a tenant created before this fix never received its DocumentManager / Viewer pair,
        // because the seed contributors ignored the tenant they were seeding for. Re-seeding every
        // existing tenant here — the same shape as the ABP application template's migration service —
        // gives those tenants their roles retroactively; SeedRoleAsync is idempotent, so re-running this
        // against an already-seeded tenant changes nothing.
        if (VaultExtractHostModule.IsMultiTenant)
        {
            var tenants = await _tenantRepository.GetListAsync(includeDetails: true);
            foreach (var tenant in tenants)
            {
                using (_currentTenant.Change(tenant.Id))
                {
                    if (tenant.ConnectionStrings.Any())
                    {
                        await MigrateDatabaseSchemaAsync();
                    }

                    await SeedDataAsync(tenant.Id);
                }

                Logger.LogInformation($"Successfully completed host database migrations for tenant '{tenant.Name}'.");
            }
        }

        Logger.LogInformation($"Successfully completed host database migrations.");
        Logger.LogInformation("You can safely end this process...");
    }

    private async Task MigrateDatabaseSchemaAsync()
    {
        await _dbSchemaMigrator.MigrateAsync();
    }

    private async Task SeedDataAsync(Guid? tenantId = null)
    {
        await _dataSeeder.SeedAsync(new DataSeedContext(tenantId)
            .WithProperty(IdentityDataSeedContributor.AdminEmailPropertyName, VaultExtractHostConsts.AdminEmailDefaultValue)
            .WithProperty(IdentityDataSeedContributor.AdminPasswordPropertyName, VaultExtractHostConsts.AdminPasswordDefaultValue)
        );
    }
}

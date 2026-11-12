using Dignite.Vault.Extract;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Search;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Application;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

[DependsOn(
    typeof(VaultIndexDomainModule),
    typeof(VaultIndexAbstractionsModule),
    typeof(VaultIndexApplicationContractsModule),
    // Extract's egress contract: IDocumentIntegrationService for ingestion, GetReadScopeAsync for retrieval.
    typeof(VaultExtractApplicationContractsModule),
    typeof(AbpDddApplicationModule),
    typeof(AbpBackgroundJobsModule)
)]
public class VaultIndexApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        Configure<VaultIndexIngestionOptions>(configuration.GetSection("Vault:IndexIngestion"));
        Configure<VaultIndexSearchOptions>(configuration.GetSection("Vault:IndexSearch"));
    }
}

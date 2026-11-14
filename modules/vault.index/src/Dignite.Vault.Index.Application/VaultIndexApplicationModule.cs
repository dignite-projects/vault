using Dignite.Vault.Extract;
using Dignite.Vault.Index.Compilation;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Search;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Application;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Caching;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

[DependsOn(
    typeof(VaultIndexDomainModule),
    typeof(VaultIndexAbstractionsModule),
    typeof(VaultIndexApplicationContractsModule),
    // Extract's egress contract: IDocumentIntegrationService for ingestion, GetReadScopeAsync for retrieval.
    typeof(VaultExtractApplicationContractsModule),
    typeof(AbpDddApplicationModule),
    typeof(AbpBackgroundJobsModule),
    typeof(AbpCachingModule)   // ModelResultCache
)]
public class VaultIndexApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        Configure<VaultIndexIngestionOptions>(configuration.GetSection("Vault:IndexIngestion"));
        Configure<VaultIndexSearchOptions>(configuration.GetSection("Vault:IndexSearch"));

        // Index's jobs never wait behind Extract's, and summary compilation (several model calls per document) never
        // holds up indexing. The names are the workers' distributed locks.
        Configure<AbpBackgroundJobWorkerOptions>(options =>
        {
            options.AddDedicatedWorker("VaultIndex:Documents", typeof(IndexDocumentJobArgs), typeof(IndexBackfillJobArgs));
            options.AddDedicatedWorker("VaultIndex:Compile", typeof(IndexCompileJobArgs));
        });
    }
}

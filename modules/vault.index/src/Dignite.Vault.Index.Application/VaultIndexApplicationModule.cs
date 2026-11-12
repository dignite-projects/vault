using Dignite.Vault.Extract;
using Volo.Abp.Application;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index;

[DependsOn(
    typeof(VaultIndexDomainModule),
    typeof(VaultIndexApplicationContractsModule),
    // Extract's egress contract: IDocumentIntegrationService for ingestion, GetReadScopeAsync for retrieval.
    typeof(VaultExtractApplicationContractsModule),
    typeof(AbpDddApplicationModule),
    typeof(AbpBackgroundJobsModule)
)]
public class VaultIndexApplicationModule : AbpModule
{
}

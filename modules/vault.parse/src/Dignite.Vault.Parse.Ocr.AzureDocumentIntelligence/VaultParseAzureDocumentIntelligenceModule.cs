using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Parse.Ocr.AzureDocumentIntelligence;

[DependsOn(typeof(VaultParseOcrModule))]
public class VaultParseAzureDocumentIntelligenceModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<AzureDocumentIntelligenceOptions>(
            configuration.GetSection("AzureDocumentIntelligence"));
    }
}

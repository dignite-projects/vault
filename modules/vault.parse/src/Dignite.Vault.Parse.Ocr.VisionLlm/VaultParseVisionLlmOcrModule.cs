using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Parse.Ocr.VisionLlm;

[DependsOn(typeof(VaultParseModule))]
public class VaultParseVisionLlmOcrModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<VisionLlmOcrOptions>(
            configuration.GetSection("VisionLlmOcr"));
    }
}

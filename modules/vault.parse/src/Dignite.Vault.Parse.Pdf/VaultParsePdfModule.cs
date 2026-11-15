using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// Registers the PdfPig-based <see cref="PdfExtractor"/>, the engine that reads <c>.pdf</c>. Engines coexist and the
/// router picks one per file, so a host that includes this module reads PDFs with PdfPig; a host that omits it falls
/// back to the catch-all ElBruno engine.
/// <para>
/// Embedded images are transcribed by the host's OCR engine (<see cref="ParseOcrClient"/>: VisionLlm, PaddleOCR or
/// Azure Document Intelligence).
/// </para>
/// </summary>
[DependsOn(typeof(VaultParseModule))]
public class VaultParsePdfModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Bind the cost/noise caps from the "PdfExtractor" configuration section, matching the
        // appsettings-driven convention of the OCR provider modules (VisionLlmOcr / PaddleOcr / Azure).
        // Absent the section, PdfExtractorOptions keeps its defaults. PdfExtractor itself self-registers
        // via ITransientDependency + [ExposeServices(typeof(ParseExtractionClient))].
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<PdfExtractorOptions>(configuration.GetSection("PdfExtractor"));
    }
}

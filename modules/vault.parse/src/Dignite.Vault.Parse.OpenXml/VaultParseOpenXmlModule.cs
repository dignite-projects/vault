using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Parse.OpenXml;

/// <summary>
/// Registers the OpenXML-based engines: <see cref="PptxExtractor"/> (reads <c>.pptx</c>, #307) and
/// <see cref="DocxExtractor"/> (reads <c>.docx</c>, #308). Engines coexist and the router picks one per file.
/// <b>This module is required for <c>.pptx</c></b>: the catch-all ElBruno engine does not read PresentationML, so
/// without it a <c>.pptx</c> yields an empty document (and, not being a PDF, gets no OCR fallback either). A
/// <c>.docx</c> degrades to ElBruno's output.
/// <para>
/// Embedded images are transcribed by the host's OCR engine (<see cref="ParseOcrClient"/>: VisionLlm, PaddleOCR or
/// Azure Document Intelligence).
/// </para>
/// </summary>
[DependsOn(typeof(VaultParseModule))]
public class VaultParseOpenXmlModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Bind the cost/noise caps + notes flag from the "OpenXmlExtractor" configuration section,
        // matching the appsettings-driven convention of the OCR / Pdf provider modules. Absent the
        // section, OpenXmlExtractorOptions keeps its defaults. The engines self-register via
        // ITransientDependency + [ExposeServices(typeof(ParseExtractionClient))].
        var configuration = context.Services.GetConfiguration();
        context.Services.Configure<OpenXmlExtractorOptions>(configuration.GetSection("OpenXmlExtractor"));
    }
}

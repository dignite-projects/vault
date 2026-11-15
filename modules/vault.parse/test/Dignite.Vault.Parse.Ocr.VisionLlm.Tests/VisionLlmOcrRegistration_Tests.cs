using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Parse.Ocr.VisionLlm;

/// <summary>
/// Guards the ABP conventional-registration metadata for the VisionLlm services.
/// <para>
/// The pure-unit engine tests construct <see cref="VisionLlmOcrClient"/> with <c>new(...)</c> and a
/// mocked <see cref="IPdfRasterizer"/>, so they cannot catch a missing DI registration. Neither service is exposed by
/// ABP's naming convention: <c>PdfToImageRasterizer</c> does NOT end with <c>PdfRasterizer</c>, and the engine base
/// classes are found only through the explicit <c>[ExposeServices]</c> (the router as <see cref="ParseExtractionClient"/>,
/// the PDF and OpenXML engines as <see cref="ParseOcrClient"/>). These tests fail loudly if an attribute is ever
/// dropped, which would crash resolution at runtime once the VisionLlm module is enabled in a host.
/// </para>
/// </summary>
public class VisionLlmOcrRegistration_Tests
{
    [Fact]
    public void Rasterizer_Exposes_IPdfRasterizer()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(PdfToImageRasterizer))
            .ShouldContain(typeof(IPdfRasterizer));
    }

    [Fact]
    public void Engine_is_exposed_as_the_ocr_engine_and_an_engine()
    {
        var exposed = ExposedServiceExplorer.GetExposedServices(typeof(VisionLlmOcrClient));
        exposed.ShouldContain(typeof(ParseOcrClient));
        exposed.ShouldContain(typeof(ParseExtractionClient));
    }
}

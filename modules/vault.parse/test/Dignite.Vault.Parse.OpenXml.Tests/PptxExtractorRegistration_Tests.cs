using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Parse.OpenXml;

/// <summary>
/// Guards the ABP conventional-registration metadata and the file claim of <see cref="PptxExtractor"/>. The behavior tests
/// construct the engine with <c>new(...)</c>, so they cannot catch a missing DI registration. The router finds engines
/// as <see cref="ParseExtractionClient"/>, exposed only through the explicit <c>[ExposeServices]</c>; this asserts it
/// stays exposed (otherwise the router would never see it and .pptx would fall back to ElBruno, which has no PresentationML converter).
/// </summary>
public class PptxExtractorRegistration_Tests
{
    [Fact]
    public void Engine_is_exposed_as_a_ParseExtractionClient()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(PptxExtractor))
            .ShouldContain(typeof(ParseExtractionClient));
    }

    [Fact]
    public void Engine_is_conventionally_registered_via_ITransientDependency()
    {
        // ABP derives the lifetime (and hence registration) from the ITransientDependency marker, NOT from
        // [ExposeServices]; a dropped marker would silently de-register the engine while the attribute test stayed green.
        typeof(ITransientDependency).IsAssignableFrom(typeof(PptxExtractor)).ShouldBeTrue();
    }

    [Fact]
    public void Claims_only_its_files_and_outranks_the_fallback()
    {
        var engine = new PptxExtractor(
            Substitute.ForPartsOf<ParseOcrClient>(),
            Options.Create(new OpenXmlExtractorOptions()));

        engine.CanExtract(".pptx", "").ShouldBeTrue();
        engine.CanExtract("", "application/vnd.openxmlformats-officedocument.presentationml.presentation").ShouldBeTrue();
        engine.CanExtract(".png", "image/png").ShouldBeFalse();
        engine.CanExtract(".docx", "").ShouldBeFalse();
        engine.CanExtract(string.Empty, string.Empty).ShouldBeFalse();

        engine.Priority.ShouldBeGreaterThan(ParseExtractionPriorities.Fallback);
        engine.Priority.ShouldBeGreaterThan(ParseExtractionPriorities.Ocr);
    }
}

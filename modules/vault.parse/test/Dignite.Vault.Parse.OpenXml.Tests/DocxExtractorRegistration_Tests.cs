using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Parse.OpenXml;

/// <summary>
/// Guards the ABP conventional-registration metadata and the file claim of <see cref="DocxExtractor"/>. The behavior tests
/// construct the engine with <c>new(...)</c>, so they cannot catch a missing DI registration. The router finds engines
/// as <see cref="ParseExtractionClient"/>, exposed only through the explicit <c>[ExposeServices]</c>; this asserts it
/// stays exposed (otherwise the router would never see it and .docx would fall back to ElBruno).
/// </summary>
public class DocxExtractorRegistration_Tests
{
    [Fact]
    public void Engine_is_exposed_as_a_ParseExtractionClient()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(DocxExtractor))
            .ShouldContain(typeof(ParseExtractionClient));
    }

    [Fact]
    public void Engine_is_conventionally_registered_via_ITransientDependency()
    {
        // ABP derives the lifetime (and hence registration) from the ITransientDependency marker, NOT from
        // [ExposeServices]; a dropped marker would silently de-register the engine while the attribute test stayed green.
        typeof(ITransientDependency).IsAssignableFrom(typeof(DocxExtractor)).ShouldBeTrue();
    }

    [Fact]
    public void Claims_only_its_files_and_outranks_the_fallback()
    {
        var engine = new DocxExtractor(
            Substitute.ForPartsOf<ParseOcrClient>(),
            Options.Create(new OpenXmlExtractorOptions()));

        engine.CanExtract(".docx", "").ShouldBeTrue();
        engine.CanExtract("", "application/vnd.openxmlformats-officedocument.wordprocessingml.document").ShouldBeTrue();
        engine.CanExtract(".png", "image/png").ShouldBeFalse();
        engine.CanExtract(".pdf", "").ShouldBeFalse();
        engine.CanExtract(string.Empty, string.Empty).ShouldBeFalse();

        engine.Priority.ShouldBeGreaterThan(ParseExtractionPriorities.Fallback);
        engine.Priority.ShouldBeGreaterThan(ParseExtractionPriorities.Ocr);
    }
}

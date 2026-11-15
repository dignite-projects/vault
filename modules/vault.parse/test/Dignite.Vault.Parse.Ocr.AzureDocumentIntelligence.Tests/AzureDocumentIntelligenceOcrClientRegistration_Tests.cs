using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Parse.Ocr.AzureDocumentIntelligence;

/// <summary>
/// Guards the ABP registration of <see cref="AzureDocumentIntelligenceOcrClient"/>. The behavior tests construct it with <c>new(...)</c>, so they
/// cannot catch a dropped <c>[ExposeServices]</c>: the router finds it as a <see cref="ParseExtractionClient"/>, and the
/// PDF and OpenXML engines inject it as the <see cref="ParseOcrClient"/> for figures.
/// </summary>
public class AzureDocumentIntelligenceOcrClientRegistration_Tests
{
    [Fact]
    public void Engine_is_exposed_as_the_ocr_engine_and_an_engine()
    {
        var exposed = ExposedServiceExplorer.GetExposedServices(typeof(AzureDocumentIntelligenceOcrClient));
        exposed.ShouldContain(typeof(ParseOcrClient));
        exposed.ShouldContain(typeof(ParseExtractionClient));
    }

    [Fact]
    public void Engine_is_conventionally_registered_via_ITransientDependency()
    {
        typeof(ITransientDependency).IsAssignableFrom(typeof(AzureDocumentIntelligenceOcrClient)).ShouldBeTrue();
    }
}

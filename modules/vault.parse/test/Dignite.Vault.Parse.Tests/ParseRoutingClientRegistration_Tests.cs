using Microsoft.Extensions.DocumentExtraction;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// Guards the ABP conventional-registration metadata for <see cref="ParseRoutingClient"/>. The behavior tests construct
/// it with <c>new(...)</c>, so they cannot catch a dropped DI marker: without it Extract could not resolve
/// <see cref="DocumentExtractionClient"/> at runtime while the behavior tests stayed green.
/// </summary>
public class ParseRoutingClientRegistration_Tests
{
    [Fact]
    public void Router_is_exposed_as_the_DocumentExtractionClient()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(ParseRoutingClient))
            .ShouldContain(typeof(DocumentExtractionClient));
    }

    [Fact]
    public void Router_is_not_exposed_as_an_engine()
    {
        // An engine list that held the router would let it route to itself.
        ExposedServiceExplorer.GetExposedServices(typeof(ParseRoutingClient))
            .ShouldNotContain(typeof(ParseExtractionClient));
    }

    [Fact]
    public void Router_is_conventionally_registered_via_ITransientDependency()
    {
        typeof(ITransientDependency).IsAssignableFrom(typeof(ParseRoutingClient)).ShouldBeTrue();
    }
}

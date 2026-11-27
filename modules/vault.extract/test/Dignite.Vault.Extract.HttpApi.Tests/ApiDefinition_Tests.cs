using System.Net;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract;

public class ApiDefinition_Tests : VaultExtractHttpApiTestBase
{
    [Fact]
    public async Task Describes_every_endpoint_with_its_types()
    {
        // What the Angular proxy generator and Swagger read. Describing a DTO constructs its attributes, so one that
        // cannot be constructed (a DynamicStringLength naming a const instead of a static property) fails the whole
        // description with a 500.
        var response = await Client.GetAsync("/api/abp/api-definition?includeTypes=true");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("FieldValidationRuleDto");
    }
}

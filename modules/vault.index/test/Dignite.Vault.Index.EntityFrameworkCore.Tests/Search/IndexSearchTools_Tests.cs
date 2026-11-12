using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Mcp.Search;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The MCP shell of search: what an LLM sees in <c>vault_index_search</c>'s input schema, and how the tool hands field
/// conditions to the application service and answers the errors an LLM can correct.
/// </summary>
public class IndexSearchTools_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    /// <summary>
    /// Through the SDK's own schema generation on ABP's Autofac container, which takes a collection interface for a
    /// resolvable service and drops it from the schema: fieldFilters must stay a concrete List&lt;T&gt; to be seen.
    /// </summary>
    [Fact]
    public void The_input_schema_exposes_every_llm_parameter_and_no_service()
    {
        var method = typeof(IndexSearchTools).GetMethod(nameof(IndexSearchTools.SearchAsync))!;
        var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = ServiceProvider });

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        properties.TryGetProperty("fieldFilters", out _).ShouldBeTrue();
        properties.TryGetProperty("query", out _).ShouldBeTrue();
        properties.TryGetProperty("documentTypeCode", out _).ShouldBeTrue();
        properties.TryGetProperty("maxResults", out _).ShouldBeTrue();
        properties.TryGetProperty("searchAppService", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Field_filters_reach_the_application_service()
    {
        var search = Substitute.For<IIndexSearchAppService>();
        search.SearchAsync(Arg.Any<IndexSearchInput>()).Returns(new IndexSearchResultDto());
        var filters = new List<DocumentFieldFilter> { new() { Name = "rent", Min = "150000" } };

        await IndexSearchTools.SearchAsync("賃料", search, "lease", fieldFilters: filters);

        await search.Received(1).SearchAsync(Arg.Is<IndexSearchInput>(i => i.DocumentTypeCode == "lease" && i.FieldFilters == filters));
    }

    [Fact]
    public async Task Field_filters_without_a_type_or_matching_too_much_are_told_to_the_caller()
    {
        var search = Substitute.For<IIndexSearchAppService>();
        var filters = new List<DocumentFieldFilter> { new() { Name = "rent", Min = "150000" } };

        await Should.ThrowAsync<McpException>(() => IndexSearchTools.SearchAsync("賃料", search, fieldFilters: filters));
        await search.DidNotReceive().SearchAsync(Arg.Any<IndexSearchInput>());

        search.SearchAsync(Arg.Any<IndexSearchInput>()).ThrowsAsync(new BusinessException(VaultIndexErrorCodes.FieldFilterTooBroad));
        var error = await Should.ThrowAsync<McpException>(() => IndexSearchTools.SearchAsync("賃料", search, "lease", fieldFilters: filters));
        error.Message.ShouldContain("narrower");
    }
}

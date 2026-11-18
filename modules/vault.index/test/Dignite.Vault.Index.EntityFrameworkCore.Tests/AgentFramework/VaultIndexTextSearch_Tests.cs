using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Search;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Index.AgentFramework;

/// <summary>Index as an Agent Framework knowledge source: what the agent receives, and what reaches Index.</summary>
public class VaultIndexTextSearch_Tests : VaultIndexTestBase<VaultIndexAgentFrameworkTestModule>
{
    private const string Lease = """
        # 賃貸借契約書

        本契約は、甲と乙の間で締結される。

        ## 第1条（目的）

        甲は乙に対し、下記の物件を住居として賃貸する。

        ## 第2条（賃料）

        乙は毎月末日までに賃料を支払うものとする。
        """;

    private static readonly Guid LeaseType = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid LeaseDoc = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private readonly Search.FakeReranker _reranker;
    private readonly VaultIndexTextSearch _textSearch;

    public VaultIndexTextSearch_Tests()
    {
        _reranker = GetRequiredService<Search.FakeReranker>();
        _textSearch = GetRequiredService<VaultIndexTextSearch>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync()
            .Returns(_ => new List<DocumentTypeSummaryDto> { new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" } });
        GetRequiredService<IDocumentAppService>().GetReadScopeAsync()
            .Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
    }

    [Fact]
    public async Task Before_a_run_the_agent_gets_the_passages_as_untrusted_document_data()
    {
        await IndexAsync();
        _reranker.Prefer = "賃料";
        var chat = new RecordingChatClient();
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { AIContextProviders = [_textSearch.CreateProvider()] });

        await agent.RunAsync("賃料はいつ払いますか");

        _reranker.LastQuery.ShouldBe("賃料はいつ払いますか");
        var context = chat.Messages.Select(m => m.Text).Single(t => t.Contains(VaultIndexTextSearch.ContextPrompt));
        // The prompt names the tags too; the passage itself sits between them on lines of its own.
        context.ShouldContain("<document>\n");
        context.ShouldContain("\n</document>");
        context.ShouldContain("乙は毎月末日までに賃料を支払うものとする。");
        context.ShouldContain("SourceDocName: <field>\n賃貸借契約書（本店） # 賃貸借契約書 ## 第2条（賃料）\n</field>");
        context.ShouldContain(VaultIndexTextSearch.CitationsPrompt);
        context.IndexOf("第2条（賃料）", StringComparison.Ordinal).ShouldBeLessThan(context.IndexOf("第1条（目的）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_demand_the_agent_gets_the_search_as_a_tool()
    {
        await IndexAsync();
        var chat = new RecordingChatClient();
        var provider = _textSearch.CreateProvider(configure: o => o.SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling);
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { AIContextProviders = [provider] });

        await agent.RunAsync("賃料はいつ払いますか");

        var tool = chat.Tools.ShouldHaveSingleItem();
        tool.Name.ShouldBe(VaultIndexTextSearch.FunctionToolName);
        tool.Description.ShouldBe(VaultIndexTextSearch.FunctionToolDescription);
        _reranker.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_long_conversation_searches_its_latest_text()
    {
        await IndexAsync();

        // A provider that keeps recent messages puts them before the request; Index takes 1000 characters.
        await _textSearch.SearchAsync(new string('甲', 1500) + "\n賃料はいつ払いますか");
        _reranker.LastQuery!.Length.ShouldBeLessThanOrEqualTo(IndexSearchConsts.MaxQueryLength);
        _reranker.LastQuery.ShouldEndWith("\n賃料はいつ払いますか");

        // Cutting from the front never leaves half a surrogate pair at the start.
        await _textSearch.SearchAsync("賃" + string.Concat(Enumerable.Repeat("😀", 600)));
        char.IsLowSurrogate(_reranker.LastQuery![0]).ShouldBeFalse();
    }

    [Fact]
    public async Task A_blank_turn_searches_nothing()
    {
        await IndexAsync();

        (await _textSearch.SearchAsync("  \n ")).ShouldBeEmpty();

        _reranker.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_score_floor_leaves_only_the_passages_the_reranker_scored_above_it()
    {
        await IndexAsync();
        _reranker.Prefer = "目的";

        var results = (await _textSearch.SearchAsync("賃料", new VaultIndexTextSearchOptions { MinRerankScore = 0.5 })).ToList();

        results.ShouldHaveSingleItem().RawRepresentation.ShouldBeOfType<IndexSearchHitDto>()
            .HeadingPath.ShouldBe("# 賃貸借契約書 ## 第1条（目的）");
    }

    [Fact]
    public async Task Without_a_rerank_the_floor_keeps_every_hit()
    {
        await IndexAsync();
        _reranker.FailWith = new HttpRequestException("rerank down");

        var results = await _textSearch.SearchAsync("賃料", new VaultIndexTextSearchOptions { MinRerankScore = 0.5 });

        results.Count().ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task The_options_narrow_the_search_and_link_each_passage()
    {
        await IndexAsync();

        var result = (await _textSearch.SearchAsync("賃料", new VaultIndexTextSearchOptions
        {
            DocumentTypeCode = "lease",
            MaxResultCount = 1,
            SourceLink = h => $"https://crm.test/documents/{h.DocumentId}"
        })).ShouldHaveSingleItem();

        result.SourceLink.ShouldBe($"https://crm.test/documents/{LeaseDoc}");
        (await _textSearch.SearchAsync("賃料", new VaultIndexTextSearchOptions { DocumentTypeCode = "invoice" })).ShouldBeEmpty();
    }

    private async Task IndexAsync()
    {
        GetRequiredService<IDocumentIntegrationService>().FindReadyAsync(LeaseDoc).Returns(new DocumentIntegrationDto
        {
            Id = LeaseDoc,
            DocumentTypeId = LeaseType,
            Title = "賃貸借契約書（本店）",
            Language = "ja",
            Markdown = Lease
        });
        await GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = LeaseDoc,
            Kind = IndexDocumentJobKind.Sync,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = new DateTime(2026, 11, 12, 1, 0, 0)
        });
    }

    /// <summary>Answers "ok" and records the messages and tools of the last request.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> Messages { get; private set; } = [];

        public List<AITool> Tools { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Tools = options?.Tools?.ToList() ?? [];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

[DependsOn(
    typeof(Search.VaultIndexRerankTestModule),
    typeof(VaultIndexAgentFrameworkModule))]
public class VaultIndexAgentFrameworkTestModule : AbpModule
{
}

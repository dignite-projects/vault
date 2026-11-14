using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Reranking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Index.Search;

/// <summary>Weighted fusion, and search with a reranker: reordering, the text it reads, and falling back when it fails.</summary>
public class Reranking_Tests : VaultIndexTestBase<VaultIndexRerankTestModule>
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

    private readonly FakeReranker _reranker;
    private readonly IIndexSearchAppService _search;

    public Reranking_Tests()
    {
        _reranker = GetRequiredService<FakeReranker>();
        _search = GetRequiredService<IIndexSearchAppService>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync()
            .Returns(_ => new List<DocumentTypeSummaryDto> { new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" } });
        GetRequiredService<IDocumentAppService>().GetReadScopeAsync()
            .Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
    }

    [Fact]
    public async Task The_reranker_orders_the_hits_and_reads_the_title_with_each_chunk()
    {
        await IndexAsync();
        _reranker.Prefer = "目的";

        var result = await _search.SearchAsync(new IndexSearchInput { Query = "賃料" });

        result.Reranked.ShouldBeTrue();
        result.Hits[0].HeadingPath.ShouldBe("# 賃貸借契約書 ## 第1条（目的）");
        result.Hits[0].KeywordRank.ShouldBeNull();
        result.Hits.Select(h => h.RerankScore).ShouldBeInOrder(SortDirection.Descending);
        _reranker.Calls.ShouldBe(1);
        _reranker.LastDocuments.ShouldAllBe(d => d.StartsWith("賃貸借契約書（本店）\n"));
        _reranker.LastQuery.ShouldBe("賃料");
    }

    [Fact]
    public async Task A_failing_reranker_leaves_the_fused_order()
    {
        await IndexAsync();
        _reranker.FailWith = new HttpRequestException("rerank down");

        var result = await _search.SearchAsync(new IndexSearchInput { Query = "賃料" });

        result.Reranked.ShouldBeFalse();
        result.Hits[0].HeadingPath.ShouldBe("# 賃貸借契約書 ## 第2条（賃料）");
        result.Hits.ShouldAllBe(h => h.RerankScore == null);
    }

    [Fact]
    public async Task A_reranker_that_times_out_leaves_the_fused_order()
    {
        // HttpClient.Timeout surfaces as a TaskCanceledException although nobody cancelled the search.
        await IndexAsync();
        _reranker.FailWith = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.", new TimeoutException());

        var result = await _search.SearchAsync(new IndexSearchInput { Query = "賃料" });

        result.Reranked.ShouldBeFalse();
        result.Hits[0].HeadingPath.ShouldBe("# 賃貸借契約書 ## 第2条（賃料）");
    }

    [Fact]
    public async Task Each_candidate_sent_to_the_reranker_is_capped()
    {
        await IndexAsync(markdown: "# 長文\n\n" + string.Join(' ', Enumerable.Repeat("賃料の支払について定める。", 900)), maxTokens: 6000);

        await _search.SearchAsync(new IndexSearchInput { Query = "賃料" });

        _reranker.LastDocuments.ShouldNotBeEmpty();
        _reranker.LastDocuments.ShouldAllBe(d => d.Length <= IndexSearchConsts.MaxRerankTextLength);
    }

    [Fact]
    public void Fusion_weights_scale_each_leg_and_zero_turns_one_off()
    {
        // Equal weights tie a keyword first and a vector first; the tie goes to the lower id.
        Guid a = Guid.Parse("00000000-0000-0000-0000-000000000001"), b = Guid.Parse("00000000-0000-0000-0000-000000000002"), doc = Guid.NewGuid();
        var keyword = new List<ChunkMatch> { new(a, doc, 9) };
        var vector = new List<ChunkMatch> { new(b, doc, 0.1) };

        ReciprocalRankFusion.Fuse(keyword, vector, 60).Select(f => f.ChunkId).ShouldBe([a, b]);
        ReciprocalRankFusion.Fuse(keyword, vector, 60, vectorWeight: 2).Select(f => f.ChunkId).ShouldBe([b, a]);
        ReciprocalRankFusion.Fuse(keyword, vector, 60, keywordWeight: 0.5).Select(f => f.ChunkId).ShouldBe([b, a]);
        ReciprocalRankFusion.Fuse(keyword, vector, 60, keywordWeight: 0).Select(f => f.ChunkId).ShouldBe([b]);
    }

    private async Task IndexAsync(string markdown = Lease, int? maxTokens = null)
    {
        if (maxTokens is { } max)
        {
            await WithUnitOfWorkAsync(() => GetRequiredService<Volo.Abp.Domain.Repositories.IRepository<Profiles.IndexProfile, Guid>>()
                .InsertAsync(new Profiles.IndexProfile(Guid.NewGuid(), null, LeaseType, ChunkingStrategy.Token, max, 0)));
        }

        GetRequiredService<IDocumentIntegrationService>().FindReadyAsync(LeaseDoc).Returns(new DocumentIntegrationDto
        {
            Id = LeaseDoc,
            DocumentTypeId = LeaseType,
            Title = "賃貸借契約書（本店）",
            Language = "ja",
            Markdown = markdown
        });
        await GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = LeaseDoc,
            Kind = IndexDocumentJobKind.Sync,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = new DateTime(2026, 11, 12, 1, 0, 0)
        });
    }
}

/// <summary>Scores a document 1 when it contains <see cref="Prefer"/>, else 0.1; records what it was given.</summary>
public sealed class FakeReranker : IReranker
{
    public string ModelId => "fake-reranker";

    public string? Prefer { get; set; }

    public Exception? FailWith { get; set; }

    public int Calls { get; private set; }

    public string? LastQuery { get; private set; }

    public IReadOnlyList<string> LastDocuments { get; private set; } = [];

    public Task<RerankResult> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastQuery = query;
        LastDocuments = documents;
        if (FailWith is not null)
        {
            throw FailWith;
        }

        return Task.FromResult(new RerankResult(
            documents.Select(d => Prefer is not null && d.Contains(Prefer) ? 1d : 0.1).ToList(),
            documents.Count));
    }
}

[DependsOn(typeof(VaultIndexIngestionTestModule))]
public class VaultIndexRerankTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var reranker = new FakeReranker();
        context.Services.AddSingleton(reranker);
        context.Services.AddSingleton<IReranker>(reranker);
    }
}

/// <summary>The HTTP reranker against a canned Cohere-style response.</summary>
public class HttpReranker_Tests
{
    [Fact]
    public async Task Scores_come_back_in_input_order_with_the_token_count()
    {
        var handler = new CannedHandler("""
            {"id":"x","results":[{"index":1,"document":null,"relevance_score":0.9},{"index":0,"document":null,"relevance_score":0.2}],
             "meta":{"tokens":{"input_tokens":42,"output_tokens":0}}}
            """);
        var reranker = Create(handler);

        var result = await reranker.RerankAsync("敷金", ["賃料は月額", "敷金は二か月分"]);

        result.Scores.ShouldBe([0.2, 0.9]);
        result.InputTokenCount.ShouldBe(42);
        handler.Request!.RequestUri!.ToString().ShouldBe("https://rerank.test/v1/rerank");
        handler.Request.Headers.Authorization!.Parameter.ShouldBe("key");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("model").GetString().ShouldBe("BAAI/bge-reranker-v2-m3");
        body.RootElement.GetProperty("return_documents").GetBoolean().ShouldBeFalse();
        body.RootElement.GetProperty("documents").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_response_that_skips_a_document_is_an_error()
    {
        var reranker = Create(new CannedHandler("""{"results":[{"index":0,"relevance_score":0.5}]}"""));

        await Should.ThrowAsync<InvalidOperationException>(() => reranker.RerankAsync("q", ["a", "b"]));
    }

    [Fact]
    public async Task No_documents_makes_no_call()
    {
        var handler = new CannedHandler("{}");

        (await Create(handler).RerankAsync("q", [])).Scores.ShouldBeEmpty();
        handler.Request.ShouldBeNull();
    }

    private static HttpReranker Create(CannedHandler handler)
        => new(new HttpClient(handler), Options.Create(new HttpRerankerOptions
        {
            Endpoint = "https://rerank.test/v1/",
            ApiKey = "key",
            ModelId = "BAAI/bge-reranker-v2-m3"
        }));

    private sealed class CannedHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}

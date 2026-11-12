using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Permissions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The retrieval test against search itself, with a reranker: every candidate in search's order with what each leg,
/// the fusion and the reranker made of it, and settings that change this run and nothing after it.
/// </summary>
public class IndexSearchTest_Tests : VaultIndexTestBase<VaultIndexRerankTestModule>
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
    private static readonly Guid InvoiceType = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid LeaseDoc = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private readonly FakeReranker _reranker;
    private readonly IIndexSearchAppService _search;
    private readonly IIndexSearchTestAppService _test;
    private readonly IDocumentAppService _documents;

    public IndexSearchTest_Tests()
    {
        _reranker = GetRequiredService<FakeReranker>();
        _search = GetRequiredService<IIndexSearchAppService>();
        _test = GetRequiredService<IIndexSearchTestAppService>();
        _documents = GetRequiredService<IDocumentAppService>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        ScopeIs(unrestricted: true);
    }

    [Fact]
    public async Task Shows_every_candidate_in_the_order_search_returns_them()
    {
        await IndexAsync();
        _reranker.Prefer = "目的";

        var test = await _test.RunAsync(new IndexSearchTestInput { Query = "賃料" });
        var search = await _search.SearchAsync(new IndexSearchInput { Query = "賃料", MaxResultCount = IndexSearchConsts.MaxResultCount });

        test.Candidates.Select(c => c.Pointer).ShouldBe(search.Hits.Select(h => h.Pointer));
        test.Candidates.Select(c => c.Rank).ShouldBe([1, 2, 3]);
        test.Reranked.ShouldBeTrue();
        test.RerankAvailable.ShouldBeTrue();
        test.RerankModelId.ShouldBe("fake-reranker");
        test.KeywordWeight.ShouldBe(1);
        test.VectorWeight.ShouldBe(1);
        test.CandidatesPerLeg.ShouldBe(IndexSearchConsts.CandidatesPerLeg);
        test.RerankCandidateCount.ShouldBe(30);
        test.VectorCandidateCount.ShouldBe(3);
        test.KeywordCandidateCount.ShouldBe(1);

        // The reranker lifted 第1条 to the top; fusion had put the chunk both legs found first.
        var purpose = test.Candidates[0];
        purpose.HeadingPath.ShouldBe("# 賃貸借契約書 ## 第1条（目的）");
        purpose.RerankScore.ShouldBe(1);
        purpose.KeywordRank.ShouldBeNull();
        purpose.KeywordScore.ShouldBeNull();
        var rent = test.Candidates.Single(c => c.HeadingPath == "# 賃貸借契約書 ## 第2条（賃料）");
        rent.FusedRank.ShouldBe(1);
        rent.KeywordRank.ShouldBe(1);
        rent.KeywordScore.ShouldNotBeNull();
        test.Candidates.ShouldAllBe(c => c.VectorRank != null && c.VectorDistance != null && c.DocumentTypeCode == "lease");
        rent.DocumentTitle.ShouldBe("賃貸借契約書（本店）");
    }

    [Fact]
    public async Task Settings_change_this_run_and_nothing_after_it()
    {
        await IndexAsync();

        var test = await _test.RunAsync(new IndexSearchTestInput { Query = "賃料", VectorWeight = 0, Rerank = false });

        test.Reranked.ShouldBeFalse();
        test.VectorWeight.ShouldBe(0);
        _reranker.Calls.ShouldBe(0);
        test.Candidates.ShouldHaveSingleItem().KeywordRank.ShouldBe(1);
        test.Candidates.ShouldAllBe(c => c.RerankScore == null);

        var search = await _search.SearchAsync(new IndexSearchInput { Query = "賃料" });
        search.Reranked.ShouldBeTrue();
        search.Hits.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_reranker_reads_only_its_share()
    {
        await IndexAsync();

        var test = await _test.RunAsync(new IndexSearchTestInput { Query = "賃料", RerankCandidateCount = 1 });

        _reranker.LastDocuments.Count.ShouldBe(1);
        test.Candidates[0].RerankScore.ShouldNotBeNull();
        test.Candidates.Skip(1).ShouldAllBe(c => c.RerankScore == null);
        test.Candidates.Skip(1).Select(c => c.FusedRank).ShouldBe([2, 3]);
    }

    [Fact]
    public async Task It_needs_the_status_permission_and_keeps_to_the_read_scope()
    {
        await IndexAsync();

        ScopeIs(types: [InvoiceType]);
        (await _test.RunAsync(new IndexSearchTestInput { Query = "賃料" })).Candidates.ShouldBeEmpty();

        GetRequiredService<SearchPermissionSwitch>().Denied.Add(VaultIndexPermissions.Status);
        await Should.ThrowAsync<AbpAuthorizationException>(() => _test.RunAsync(new IndexSearchTestInput { Query = "賃料" }));
    }

    [Fact]
    public async Task Field_filters_need_a_document_type()
    {
        await Should.ThrowAsync<AbpValidationException>(() => _test.RunAsync(new IndexSearchTestInput
        {
            Query = "賃料",
            FieldFilters = [new DocumentFieldFilter { Name = "rent", Min = "1" }]
        }));
    }

    private void ScopeIs(bool unrestricted = false, Guid[]? types = null)
    {
        _documents.GetReadScopeAsync().Returns(new DocumentReadScopeDto
        {
            Version = 1,
            IsUnrestricted = unrestricted,
            DocumentTypeIds = types?.ToList() ?? new List<Guid>()
        });
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
}

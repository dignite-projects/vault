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
using Dignite.Vault.Index.Profiles;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// Retrieval against SQLite, with documents indexed by the real ingestion job and the in-memory vector store. Covers
/// the P0 acceptance at the application level: an indexed document is found and read back, and nothing outside the
/// caller's read scope, tenant or retrieval state is ever returned, by search, outline or read.
/// </summary>
public class IndexSearch_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    private const string Lease = """
        # 賃貸借契約書

        本契約は、甲と乙の間で締結される。

        ## 第1条（目的）

        甲は乙に対し、下記の物件を住居として賃貸する。

        ## 第2条（賃料）

        乙は毎月末日までに賃料を支払うものとする。
        """;

    private const string Invoice = """
        # 請求書

        請求番号 INV-2026-1112 の合計金額は 211,200 円、支払期限は 2026年12月10日。
        """;

    private static readonly Guid LeaseType = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid InvoiceType = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid LeaseDoc = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid InvoiceDoc = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid SecondLeaseDoc = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid Alice = Guid.Parse("0e000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("0e000000-0000-0000-0000-0000000000b0");
    private static readonly Guid OtherTenant = Guid.Parse("7e000000-0000-0000-0000-000000000001");

    private readonly IDocumentIntegrationService _extract;
    private readonly IDocumentAppService _documents;
    private readonly IDocumentTypeAppService _types;
    private readonly FakeEmbeddingGenerator _model;
    private readonly IIndexSearchAppService _search;
    private DateTime _eventTime = new(2026, 11, 12, 1, 0, 0);

    public IndexSearch_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _documents = GetRequiredService<IDocumentAppService>();
        _types = GetRequiredService<IDocumentTypeAppService>();
        _model = GetRequiredService<FakeEmbeddingGenerator>();
        _search = GetRequiredService<IIndexSearchAppService>();

        _types.GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        ScopeIs(unrestricted: true);
    }

    [Fact]
    public async Task An_indexed_document_is_found_and_read_back()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "賃貸借契約書（本店）");

        var result = await _search.SearchAsync(new IndexSearchInput { Query = "賃料" });

        // The vector leg returns every chunk in scope, nearest first; the chunk both legs found ranks first.
        result.Hits.ShouldAllBe(h => h.DocumentId == LeaseDoc);
        var hit = result.Hits[0];
        hit.DocumentId.ShouldBe(LeaseDoc);
        hit.DocumentTitle.ShouldBe("賃貸借契約書（本店）");
        hit.DocumentTypeCode.ShouldBe("lease");
        hit.HeadingPath.ShouldBe("# 賃貸借契約書 ## 第2条（賃料）");
        hit.Text.ShouldContain("毎月末日");
        hit.KeywordRank.ShouldBe(1);
        hit.Pointer.ShouldStartWith("chunk:");

        var read = await _search.ReadAsync(new IndexReadInput { Pointer = hit.Pointer });
        read.Text.ShouldContain("乙は毎月末日までに賃料を支払うものとする。");
        read.TextTruncated.ShouldBeFalse();
        read.PreviousPointer.ShouldNotBeNull();
        read.NextPointer.ShouldBeNull();
    }

    [Fact]
    public async Task The_outline_follows_the_headings_and_a_node_reads_its_section()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "賃貸借契約書");

        var outline = await _search.GetOutlineAsync(LeaseDoc);

        outline.Nodes.Select(n => (n.Title, n.Depth)).ShouldBe(
        [
            ("賃貸借契約書", 1),
            ("第1条（目的）", 2),
            ("第2条（賃料）", 2)
        ]);
        outline.Nodes[0].ChunkCount.ShouldBe(outline.ChunkCount);
        outline.Nodes[2].Pointer.ShouldBe($"node:{LeaseDoc:D}/1.2");

        var section = await _search.ReadAsync(new IndexReadInput { Pointer = outline.Nodes[1].Pointer });
        section.Text.ShouldContain("住居として賃貸する");
        section.Text.ShouldNotContain("賃料を支払う");
        section.HeadingPath.ShouldBe("# 賃貸借契約書 ## 第1条（目的）");

        var whole = await _search.ReadAsync(new IndexReadInput { Pointer = outline.Nodes[0].Pointer });
        whole.ChunkCount.ShouldBe(outline.ChunkCount);
        whole.Text.ShouldContain("住居として賃貸する");
        whole.Text.ShouldContain("賃料を支払う");
    }

    [Fact]
    public async Task Search_returns_only_what_the_read_scope_reaches()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        await IndexAsync(InvoiceDoc, InvoiceType, Bob, Invoice, "Invoice");
        const string both = "賃料 請求書";

        ScopeIs(types: [LeaseType]);
        (await DocumentsFoundAsync(both)).ShouldBe([LeaseDoc]);

        ScopeIs(owner: Bob);
        (await DocumentsFoundAsync(both)).ShouldBe([InvoiceDoc]);

        ScopeIs(types: [LeaseType], owner: Bob);
        (await DocumentsFoundAsync(both)).ShouldBe([LeaseDoc, InvoiceDoc], ignoreOrder: true);

        ScopeIs(unrestricted: true);
        (await DocumentsFoundAsync(both)).ShouldBe([LeaseDoc, InvoiceDoc], ignoreOrder: true);
    }

    [Fact]
    public async Task Outside_the_read_scope_a_pointer_is_not_found()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        var pointer = (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits[0].Pointer;
        var node = (await _search.GetOutlineAsync(LeaseDoc)).Nodes[0].Pointer;

        ScopeIs(types: [InvoiceType], owner: Bob);

        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits.ShouldBeEmpty();
        await Should.ThrowAsync<EntityNotFoundException>(() => _search.ReadAsync(new IndexReadInput { Pointer = pointer }));
        await Should.ThrowAsync<EntityNotFoundException>(() => _search.ReadAsync(new IndexReadInput { Pointer = node }));
        await Should.ThrowAsync<EntityNotFoundException>(() => _search.GetOutlineAsync(LeaseDoc));
    }

    [Fact]
    public async Task A_document_in_the_recycle_bin_or_withheld_is_not_found_until_it_returns()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        var pointer = (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits[0].Pointer;

        await RunAsync(LeaseDoc, IndexDocumentJobKind.Archive);
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits.ShouldBeEmpty();
        await Should.ThrowAsync<EntityNotFoundException>(() => _search.ReadAsync(new IndexReadInput { Pointer = pointer }));

        await RunAsync(LeaseDoc, IndexDocumentJobKind.Unarchive);
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits.ShouldNotBeEmpty();

        await RunAsync(LeaseDoc, IndexDocumentJobKind.Withhold);
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits.ShouldBeEmpty();

        // The next Ready, with unchanged text, ends the withhold without re-embedding.
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Another_tenants_documents_are_never_returned()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease", tenantId: OtherTenant);

        // The in-memory vector leg has no tenant column, so the other tenant's chunk is a vector candidate here: the
        // tenant-filtered chunk read must drop it. On SQL Server the vector DbSet is tenant-filtered as well.
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits.ShouldBeEmpty();
        await Should.ThrowAsync<EntityNotFoundException>(() => _search.GetOutlineAsync(LeaseDoc));

        using (GetRequiredService<ICurrentTenant>().Change(OtherTenant))
        {
            (await DocumentsFoundAsync("賃料")).ShouldBe([LeaseDoc]);
        }
    }

    [Fact]
    public async Task A_type_filter_narrows_and_an_invisible_type_finds_nothing()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        await IndexAsync(InvoiceDoc, InvoiceType, Bob, Invoice, "Invoice");

        (await DocumentsFoundAsync("賃料 請求書", "invoice")).ShouldBe([InvoiceDoc]);
        (await DocumentsFoundAsync("賃料 請求書", "no-such-type")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Field_filters_search_only_the_documents_Extract_matched()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease 1");
        await IndexAsync(SecondLeaseDoc, LeaseType, Bob, Lease, "Lease 2");
        FieldsMatch(SecondLeaseDoc);

        (await DocumentsFoundAsync("賃料", "lease", RentFilter())).ShouldBe([SecondLeaseDoc]);

        await _documents.Received(1).GetIdsByFieldsAsync(Arg.Is<GetDocumentIdsByFieldsInput>(i =>
            i.DocumentTypeCode == "lease"
            && i.FieldFilters.Count == 1 && i.FieldFilters[0].Name == "rent" && i.FieldFilters[0].Min == "150000"
            && i.MaxResultCount == IndexSearchConsts.MaxFieldFilterDocuments));
    }

    [Fact]
    public async Task Field_filters_never_widen_the_read_scope()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease 1");
        await IndexAsync(SecondLeaseDoc, LeaseType, Bob, Lease, "Lease 2");
        FieldsMatch(LeaseDoc, SecondLeaseDoc);
        ScopeIs(owner: Bob);

        (await DocumentsFoundAsync("賃料", "lease", RentFilter())).ShouldBe([SecondLeaseDoc]);
    }

    [Fact]
    public async Task Field_filters_that_match_nothing_or_name_an_invisible_type_find_nothing()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        FieldsMatch();

        (await DocumentsFoundAsync("賃料", "lease", RentFilter())).ShouldBeEmpty();

        _documents.ClearReceivedCalls();
        (await DocumentsFoundAsync("賃料", "no-such-type", RentFilter())).ShouldBeEmpty();
        await _documents.DidNotReceive().GetIdsByFieldsAsync(Arg.Any<GetDocumentIdsByFieldsInput>());
    }

    [Fact]
    public async Task Field_filters_that_match_too_many_documents_are_refused()
    {
        _documents.GetIdsByFieldsAsync(Arg.Any<GetDocumentIdsByFieldsInput>())
            .Returns(new DocumentIdListDto { Ids = [LeaseDoc], HasMore = true });

        var error = await Should.ThrowAsync<BusinessException>(() => _search.SearchAsync(new IndexSearchInput
        {
            Query = "賃料",
            DocumentTypeCode = "lease",
            FieldFilters = RentFilter()
        }));
        error.Code.ShouldBe(VaultIndexErrorCodes.FieldFilterTooBroad);
    }

    [Fact]
    public async Task Field_filters_need_a_document_type()
    {
        await Should.ThrowAsync<AbpValidationException>(() => _search.SearchAsync(new IndexSearchInput
        {
            Query = "賃料",
            FieldFilters = RentFilter()
        }));
        await _documents.DidNotReceive().GetIdsByFieldsAsync(Arg.Any<GetDocumentIdsByFieldsInput>());
    }

    [Fact]
    public async Task Hits_are_capped_and_a_long_chunk_is_cut_and_says_so()
    {
        await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexProfile, Guid>>().InsertAsync(
            new IndexProfile(Guid.NewGuid(), null, LeaseType, ChunkingStrategy.Token, 3000, 0)));
        var longText = string.Join(' ', Enumerable.Repeat("rent payable monthly", 400));
        await IndexAsync(LeaseDoc, LeaseType, Alice, longText, "Long");

        var hit = (await _search.SearchAsync(new IndexSearchInput { Query = "rent", MaxResultCount = 1 })).Hits.ShouldHaveSingleItem();

        hit.Text.Length.ShouldBe(IndexSearchConsts.MaxHitTextLength);
        hit.TextTruncated.ShouldBeTrue();
        hit.TextTotalLength.ShouldBeGreaterThan(IndexSearchConsts.MaxHitTextLength);
    }

    [Fact]
    public async Task Search_outline_and_read_need_the_search_permission()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        GetRequiredService<SearchPermissionSwitch>().Denied.Add(VaultIndexPermissions.Search);

        await Should.ThrowAsync<AbpAuthorizationException>(() => _search.SearchAsync(new IndexSearchInput { Query = "賃料" }));
        await Should.ThrowAsync<AbpAuthorizationException>(() => _search.GetOutlineAsync(LeaseDoc));
        await Should.ThrowAsync<AbpAuthorizationException>(() => _search.ReadAsync(new IndexReadInput { Pointer = "chunk:" + Guid.NewGuid() }));
    }

    [Fact]
    public async Task A_caller_Extract_refuses_a_read_scope_is_refused()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        _documents.GetReadScopeAsync().ThrowsAsync(new AbpAuthorizationException("no entry"));

        await Should.ThrowAsync<AbpAuthorizationException>(() => _search.SearchAsync(new IndexSearchInput { Query = "賃料" }));
    }

    [Fact]
    public async Task A_read_scope_version_this_index_does_not_know_is_refused()
    {
        _documents.GetReadScopeAsync().Returns(new DocumentReadScopeDto { Version = 2, IsUnrestricted = true });

        var error = await Should.ThrowAsync<BusinessException>(() => _search.SearchAsync(new IndexSearchInput { Query = "賃料" }));
        error.Code.ShouldBe(VaultIndexErrorCodes.UnsupportedReadScope);
    }

    [Fact]
    public async Task Searching_with_another_model_than_the_serving_generation_is_refused()
    {
        await IndexAsync(LeaseDoc, LeaseType, Alice, Lease, "Lease");
        _model.ModelId = "another-model";

        var error = await Should.ThrowAsync<BusinessException>(() => _search.SearchAsync(new IndexSearchInput { Query = "賃料" }));
        error.Code.ShouldBe(VaultIndexErrorCodes.EmbeddingModelChanged);
    }

    [Fact]
    public async Task A_pointer_search_and_outline_did_not_return_is_refused()
    {
        var error = await Should.ThrowAsync<BusinessException>(() => _search.ReadAsync(new IndexReadInput { Pointer = "VaultDocuments.Markdown[0:500]" }));
        error.Code.ShouldBe(VaultIndexErrorCodes.InvalidPointer);
    }

    [Fact]
    public async Task Nothing_indexed_finds_nothing()
    {
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits.ShouldBeEmpty();
    }

    private async Task<List<Guid>> DocumentsFoundAsync(string query, string? typeCode = null, List<DocumentFieldFilter>? fieldFilters = null)
        => (await _search.SearchAsync(new IndexSearchInput
        {
            Query = query,
            DocumentTypeCode = typeCode,
            FieldFilters = fieldFilters,
            MaxResultCount = IndexSearchConsts.MaxResultCount
        }))
            .Hits.Select(h => h.DocumentId).Distinct().ToList();

    private static List<DocumentFieldFilter> RentFilter() => [new() { Name = "rent", Min = "150000" }];

    private void FieldsMatch(params Guid[] documentIds)
        => _documents.GetIdsByFieldsAsync(Arg.Any<GetDocumentIdsByFieldsInput>())
            .Returns(new DocumentIdListDto { Ids = documentIds.ToList() });

    private void ScopeIs(bool unrestricted = false, Guid[]? types = null, Guid? owner = null)
    {
        _documents.GetReadScopeAsync().Returns(new DocumentReadScopeDto
        {
            Version = 1,
            IsUnrestricted = unrestricted,
            DocumentTypeIds = types?.ToList() ?? new List<Guid>(),
            OwnerId = owner
        });
    }

    private async Task IndexAsync(Guid documentId, Guid typeId, Guid ownerId, string markdown, string title, Guid? tenantId = null)
    {
        _extract.FindReadyAsync(documentId).Returns(new DocumentIntegrationDto
        {
            Id = documentId,
            DocumentTypeId = typeId,
            OwnerId = ownerId,
            Title = title,
            Language = "ja",
            Markdown = markdown
        });

        await RunAsync(documentId, IndexDocumentJobKind.Sync, tenantId);
    }

    private Task RunAsync(Guid documentId, IndexDocumentJobKind kind, Guid? tenantId = null)
    {
        _eventTime = _eventTime.AddMinutes(1);
        return GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = documentId,
            TenantId = tenantId,
            Kind = kind,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = _eventTime
        });
    }
}

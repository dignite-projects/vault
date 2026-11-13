using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Compilation;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Mcp.Search;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Profiles;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Xunit;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The type catalog: the documents of one type a caller may read, by title, with their summaries. It is derived when
/// read, through the same scope as search, so it follows the read rule per document, deletions and the recycle bin at
/// once, and shows a summary only while it matches the document's chunks.
/// </summary>
public class IndexCatalog_Tests : VaultIndexTestBase<VaultIndexSummaryTestModule>
{
    private const string Lease = """
        # 賃貸借契約書

        本契約は、甲と乙の間で締結される。

        ## 第2条（賃料）

        乙は毎月末日までに賃料を支払うものとする。
        """;

    private static readonly Guid LeaseType = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid InvoiceType = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid LeaseA = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid LeaseB = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid LeaseC = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid Invoice = Guid.Parse("d0000000-0000-0000-0000-000000000004");
    private static readonly Guid Alice = Guid.Parse("0e000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("0e000000-0000-0000-0000-0000000000b0");
    private static readonly Guid OtherTenant = Guid.Parse("7e000000-0000-0000-0000-000000000001");

    private readonly IDocumentIntegrationService _extract;
    private readonly IDocumentAppService _documents;
    private readonly IIndexSearchAppService _search;
    private DateTime _eventTime = new(2026, 11, 13, 1, 0, 0);

    public IndexCatalog_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _documents = GetRequiredService<IDocumentAppService>();
        _search = GetRequiredService<IIndexSearchAppService>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        ScopeIs(unrestricted: true);
    }

    [Fact]
    public async Task It_lists_a_types_documents_by_title_a_page_at_a_time()
    {
        await IndexAllAsync();

        var catalog = await CatalogAsync();
        catalog.DocumentTypeCode.ShouldBe("lease");
        catalog.TotalCount.ShouldBe(3);
        // Title order is not id order, so a sort by id would show here.
        catalog.Items.Select(i => (i.Title, i.DocumentId)).ShouldBe([("Lease 1", LeaseC), ("Lease 2", LeaseA), ("Lease 3", LeaseB)]);
        catalog.Items.ShouldAllBe(i => i.Summary == null && i.LastIndexedTime != null);

        var page = await _search.GetCatalogAsync(new GetIndexCatalogInput { DocumentTypeCode = "lease", SkipCount = 1, MaxResultCount = 1 });
        (page.TotalCount, page.Items.Single().DocumentId).ShouldBe((3, LeaseA));
    }

    [Fact]
    public async Task A_caller_who_reads_only_their_own_uploads_sees_only_those()
    {
        await IndexAllAsync();

        ScopeIs(owner: Bob);
        (await CatalogAsync()).Items.Select(i => i.DocumentId).ShouldBe([LeaseB]);

        ScopeIs(types: [LeaseType]);
        (await CatalogAsync()).TotalCount.ShouldBe(3);

        ScopeIs(types: [InvoiceType]);
        (await CatalogAsync()).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deleted_or_binned_document_leaves_the_catalog_at_once()
    {
        await IndexAllAsync();

        await RunAsync(LeaseA, IndexDocumentJobKind.Remove);
        await RunAsync(LeaseB, IndexDocumentJobKind.Archive);

        var catalog = await CatalogAsync();
        (catalog.TotalCount, catalog.Items.Single().DocumentId).ShouldBe((1, LeaseC));
    }

    [Fact]
    public async Task Another_tenants_documents_are_not_listed()
    {
        await IndexAsync(LeaseA, LeaseType, Alice, "Lease A");
        await IndexAsync(LeaseB, LeaseType, Alice, "Lease B", OtherTenant);

        (await CatalogAsync()).Items.Select(i => i.DocumentId).ShouldBe([LeaseA]);
    }

    [Fact]
    public async Task Field_filters_narrow_it_to_the_documents_Extract_matched()
    {
        await IndexAllAsync();
        _documents.GetIdsByFieldsAsync(Arg.Any<GetDocumentIdsByFieldsInput>()).Returns(new DocumentIdListDto { Ids = [LeaseC] });

        var catalog = await _search.GetCatalogAsync(new GetIndexCatalogInput
        {
            DocumentTypeCode = "lease",
            FieldFilters = [new() { Name = "rent", Min = "150000" }]
        });

        catalog.Items.Select(i => i.DocumentId).ShouldBe([LeaseC]);
    }

    [Fact]
    public async Task A_summary_is_shown_only_while_it_matches_the_documents_chunks()
    {
        await GetRequiredService<IIndexProfileAppService>().SetAsync(new IndexProfileInput { DocumentTypeId = LeaseType, Compilation = IndexCompilation.Summaries });
        await IndexAllAsync();
        await CompileAsync(LeaseA);

        var catalog = await CatalogAsync();
        catalog.Items.Single(i => i.DocumentId == LeaseA).Summary.ShouldBe("the whole document");
        catalog.Items.Where(i => i.DocumentId != LeaseA).ShouldAllBe(i => i.Summary == null);

        var chunk = (await WithUnitOfWorkAsync(() => GetRequiredService<IChunkRepository>().GetListAsync(c => c.DocumentId == LeaseA)))
            .Single(c => c.HeadingPath!.Contains("第2条"));
        await GetRequiredService<IIndexChunkAppService>().DisableAsync(chunk.Id);
        (await CatalogAsync()).Items.Single(i => i.DocumentId == LeaseA).Summary.ShouldBeNull();
    }

    [Fact]
    public async Task It_needs_the_search_permission_and_lists_nothing_for_an_unknown_type()
    {
        await IndexAllAsync();

        (await _search.GetCatalogAsync(new GetIndexCatalogInput { DocumentTypeCode = "no-such-type" })).Items.ShouldBeEmpty();

        GetRequiredService<SearchPermissionSwitch>().Denied.Add(VaultIndexPermissions.Search);
        await Should.ThrowAsync<AbpAuthorizationException>(() => CatalogAsync());
    }

    [Fact]
    public async Task The_mcp_catalog_wraps_titles_and_summaries_and_says_when_more_follow()
    {
        await GetRequiredService<IIndexProfileAppService>().SetAsync(new IndexProfileInput { DocumentTypeId = LeaseType, Compilation = IndexCompilation.Summaries });
        await IndexAllAsync();
        await CompileAsync(LeaseA);

        var first = await IndexSearchTools.CatalogAsync("lease", _search, maxResults: 2);
        first.Items.Select(i => i.Title).ShouldBe(["<field>\nLease 1\n</field>", "<field>\nLease 2\n</field>"]);
        first.Items[1].Summary.ShouldBe("<field>\nthe whole document\n</field>");
        (first.TotalCount, first.Truncated).ShouldBe((3, true));

        var last = await IndexSearchTools.CatalogAsync("lease", _search, skip: 2, maxResults: 2);
        (last.Items.Count, last.Truncated).ShouldBe((1, false));
    }

    private Task<IndexCatalogDto> CatalogAsync() => _search.GetCatalogAsync(new GetIndexCatalogInput { DocumentTypeCode = "lease" });

    private async Task IndexAllAsync()
    {
        await IndexAsync(LeaseC, LeaseType, Alice, "Lease 1");
        await IndexAsync(LeaseA, LeaseType, Alice, "Lease 2");
        await IndexAsync(LeaseB, LeaseType, Bob, "Lease 3");
        await IndexAsync(Invoice, InvoiceType, Alice, "Invoice");
    }

    private void ScopeIs(bool unrestricted = false, Guid[]? types = null, Guid? owner = null)
        => _documents.GetReadScopeAsync().Returns(new DocumentReadScopeDto
        {
            Version = 1,
            IsUnrestricted = unrestricted,
            DocumentTypeIds = types?.ToList() ?? new List<Guid>(),
            OwnerId = owner
        });

    private async Task IndexAsync(Guid documentId, Guid typeId, Guid ownerId, string title, Guid? tenantId = null)
    {
        _extract.FindReadyAsync(documentId).Returns(new DocumentIntegrationDto
        {
            Id = documentId,
            DocumentTypeId = typeId,
            OwnerId = ownerId,
            Title = title,
            Language = "ja",
            Markdown = Lease
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

    private Task CompileAsync(Guid documentId)
        => GetRequiredService<IndexCompileJob>().ExecuteAsync(new IndexCompileJobArgs { DocumentId = documentId });
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Search;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Entities;
using Xunit;

namespace Dignite.Vault.Index.Status;

/// <summary>
/// The chunk view on SQLite with the real ingestion job: listing a document's chunks within the read scope, and the
/// disable overlay, which takes a chunk out of search, outline and read, follows its text across a rebuild, and ends
/// when the text changes.
/// </summary>
public class IndexChunk_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    private const string Lease = """
        # 賃貸借契約書

        本契約は、甲と乙の間で締結される。

        ## 第1条（目的）

        甲は乙に対し、下記の物件を住居として賃貸する。

        ## 第2条（賃料）

        乙は毎月末日までに賃料を支払うものとする。
        """;

    private const string RentHeading = "# 賃貸借契約書 ## 第2条（賃料）";

    private static readonly Guid LeaseType = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid InvoiceType = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid LeaseDoc = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Alice = Guid.Parse("0e000000-0000-0000-0000-0000000000a1");

    private readonly IDocumentIntegrationService _extract;
    private readonly IDocumentAppService _documents;
    private readonly IIndexChunkAppService _chunks;
    private readonly IIndexSearchAppService _search;
    private readonly InMemoryChunkSearchIndex _store;
    private DateTime _eventTime = new(2026, 11, 12, 1, 0, 0);

    public IndexChunk_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _documents = GetRequiredService<IDocumentAppService>();
        _chunks = GetRequiredService<IIndexChunkAppService>();
        _search = GetRequiredService<IIndexSearchAppService>();
        _store = GetRequiredService<InMemoryChunkSearchIndex>();

        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        ScopeIs(unrestricted: true);
    }

    [Fact]
    public async Task Lists_a_documents_chunks_in_order()
    {
        await IndexAsync(Lease);

        var list = await ListAsync();

        list.DocumentTitle.ShouldBe("賃貸借契約書（本店）");
        list.DocumentTypeCode.ShouldBe("lease");
        list.Status.ShouldBe(IndexedDocumentStatus.Indexed);
        list.TotalCount.ShouldBe(3);
        list.DisabledCount.ShouldBe(0);
        list.Items.Select(c => c.Ordinal).ShouldBe([0, 1, 2]);
        list.Items[2].HeadingPath.ShouldBe(RentHeading);
        list.Items[2].Text.ShouldContain("賃料を支払う");
        list.Items[2].Pointer.ShouldBe("chunk:" + list.Items[2].Id.ToString("D"));

        var page = await _chunks.GetListAsync(new GetIndexChunkListInput { DocumentId = LeaseDoc, SkipCount = 1, MaxResultCount = 1 });
        page.TotalCount.ShouldBe(3);
        page.Items.ShouldHaveSingleItem().Ordinal.ShouldBe(1);
    }

    [Fact]
    public async Task A_disabled_chunk_leaves_search_outline_and_read_until_it_is_enabled()
    {
        await IndexAsync(Lease);
        var rent = await RentChunkAsync();

        await _chunks.DisableAsync(rent.Id);

        (await RentChunkAsync()).IsDisabled.ShouldBeTrue();
        (await ListAsync()).DisabledCount.ShouldBe(1);
        _store.Vectors.Values.Single(v => v.ChunkId == rent.Id).IsDisabled.ShouldBeTrue();
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料", MaxResultCount = 20 })).Hits
            .ShouldAllBe(h => h.Pointer != rent.Pointer);
        await Should.ThrowAsync<EntityNotFoundException>(() => _search.ReadAsync(new IndexReadInput { Pointer = rent.Pointer }));
        (await _search.GetOutlineAsync(LeaseDoc)).Nodes.ShouldAllBe(n => n.Title != "第2条（賃料）");

        await _chunks.EnableAsync(rent.Id);

        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料" })).Hits[0].Pointer.ShouldBe(rent.Pointer);
        _store.Vectors.Values.Single(v => v.ChunkId == rent.Id).IsDisabled.ShouldBeFalse();
    }

    [Fact]
    public async Task The_mark_follows_the_text_across_a_rebuild_and_ends_when_the_text_changes()
    {
        await IndexAsync(Lease);
        var rent = await RentChunkAsync();
        await _chunks.DisableAsync(rent.Id);

        await IndexAsync(Lease, force: true);

        var rebuilt = await RentChunkAsync();
        rebuilt.Id.ShouldNotBe(rent.Id);
        rebuilt.IsDisabled.ShouldBeTrue();
        (await ListAsync()).DisabledCount.ShouldBe(1);
        _store.Vectors.Values.Single(v => v.ChunkId == rebuilt.Id).IsDisabled.ShouldBeTrue();
        (await _search.SearchAsync(new IndexSearchInput { Query = "賃料", MaxResultCount = 20 })).Hits
            .ShouldAllBe(h => h.HeadingPath != RentHeading);

        await IndexAsync(Lease.Replace("毎月末日", "毎月25日"));

        (await RentChunkAsync()).IsDisabled.ShouldBeFalse();
        (await ListAsync()).DisabledCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_hidden_documents_chunks_are_still_listed()
    {
        await IndexAsync(Lease);
        await RunAsync(IndexDocumentJobKind.Archive);

        var list = await ListAsync();

        list.IsHidden.ShouldBeTrue();
        list.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task Listing_needs_the_status_permission_and_disabling_needs_manage_chunks()
    {
        await IndexAsync(Lease);
        var rent = await RentChunkAsync();
        var permissions = GetRequiredService<SearchPermissionSwitch>();

        permissions.Denied.Add(VaultIndexPermissions.StatusManageChunks);
        await Should.ThrowAsync<AbpAuthorizationException>(() => _chunks.DisableAsync(rent.Id));
        await Should.ThrowAsync<AbpAuthorizationException>(() => _chunks.EnableAsync(rent.Id));
        (await RentChunkAsync()).IsDisabled.ShouldBeFalse();

        permissions.Denied.Add(VaultIndexPermissions.Status);
        await Should.ThrowAsync<AbpAuthorizationException>(ListAsync);
    }

    [Fact]
    public async Task Outside_the_read_scope_a_document_and_its_chunks_are_not_found()
    {
        await IndexAsync(Lease);
        var rent = await RentChunkAsync();

        ScopeIs(types: [InvoiceType]);

        await Should.ThrowAsync<EntityNotFoundException>(ListAsync);
        await Should.ThrowAsync<EntityNotFoundException>(() => _chunks.DisableAsync(rent.Id));

        ScopeIs(unrestricted: true);
        (await RentChunkAsync()).IsDisabled.ShouldBeFalse();
    }

    private Task<IndexChunkListDto> ListAsync()
        => _chunks.GetListAsync(new GetIndexChunkListInput { DocumentId = LeaseDoc });

    private async Task<IndexChunkDto> RentChunkAsync()
        => (await ListAsync()).Items.Single(c => c.HeadingPath == RentHeading);

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

    private async Task IndexAsync(string markdown, bool force = false)
    {
        _extract.FindReadyAsync(LeaseDoc).Returns(new DocumentIntegrationDto
        {
            Id = LeaseDoc,
            DocumentTypeId = LeaseType,
            OwnerId = Alice,
            Title = "賃貸借契約書（本店）",
            Language = "ja",
            Markdown = markdown
        });

        await RunAsync(IndexDocumentJobKind.Sync, force);
    }

    private Task RunAsync(IndexDocumentJobKind kind, bool force = false)
    {
        _eventTime = _eventTime.AddMinutes(1);
        return GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = LeaseDoc,
            Kind = kind,
            Trigger = force ? IndexRunTrigger.Manual : IndexRunTrigger.DocumentReady,
            EventTime = force ? null : _eventTime,
            Force = force
        });
    }
}

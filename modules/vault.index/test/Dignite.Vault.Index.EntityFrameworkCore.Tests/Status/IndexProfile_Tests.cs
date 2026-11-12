using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Profiles;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Vault.Index.Status;

/// <summary>
/// The index profile page on SQLite with the real ingestion job: the settings each type gets, the rebuild estimate
/// before a change, and the change itself, which queues a rebuild that re-chunks exactly the documents it affects.
/// </summary>
public class IndexProfile_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
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
    private static readonly Guid LeaseA = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid LeaseB = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid Invoice = Guid.Parse("d0000000-0000-0000-0000-000000000003");

    private readonly IIndexProfileAppService _profiles;
    private readonly IBackgroundJobManager _jobs;
    private DateTime _eventTime = new(2026, 11, 12, 1, 0, 0);

    public IndexProfile_Tests()
    {
        _profiles = GetRequiredService<IIndexProfileAppService>();
        _jobs = GetRequiredService<IBackgroundJobManager>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        GetRequiredService<IDocumentAppService>().GetReadScopeAsync()
            .Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
    }

    [Fact]
    public async Task Each_type_shows_the_settings_that_apply_to_it()
    {
        await IndexAllAsync();
        await _profiles.SetAsync(new IndexProfileInput { ChunkingStrategy = ChunkingStrategy.Token, MaxTokensPerChunk = 600, OverlapTokens = 60 });
        await _profiles.SetAsync(new IndexProfileInput { DocumentTypeId = InvoiceType, MaxTokensPerChunk = 200 });

        var rows = (await _profiles.GetListAsync()).Items;

        rows.Select(r => r.DocumentTypeCode).ShouldBe([null, "invoice", "lease"]);
        rows[0].HasOwnProfile.ShouldBeTrue();
        rows[0].IndexedDocumentCount.ShouldBe(3);
        var invoice = rows.Single(r => r.DocumentTypeCode == "invoice");
        (invoice.HasOwnProfile, invoice.ChunkingStrategy, invoice.MaxTokensPerChunk).ShouldBe((true, ChunkingStrategy.Heading, 200));
        var lease = rows.Single(r => r.DocumentTypeCode == "lease");
        (lease.HasOwnProfile, lease.ChunkingStrategy, lease.MaxTokensPerChunk, lease.OverlapTokens).ShouldBe((false, ChunkingStrategy.Token, 600, 60));
        lease.IndexedDocumentCount.ShouldBe(2);
    }

    [Fact]
    public async Task The_estimate_counts_the_documents_a_change_would_rebuild_and_their_last_tokens()
    {
        await IndexAllAsync();
        // A later, smaller version of one lease (two chunks instead of three): only its latest run's tokens count.
        await IndexAsync(LeaseA, LeaseType, Lease[..Lease.IndexOf("## 第2条", StringComparison.Ordinal)]);

        var lease = await _profiles.GetRebuildEstimateAsync(new IndexProfileInput { DocumentTypeId = LeaseType, MaxTokensPerChunk = 200 });

        lease.DocumentCount.ShouldBe(2);
        lease.ChunkCount.ShouldBe(5);
        // The fake model reports 10 tokens per input: 2 chunks in the latest run of one lease, 3 in the other.
        lease.EmbeddingTokenCount.ShouldBe(50);
        lease.DocumentsWithoutTokenCount.ShouldBe(0);

        // The settings in use change nothing.
        (await _profiles.GetRebuildEstimateAsync(new IndexProfileInput { DocumentTypeId = LeaseType })).DocumentCount.ShouldBe(0);

        // The layer default does not reach a type with its own profile.
        await _profiles.SetAsync(new IndexProfileInput { DocumentTypeId = InvoiceType });
        (await _profiles.GetRebuildEstimateAsync(new IndexProfileInput { MaxTokensPerChunk = 200 })).DocumentCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_change_queues_a_rebuild_that_rechunks_only_the_affected_documents()
    {
        await IndexAllAsync();
        _jobs.ClearReceivedCalls();

        await _profiles.SetAsync(new IndexProfileInput { DocumentTypeId = LeaseType, ChunkingStrategy = ChunkingStrategy.Token, MaxTokensPerChunk = 1000 });

        var rebuild = _jobs.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<IndexBackfillJobArgs>()
            .ShouldHaveSingleItem();
        rebuild.IndexedOnly.ShouldBeTrue();
        rebuild.DocumentTypeId.ShouldBe(LeaseType);

        _jobs.ClearReceivedCalls();
        await GetRequiredService<IndexBackfillJob>().ExecuteAsync(rebuild);
        var syncs = _jobs.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<IndexDocumentJobArgs>().ToList();
        syncs.Select(s => s.DocumentId).ShouldBe([LeaseA, LeaseB], ignoreOrder: true);
        syncs.ShouldAllBe(s => s.Trigger == IndexRunTrigger.Rebuild && s.Kind == IndexDocumentJobKind.Sync);

        foreach (var sync in syncs)
        {
            await GetRequiredService<IndexDocumentJob>().ExecuteAsync(sync);
        }

        var documents = await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexedDocument, Guid>>().GetListAsync());
        documents.Single(d => d.Id == LeaseA).ProfileStamp.ShouldBe("Token:1000:0");
        documents.Single(d => d.Id == Invoice).ProfileStamp.ShouldBe(ResolvedIndexProfile.BuiltIn.Stamp);
        (await _profiles.GetRebuildEstimateAsync(new IndexProfileInput
        {
            DocumentTypeId = LeaseType,
            ChunkingStrategy = ChunkingStrategy.Token,
            MaxTokensPerChunk = 1000
        })).DocumentCount.ShouldBe(0);
    }

    [Fact]
    public async Task Resetting_a_type_returns_it_to_the_layer_default_and_rebuilds_it()
    {
        await IndexAllAsync();
        await _profiles.SetAsync(new IndexProfileInput { DocumentTypeId = InvoiceType, MaxTokensPerChunk = 200 });
        _jobs.ClearReceivedCalls();

        await _profiles.ResetAsync(InvoiceType);

        (await _profiles.GetListAsync()).Items.Single(r => r.DocumentTypeCode == "invoice").HasOwnProfile.ShouldBeFalse();
        _jobs.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<IndexBackfillJobArgs>()
            .ShouldHaveSingleItem().DocumentTypeId.ShouldBe(InvoiceType);

        _jobs.ClearReceivedCalls();
        await _profiles.ResetAsync(InvoiceType);
        _jobs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Profiles_need_their_permission_and_a_visible_type()
    {
        await Should.ThrowAsync<EntityNotFoundException>(() => _profiles.SetAsync(new IndexProfileInput { DocumentTypeId = Guid.NewGuid() }));
        await Should.ThrowAsync<AbpValidationException>(() => _profiles.SetAsync(new IndexProfileInput { MaxTokensPerChunk = 200, OverlapTokens = 200 }));

        GetRequiredService<SearchPermissionSwitch>().Denied.Add(VaultIndexPermissions.Profiles);
        await Should.ThrowAsync<AbpAuthorizationException>(() => _profiles.GetListAsync());
        await Should.ThrowAsync<AbpAuthorizationException>(() => _profiles.GetRebuildEstimateAsync(new IndexProfileInput()));
        await Should.ThrowAsync<AbpAuthorizationException>(() => _profiles.SetAsync(new IndexProfileInput()));
        await Should.ThrowAsync<AbpAuthorizationException>(() => _profiles.ResetAsync(LeaseType));
    }

    private async Task IndexAllAsync()
    {
        await IndexAsync(LeaseA, LeaseType, Lease);
        await IndexAsync(LeaseB, LeaseType, Lease.Replace("末日", "25日"));
        await IndexAsync(Invoice, InvoiceType, "# 請求書\n\n合計 211,200 円。\n");
    }

    private async Task IndexAsync(Guid documentId, Guid typeId, string markdown)
    {
        GetRequiredService<IDocumentIntegrationService>().FindReadyAsync(documentId).Returns(new DocumentIntegrationDto
        {
            Id = documentId,
            DocumentTypeId = typeId,
            Title = "T",
            Language = "ja",
            Markdown = markdown
        });
        _eventTime = _eventTime.AddMinutes(1);
        await GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = documentId,
            Kind = IndexDocumentJobKind.Sync,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = _eventTime
        });
    }
}

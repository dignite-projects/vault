using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Runs;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace Dignite.Vault.Index.Status;

/// <summary>The index status page: counts, the read-scoped document list and runs, and a forced rebuild.</summary>
public class IndexStatus_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    private static readonly Guid LeaseType = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid InvoiceType = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Lease = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Invoice = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid Empty = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid Alice = Guid.Parse("0e000000-0000-0000-0000-0000000000a1");

    private readonly IDocumentIntegrationService _extract;
    private readonly IDocumentAppService _documents;
    private readonly IBackgroundJobManager _jobs;
    private readonly FakeEmbeddingGenerator _model;
    private readonly IIndexStatusAppService _status;
    private DateTime _eventTime = new(2026, 11, 12, 1, 0, 0);

    public IndexStatus_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _documents = GetRequiredService<IDocumentAppService>();
        _jobs = GetRequiredService<IBackgroundJobManager>();
        _model = GetRequiredService<FakeEmbeddingGenerator>();
        _status = GetRequiredService<IIndexStatusAppService>();

        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        ScopeIs(unrestricted: true);
    }

    [Fact]
    public async Task The_summary_counts_states_chunks_and_the_serving_generation()
    {
        await IndexAsync(Lease, LeaseType, "# 契約\n\n## 賃料\n\n月額。\n\n## 敷金\n\n二か月分。", "賃貸借契約書");
        await IndexAsync(Invoice, InvoiceType, "# 請求書\n\n合計 10,000 円。", "請求書");
        await IndexAsync(Empty, InvoiceType, "   ", "空の文書");
        await RunAsync(Invoice, IndexDocumentJobKind.Archive);

        var summary = await _status.GetSummaryAsync();

        summary.IndexedCount.ShouldBe(2);
        summary.FailedCount.ShouldBe(1);
        summary.ArchivedCount.ShouldBe(1);
        summary.WithheldCount.ShouldBe(0);
        summary.ChunkCount.ShouldBeGreaterThanOrEqualTo(3);
        summary.VectorCount.ShouldBe(summary.ChunkCount);
        summary.ServingModelId.ShouldBe("fake-embedding");
        summary.ConfiguredModelId.ShouldBe("fake-embedding");
        summary.ServingDimensions.ShouldBe(8);
        summary.KeywordIndexAvailable.ShouldBeTrue();
        summary.ExactSearchChunkLimit.ShouldBe(50_000);
    }

    [Fact]
    public async Task The_document_list_shows_only_the_read_scope_and_the_failure_reason()
    {
        await IndexAsync(Lease, LeaseType, "# 契約\n\n賃料は月額。", "賃貸借契約書");
        await IndexAsync(Empty, InvoiceType, "   ", "空の請求書");

        var all = await _status.GetDocumentListAsync(new GetIndexedDocumentListInput());
        all.TotalCount.ShouldBe(2);
        var failed = all.Items.Single(d => d.DocumentId == Empty);
        failed.Status.ShouldBe(IndexedDocumentStatus.Failed);
        failed.LastError.ShouldStartWith(VaultIndexErrorCodes.EmptyDocument);
        failed.DocumentTypeCode.ShouldBe("invoice");

        (await _status.GetDocumentListAsync(new GetIndexedDocumentListInput { Status = IndexedDocumentStatus.Indexed }))
            .Items.Select(d => d.DocumentId).ShouldBe([Lease]);
        (await _status.GetDocumentListAsync(new GetIndexedDocumentListInput { Filter = "請求" }))
            .Items.Select(d => d.DocumentId).ShouldBe([Empty]);

        ScopeIs(types: [LeaseType]);
        (await _status.GetDocumentListAsync(new GetIndexedDocumentListInput())).Items.Select(d => d.DocumentId).ShouldBe([Lease]);
        await Should.ThrowAsync<EntityNotFoundException>(() => _status.GetRunsAsync(Empty));
        await Should.ThrowAsync<EntityNotFoundException>(() => _status.RebuildAsync(Empty));
    }

    [Fact]
    public async Task Runs_are_listed_newest_first()
    {
        await IndexAsync(Lease, LeaseType, "# 契約\n\n賃料は月額。", "賃貸借契約書");
        await IndexAsync(Lease, LeaseType, "# 契約\n\n賃料は月額。", "賃貸借契約書");

        var runs = (await _status.GetRunsAsync(Lease)).Items;

        runs.Select(r => r.Status).ShouldBe([IndexRunStatus.Skipped, IndexRunStatus.Succeeded]);
    }

    [Fact]
    public async Task A_rebuild_queues_a_forced_manual_sync_that_re_embeds_unchanged_text()
    {
        await IndexAsync(Lease, LeaseType, "# 契約\n\n賃料は月額。", "賃貸借契約書");

        await _status.RebuildAsync(Lease);

        var args = (IndexDocumentJobArgs)_jobs.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobManager.EnqueueAsync) && c.GetArguments()[0] is IndexDocumentJobArgs)
            .GetArguments()[0]!;
        args.DocumentId.ShouldBe(Lease);
        args.Kind.ShouldBe(IndexDocumentJobKind.Sync);
        args.Trigger.ShouldBe(IndexRunTrigger.Manual);
        args.Force.ShouldBeTrue();

        var calls = _model.Calls;
        await GetRequiredService<IndexDocumentJob>().ExecuteAsync(args);

        _model.Calls.ShouldBeGreaterThan(calls);
        var latest = (await GetRequiredService<IRepository<IndexRun, Guid>>().GetListAsync()).OrderBy(r => r.StartedTime).Last();
        latest.Status.ShouldBe(IndexRunStatus.Succeeded);
        latest.Trigger.ShouldBe(IndexRunTrigger.Manual);
    }

    [Fact]
    public async Task Without_a_chat_model_no_summaries_are_pending_and_a_recompile_is_refused()
    {
        await GetRequiredService<IIndexProfileAppService>().SetAsync(new IndexProfileInput { DocumentTypeId = LeaseType, Compilation = IndexCompilation.Summaries });
        await IndexAsync(Lease, LeaseType, "# 契約\n\n賃料は月額。", "賃貸借契約書");

        (await _status.GetSummaryAsync()).ChatModelAvailable.ShouldBeFalse();
        (await _status.GetDocumentListAsync(new GetIndexedDocumentListInput())).Items.Single().SummariesState.ShouldBe(IndexArtifactState.None);
        (await Should.ThrowAsync<BusinessException>(() => _status.RecompileAsync(Lease))).Code.ShouldBe(VaultIndexErrorCodes.ChatModelUnavailable);
    }

    [Fact]
    public async Task The_page_needs_the_status_permission_and_a_rebuild_its_child()
    {
        await IndexAsync(Lease, LeaseType, "# 契約\n\n賃料は月額。", "賃貸借契約書");
        var permissions = GetRequiredService<SearchPermissionSwitch>();

        permissions.Denied.Add(VaultIndexPermissions.StatusRebuild);
        await Should.ThrowAsync<AbpAuthorizationException>(() => _status.RebuildAsync(Lease));
        (await _status.GetSummaryAsync()).IndexedCount.ShouldBe(1);

        permissions.Denied.Add(VaultIndexPermissions.Status);
        await Should.ThrowAsync<AbpAuthorizationException>(() => _status.GetSummaryAsync());
        await Should.ThrowAsync<AbpAuthorizationException>(() => _status.GetDocumentListAsync(new GetIndexedDocumentListInput()));
        await Should.ThrowAsync<AbpAuthorizationException>(() => _status.GetRunsAsync(Lease));
    }

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

    private async Task IndexAsync(Guid documentId, Guid typeId, string markdown, string title)
    {
        _extract.FindReadyAsync(documentId).Returns(new DocumentIntegrationDto
        {
            Id = documentId,
            DocumentTypeId = typeId,
            OwnerId = Alice,
            Title = title,
            Language = "ja",
            Markdown = markdown
        });

        await RunAsync(documentId, IndexDocumentJobKind.Sync);
    }

    private Task RunAsync(Guid documentId, IndexDocumentJobKind kind)
    {
        _eventTime = _eventTime.AddMinutes(1);
        return GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = documentId,
            Kind = kind,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = _eventTime
        });
    }
}

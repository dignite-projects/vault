using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Compilation;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Search;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace Dignite.Vault.Index.Status;

/// <summary>
/// The status page's view of compiled summaries, with a chat model registered: each document's state by the same
/// source hash the compile job writes, the counts, and a recompile that queues a forced compile job.
/// </summary>
public class SummaryStatus_Tests : VaultIndexTestBase<VaultIndexSummaryTestModule>
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
    private static readonly Guid Invoice = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid Alice = Guid.Parse("0e000000-0000-0000-0000-0000000000a1");

    private readonly IDocumentIntegrationService _extract;
    private readonly IDocumentAppService _documents;
    private readonly IBackgroundJobManager _jobs;
    private readonly IIndexStatusAppService _status;
    private DateTime _eventTime = new(2026, 11, 13, 1, 0, 0);

    public SummaryStatus_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _documents = GetRequiredService<IDocumentAppService>();
        _jobs = GetRequiredService<IBackgroundJobManager>();
        _status = GetRequiredService<IIndexStatusAppService>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        _documents.GetReadScopeAsync().Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
    }

    [Fact]
    public async Task Each_document_shows_where_its_summaries_stand()
    {
        await LeaseSummariesOnAsync();
        await IndexAsync(LeaseA, LeaseType);
        await IndexAsync(LeaseB, LeaseType);
        await IndexAsync(Invoice, InvoiceType);
        await CompileAsync(LeaseA);

        var rows = await RowsAsync();
        rows[LeaseA].SummariesState.ShouldBe(IndexArtifactState.Current);
        (rows[LeaseA].SummariesInputTokenCount, rows[LeaseA].SummariesOutputTokenCount, rows[LeaseA].SummariesNote).ShouldBe((300L, 150L, (string?)null));
        rows[LeaseA].SummariesCompiledTime.ShouldNotBeNull();
        rows[LeaseB].SummariesState.ShouldBe(IndexArtifactState.Pending);
        rows[Invoice].SummariesState.ShouldBe(IndexArtifactState.None);

        var summary = await _status.GetSummaryAsync();
        (summary.ChatModelAvailable, summary.SummaryDocumentCount, summary.SummaryNoteCount).ShouldBe((true, 1, 0));
    }

    [Fact]
    public async Task Summaries_of_other_chunks_or_an_older_template_are_out_of_date()
    {
        await LeaseSummariesOnAsync();
        await IndexAsync(LeaseA, LeaseType);
        await CompileAsync(LeaseA);

        var chunk = (await WithUnitOfWorkAsync(() => GetRequiredService<IChunkRepository>().GetListAsync(c => c.DocumentId == LeaseA)))
            .Single(c => c.HeadingPath!.Contains("第2条"));
        await GetRequiredService<IIndexChunkAppService>().DisableAsync(chunk.Id);
        (await RowsAsync())[LeaseA].SummariesState.ShouldBe(IndexArtifactState.Outdated);

        await CompileAsync(LeaseA);
        (await RowsAsync())[LeaseA].SummariesState.ShouldBe(IndexArtifactState.Current);

        // Same source, but written by an older template version.
        await WithUnitOfWorkAsync(async () =>
        {
            var artifacts = GetRequiredService<IRepository<Artifact, Guid>>();
            var artifact = await artifacts.SingleAsync(a => a.DocumentId == LeaseA);
            var old = new DocumentSummaries { Version = DocumentSummaries.CurrentVersion - 1, Document = "old" };
            artifact.Recompile(old.ToJson(), artifact.SourceHash, null, null, "a note");
            await artifacts.UpdateAsync(artifact);
        });
        (await RowsAsync())[LeaseA].SummariesState.ShouldBe(IndexArtifactState.Outdated);
        (await _status.GetSummaryAsync()).SummaryNoteCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_recompile_queues_a_forced_compile_job()
    {
        await LeaseSummariesOnAsync();
        await IndexAsync(LeaseA, LeaseType);
        _jobs.ClearReceivedCalls();

        await _status.RecompileAsync(LeaseA);

        await _jobs.Received(1).EnqueueAsync(
            Arg.Is<IndexCompileJobArgs>(a => a.DocumentId == LeaseA && a.Force),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task A_recompile_is_refused_where_it_would_only_remove_or_reach_nothing()
    {
        await LeaseSummariesOnAsync();
        await IndexAsync(LeaseA, LeaseType);
        await IndexAsync(Invoice, InvoiceType);
        _jobs.ClearReceivedCalls();

        (await Should.ThrowAsync<BusinessException>(() => _status.RecompileAsync(Invoice))).Code.ShouldBe(VaultIndexErrorCodes.SummariesOff);
        await Should.ThrowAsync<EntityNotFoundException>(() => _status.RecompileAsync(Guid.NewGuid()));

        _documents.GetReadScopeAsync().Returns(new DocumentReadScopeDto { Version = 1, DocumentTypeIds = [InvoiceType] });
        await Should.ThrowAsync<EntityNotFoundException>(() => _status.RecompileAsync(LeaseA));

        _documents.GetReadScopeAsync().Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
        GetRequiredService<SearchPermissionSwitch>().Denied.Add(VaultIndexPermissions.StatusRebuild);
        await Should.ThrowAsync<AbpAuthorizationException>(() => _status.RecompileAsync(LeaseA));

        _jobs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_estimate_counts_about_the_calls_compiling_takes()
    {
        await IndexAsync(LeaseA, LeaseType);
        await IndexAsync(LeaseB, LeaseType);

        // Two documents of 2 chunks each: one call for the sections, two for the upper level and the document.
        var estimate = await GetRequiredService<IIndexProfileAppService>().GetRebuildEstimateAsync(
            new IndexProfileInput { DocumentTypeId = LeaseType, Compilation = IndexCompilation.Summaries });
        (estimate.SummaryDocumentCount, estimate.SummaryCallCount).ShouldBe((2, 6));
    }

    private Task LeaseSummariesOnAsync()
        => GetRequiredService<IIndexProfileAppService>().SetAsync(new IndexProfileInput { DocumentTypeId = LeaseType, Compilation = IndexCompilation.Summaries });

    private async Task<Dictionary<Guid, IndexedDocumentStatusDto>> RowsAsync()
        => (await _status.GetDocumentListAsync(new GetIndexedDocumentListInput { MaxResultCount = 50 })).Items.ToDictionary(r => r.DocumentId);

    private async Task IndexAsync(Guid documentId, Guid typeId)
    {
        _extract.FindReadyAsync(documentId).Returns(new DocumentIntegrationDto
        {
            Id = documentId,
            DocumentTypeId = typeId,
            OwnerId = Alice,
            Title = "T",
            Language = "ja",
            Markdown = Lease
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

    private Task CompileAsync(Guid documentId)
        => GetRequiredService<IndexCompileJob>().ExecuteAsync(new IndexCompileJobArgs { DocumentId = documentId });
}

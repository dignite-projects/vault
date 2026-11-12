using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Runs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Application.Dtos;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>First install: the seed contributor starts a backfill once, and the backfill walks Extract's cursor.</summary>
public class IndexBackfill_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    private readonly IDocumentIntegrationService _extract;
    private readonly IBackgroundJobManager _jobs;

    public IndexBackfill_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _jobs = GetRequiredService<IBackgroundJobManager>();
    }

    [Fact]
    public async Task A_layer_never_indexed_gets_a_backfill_on_seed()
    {
        await SeedAsync();

        await _jobs.Received(1).EnqueueAsync(Arg.Is<IndexBackfillJobArgs>(a => a.AfterId == null), BackgroundJobPriority.Low, Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task A_layer_with_an_indexed_document_gets_no_backfill()
    {
        await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexedDocument, Guid>>().InsertAsync(
            new IndexedDocument(Guid.NewGuid(), null, Guid.NewGuid(), null)));

        await SeedAsync();

        await _jobs.DidNotReceive().EnqueueAsync(Arg.Any<IndexBackfillJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task A_full_page_enqueues_a_sync_per_document_and_the_next_page()
    {
        Configure(pageSize: 2);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var last = ids[1];
        _extract.GetReadyIdsAsync(Arg.Is<GetReadyDocumentIdsInput>(i => i.AfterId == null && i.MaxResultCount == 2))
            .Returns(new ListResultDto<Guid>(ids));

        await GetRequiredService<IndexBackfillJob>().ExecuteAsync(new IndexBackfillJobArgs());

        foreach (var id in ids)
        {
            await _jobs.Received(1).EnqueueAsync(
                Arg.Is<IndexDocumentJobArgs>(a => a.DocumentId == id && a.Kind == IndexDocumentJobKind.Sync && a.Trigger == IndexRunTrigger.Backfill),
                BackgroundJobPriority.Low,
                Arg.Any<TimeSpan?>());
        }

        await _jobs.Received(1).EnqueueAsync(Arg.Is<IndexBackfillJobArgs>(a => a.AfterId == last), BackgroundJobPriority.Low, Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task A_short_page_ends_the_backfill()
    {
        Configure(pageSize: 2);
        _extract.GetReadyIdsAsync(Arg.Any<GetReadyDocumentIdsInput>()).Returns(new ListResultDto<Guid>([Guid.NewGuid()]));

        await GetRequiredService<IndexBackfillJob>().ExecuteAsync(new IndexBackfillJobArgs());

        await _jobs.Received(1).EnqueueAsync(Arg.Any<IndexDocumentJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _jobs.DidNotReceive().EnqueueAsync(Arg.Any<IndexBackfillJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    private void Configure(int pageSize)
        => GetRequiredService<Microsoft.Extensions.Options.IOptions<VaultIndexIngestionOptions>>().Value.BackfillPageSize = pageSize;

    private Task SeedAsync()
        => GetRequiredService<IndexBackfillDataSeedContributor>().SeedAsync(new DataSeedContext());
}

/// <summary>A host that registered no embedding model: ingestion is off and leaves no trace.</summary>
public class Ingestion_without_a_model_Tests : VaultIndexTestBase<VaultIndexIngestionWithoutModelTestModule>
{
    [Fact]
    public async Task A_Ready_event_indexes_nothing_and_marks_nothing_failed()
    {
        var documentId = Guid.NewGuid();
        GetRequiredService<IDocumentIntegrationService>().FindReadyAsync(documentId).Returns(new DocumentIntegrationDto
        {
            Id = documentId,
            DocumentTypeId = Guid.NewGuid(),
            Markdown = "# 見積書\n\n合計 10,000 円"
        });

        await GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = documentId,
            Kind = IndexDocumentJobKind.Sync,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = DateTime.UtcNow
        });

        (await GetRequiredService<IRepository<IndexedDocument, Guid>>().GetCountAsync()).ShouldBe(0);
        (await GetRequiredService<IRepository<IndexRun, Guid>>().GetCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Seeding_starts_no_backfill()
    {
        await GetRequiredService<IndexBackfillDataSeedContributor>().SeedAsync(new DataSeedContext());

        await GetRequiredService<IBackgroundJobManager>().DidNotReceive()
            .EnqueueAsync(Arg.Any<IndexBackfillJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }
}

[DependsOn(typeof(VaultIndexIngestionTestModule))]
public class VaultIndexIngestionWithoutModelTestModule : AbpModule
{
    public override void PostConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.RemoveAll(d => d.IsKeyedService
                                        && Equals(d.ServiceKey, VaultIndexConsts.EmbeddingGeneratorKey)
                                        && d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
    }
}

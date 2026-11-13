using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Runs;
using Dignite.Vault.Index.Search;
using Dignite.Vault.Index.Status;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Changing the embedding model: a new generation is built in the background from the chunks Index holds while the
/// serving one keeps answering searches, then takes over, and the old vectors go. The queue is the test's own: what
/// the jobs enqueue is collected and run when a test says so, the way ABP's worker would later.
/// </summary>
public class EmbeddingModelSwitch_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    private const string OldModel = "fake-embedding";
    private const string NewModel = "new-embedding";
    private const string NoModel = "no-previous-model";

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
    private static readonly Guid Owner = Guid.Parse("0e000000-0000-0000-0000-0000000000a1");

    private readonly IDocumentIntegrationService _extract;
    private readonly IBackgroundJobManager _jobs;
    private readonly InMemoryChunkSearchIndex _vectors;
    private readonly FakeEmbeddingGenerator _model;
    private readonly FakeEmbeddingGenerator _previous;
    private readonly IIndexSearchAppService _search;
    private readonly List<object> _queue = [];
    private DateTime _eventTime = new(2026, 11, 13, 1, 0, 0);

    public EmbeddingModelSwitch_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _jobs = GetRequiredService<IBackgroundJobManager>();
        _vectors = GetRequiredService<InMemoryChunkSearchIndex>();
        _model = GetRequiredService<FakeEmbeddingGenerator>();
        _previous = GetRequiredService<PreviousEmbeddingModel>().Generator;
        _search = GetRequiredService<IIndexSearchAppService>();

        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = InvoiceType, TypeCode = "invoice", DisplayName = "Invoice" }
        });
        GetRequiredService<IDocumentAppService>().GetReadScopeAsync().Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
    }

    [Fact]
    public async Task A_new_model_builds_a_generation_from_the_chunks_held_and_takes_over_when_every_document_is_in_it()
    {
        await IndexBothAsync();
        var old = await ServingAsync();
        var chunkIds = await ChunkIdsAsync();
        _extract.ClearReceivedCalls();

        UseNewModel(keepPrevious: true);
        await SeedAsync();
        await DrainAsync();

        var generations = await Repo<IndexGeneration>().GetListAsync();
        generations.Count.ShouldBe(2);
        var serving = generations.Single(g => g.Status == IndexGenerationStatus.Serving);
        serving.ModelId.ShouldBe(NewModel);
        generations.Single(g => g.Id == old.Id).Status.ShouldBe(IndexGenerationStatus.Retired);

        // The chunks are the ones held, not pulled or cut again; only their vectors are new, and the old ones are gone.
        (await ChunkIdsAsync()).ShouldBe(chunkIds, ignoreOrder: true);
        await _extract.DidNotReceive().FindReadyAsync(Arg.Any<Guid>());
        _vectors.Vectors.Keys.ShouldAllBe(k => k.GenerationId == serving.Id);
        _vectors.Vectors.Keys.Select(k => k.ChunkId).ShouldBe(chunkIds, ignoreOrder: true);
        (await Repo<IndexedDocument>().GetListAsync()).ShouldAllBe(d => d.GenerationId == serving.Id && d.Status == IndexedDocumentStatus.Indexed);

        var runs = (await Repo<IndexRun>().GetListAsync()).Where(r => r.Trigger == IndexRunTrigger.ModelChange).ToList();
        runs.Select(r => r.DocumentId).ShouldBe([LeaseDoc, InvoiceDoc], ignoreOrder: true);
        runs.ShouldAllBe(r => r.Status == IndexRunStatus.Succeeded && r.EmbeddingTokenCount > 0);

        _model.Inputs.Clear();
        (await FoundAsync("支払期限")).ShouldContain(InvoiceDoc);
        _model.Inputs.ShouldBe(["支払期限"]);
    }

    [Fact]
    public async Task While_the_new_generation_is_built_search_reads_the_serving_one_with_the_previous_model()
    {
        await IndexBothAsync();
        var old = await ServingAsync();
        UseNewModel(keepPrevious: true);
        await SeedAsync();
        await RunQueuedAsync(a => a is IndexBackfillJobArgs);
        await RunEmbedAsync(LeaseDoc);

        var building = await BuildingAsync();
        building.ModelId.ShouldBe(NewModel);
        (await ServingAsync()).Id.ShouldBe(old.Id);

        _model.Inputs.Clear();
        _previous.Inputs.Clear();
        (await FoundAsync("支払期限")).ShouldContain(InvoiceDoc);
        _previous.Inputs.ShouldBe(["支払期限"]);
        _model.Inputs.ShouldBeEmpty();

        var summary = await GetRequiredService<IIndexStatusAppService>().GetSummaryAsync();
        summary.ServingModelId.ShouldBe(OldModel);
        summary.ServingModelAvailable.ShouldBeTrue();
        summary.ConfiguredModelId.ShouldBe(NewModel);
        summary.BuildingModelId.ShouldBe(NewModel);
        summary.BuildingDocumentCount.ShouldBe(1);
        summary.BuildingTotalCount.ShouldBe(2);
        summary.BuildingFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_document_changed_during_the_build_stays_findable_in_the_serving_generation_and_joins_the_new_one()
    {
        await IndexBothAsync();
        var old = await ServingAsync();
        UseNewModel(keepPrevious: true);
        await SeedAsync();
        await RunQueuedAsync(a => a is IndexBackfillJobArgs);

        await IndexAsync(LeaseDoc, LeaseType, Lease + "\n\n## 第3条（解約）\n\n解約は三か月前までに予告する。", "賃貸借契約書");

        // Both generations hold the new chunks: the serving one from the previous model, the new one from the configured.
        var building = await BuildingAsync();
        var chunks = await ChunkIdsAsync(LeaseDoc);
        VectorChunkIds(old.Id, LeaseDoc).ShouldBe(chunks, ignoreOrder: true);
        VectorChunkIds(building.Id, LeaseDoc).ShouldBe(chunks, ignoreOrder: true);
        (await Repo<IndexedDocument>().GetAsync(LeaseDoc)).GenerationId.ShouldBe(building.Id);

        await DrainAsync();

        (await ServingAsync()).Id.ShouldBe(building.Id);
        VectorChunkIds(old.Id, LeaseDoc).ShouldBeEmpty();
        VectorChunkIds(building.Id, LeaseDoc).ShouldBe(chunks, ignoreOrder: true);
    }

    [Fact]
    public async Task An_unchanged_document_synced_during_the_build_is_embedded_as_it_is()
    {
        await IndexBothAsync();
        var chunks = await ChunkIdsAsync(InvoiceDoc);
        UseNewModel(keepPrevious: true);

        await IndexAsync(InvoiceDoc, InvoiceType, Invoice, "請求書");

        var building = await BuildingAsync();
        (await ChunkIdsAsync(InvoiceDoc)).ShouldBe(chunks, ignoreOrder: true);
        VectorChunkIds(building.Id, InvoiceDoc).ShouldBe(chunks, ignoreOrder: true);
        (await Repo<IndexedDocument>().GetAsync(InvoiceDoc)).GenerationId.ShouldBe(building.Id);
        (await Repo<IndexRun>().GetListAsync()).Where(r => r.DocumentId == InvoiceDoc).OrderBy(r => r.StartedTime).Last()
            .Status.ShouldBe(IndexRunStatus.Succeeded);

        // Creating the generation started the walk that brings in the rest.
        Collect();
        _queue.OfType<IndexBackfillJobArgs>().ShouldContain(a => a.EmbedOnly);
    }

    [Fact]
    public async Task Without_the_previous_model_search_is_refused_until_the_new_generation_takes_over()
    {
        await IndexBothAsync();
        UseNewModel(keepPrevious: false);

        var error = await Should.ThrowAsync<BusinessException>(() => FoundAsync("賃料"));
        error.Code.ShouldBe(VaultIndexErrorCodes.EmbeddingModelChanged);
        (await GetRequiredService<IIndexStatusAppService>().GetSummaryAsync()).ServingModelAvailable.ShouldBeFalse();

        // A document changed meanwhile goes into the new generation only; its old chunks' vectors go everywhere.
        var old = await ServingAsync();
        await IndexAsync(LeaseDoc, LeaseType, Lease + "\n\n追記。", "賃貸借契約書");
        VectorChunkIds(old.Id, LeaseDoc).ShouldBeEmpty();
        VectorChunkIds((await BuildingAsync()).Id, LeaseDoc).ShouldBe(await ChunkIdsAsync(LeaseDoc), ignoreOrder: true);

        await SeedAsync();
        await DrainAsync();

        (await FoundAsync("賃料")).ShouldContain(LeaseDoc);
    }

    [Fact]
    public async Task A_failure_partway_leaves_the_serving_generation_answering_and_a_retry_finishes_the_switch()
    {
        await IndexBothAsync();
        var old = await ServingAsync();
        UseNewModel(keepPrevious: true);
        await SeedAsync();
        await RunQueuedAsync(a => a is IndexBackfillJobArgs);
        await RunEmbedAsync(LeaseDoc);

        _model.FailWith = new TimeoutException("model timed out");
        await Should.ThrowAsync<TimeoutException>(() => RunEmbedAsync(InvoiceDoc));

        var building = await BuildingAsync();
        (await ServingAsync()).Id.ShouldBe(old.Id);
        var invoice = await Repo<IndexedDocument>().GetAsync(InvoiceDoc);
        invoice.GenerationId.ShouldBe(old.Id);
        invoice.Status.ShouldBe(IndexedDocumentStatus.Indexed);
        var failed = (await Repo<IndexRun>().GetListAsync()).Single(r => r.DocumentId == InvoiceDoc && r.Trigger == IndexRunTrigger.ModelChange);
        failed.Status.ShouldBe(IndexRunStatus.Failed);
        failed.ErrorMessage.ShouldBe("model timed out");
        (await GetRequiredService<IIndexStatusAppService>().GetSummaryAsync()).BuildingFailedCount.ShouldBe(1);
        (await FoundAsync("支払期限")).ShouldContain(InvoiceDoc);

        // ABP retries the job with the same arguments; this time the model answers.
        _model.FailWith = null;
        await RunEmbedAsync(InvoiceDoc);

        (await ServingAsync()).Id.ShouldBe(building.Id);
        (await Repo<IndexRun>().GetListAsync())
            .Where(r => r.DocumentId == InvoiceDoc && r.Trigger == IndexRunTrigger.ModelChange)
            .Select(r => r.AttemptNumber).OrderBy(n => n).ShouldBe([1, 2]);
        _vectors.Vectors.Keys.ShouldAllBe(k => k.GenerationId == building.Id);
    }

    [Fact]
    public async Task A_new_model_of_another_dimension_never_replaces_the_serving_generation()
    {
        await IndexBothAsync();
        var old = await ServingAsync();
        UseNewModel(keepPrevious: true);
        _model.Dimensions = 16;

        await SeedAsync();
        await DrainAsync();

        (await Repo<IndexGeneration>().GetListAsync()).ShouldHaveSingleItem().Id.ShouldBe(old.Id);
        var runs = (await Repo<IndexRun>().GetListAsync()).Where(r => r.Trigger == IndexRunTrigger.ModelChange).ToList();
        runs.Count.ShouldBe(2);
        runs.ShouldAllBe(r => r.Status == IndexRunStatus.Failed && r.ErrorMessage!.StartsWith(VaultIndexErrorCodes.EmbeddingDimensionMismatch));
        (await FoundAsync("賃料")).ShouldContain(LeaseDoc);

        // No generation was started, and the status page still says the rebuild is failing.
        var summary = await GetRequiredService<IIndexStatusAppService>().GetSummaryAsync();
        summary.BuildingModelId.ShouldBeNull();
        summary.BuildingFailedCount.ShouldBe(2);
    }

    [Fact]
    public async Task Configuring_the_old_model_back_abandons_the_new_generation()
    {
        await IndexBothAsync();
        var old = await ServingAsync();
        UseNewModel(keepPrevious: true);
        await SeedAsync();
        await RunQueuedAsync(a => a is IndexBackfillJobArgs);
        await RunEmbedAsync(LeaseDoc);
        var building = await BuildingAsync();

        _model.ModelId = OldModel;
        _previous.ModelId = NoModel;
        await SeedAsync();

        // Abandoned at once, so the status page no longer shows a rebuild for a model the host does not configure.
        (await Repo<IndexGeneration>().GetAsync(building.Id)).Status.ShouldBe(IndexGenerationStatus.Retired);
        _vectors.Vectors.Keys.ShouldNotContain(k => k.GenerationId == building.Id);
        (await GetRequiredService<IIndexStatusAppService>().GetSummaryAsync()).BuildingModelId.ShouldBeNull();

        await DrainAsync();

        (await Repo<IndexGeneration>().GetAsync(building.Id)).Status.ShouldBe(IndexGenerationStatus.Retired);
        (await ServingAsync()).Id.ShouldBe(old.Id);
        _vectors.Vectors.Keys.ShouldAllBe(k => k.GenerationId == old.Id);
        (await Repo<IndexedDocument>().GetListAsync()).ShouldAllBe(d => d.GenerationId == old.Id);
        (await FoundAsync("賃料")).ShouldContain(LeaseDoc);
    }

    [Fact]
    public async Task Chunks_replaced_while_they_were_embedded_are_not_written_into_the_new_generation()
    {
        await IndexBothAsync();
        UseNewModel(keepPrevious: true);
        await SeedAsync();
        await RunQueuedAsync(a => a is IndexBackfillJobArgs);

        // While the embed job waits on the model, a host instance still on the old model re-chunks the document.
        _model.DuringGenerate = async () =>
        {
            _model.ModelId = OldModel;
            await IndexAsync(LeaseDoc, LeaseType, Lease + "\n\n追記。", "賃貸借契約書");
            _model.ModelId = NewModel;
        };
        await RunEmbedAsync(LeaseDoc);

        var building = await BuildingAsync();
        VectorChunkIds(building.Id, LeaseDoc).ShouldBeEmpty();
        (await Repo<IndexedDocument>().GetAsync(LeaseDoc)).GenerationId.ShouldNotBe(building.Id);
        (await Repo<IndexRun>().GetListAsync()).Single(r => r.DocumentId == LeaseDoc && r.Trigger == IndexRunTrigger.ModelChange)
            .Status.ShouldBe(IndexRunStatus.Skipped);

        // The walk that comes after brings the new chunks in.
        await DrainAsync();
        VectorChunkIds(building.Id, LeaseDoc).ShouldBe(await ChunkIdsAsync(LeaseDoc), ignoreOrder: true);
    }

    [Fact]
    public async Task A_sync_whose_held_chunks_were_replaced_while_it_embedded_them_writes_nothing_and_is_retried()
    {
        await IndexBothAsync();
        UseNewModel(keepPrevious: true);

        // A claim that expires at once: another job takes the document over while this one waits on the model.
        GetRequiredService<Microsoft.Extensions.Options.IOptions<VaultIndexIngestionOptions>>().Value.ClaimTimeout = TimeSpan.Zero;
        _model.DuringGenerate = () => IndexAsync(InvoiceDoc, InvoiceType, Invoice.Replace("211,200", "211,300"), "請求書");

        await Should.ThrowAsync<InvalidOperationException>(() => IndexAsync(InvoiceDoc, InvoiceType, Invoice, "請求書"));

        // What is stored is the takeover's: the new chunk, in both generations, from the same text.
        var building = await BuildingAsync();
        var old = await ServingAsync();
        var chunk = (await ChunkIdsAsync(InvoiceDoc)).ShouldHaveSingleItem();
        VectorChunkIds(building.Id, InvoiceDoc).ShouldBe([chunk]);
        _vectors.Vectors[(building.Id, chunk)].Vector.ToArray().ShouldBe(_vectors.Vectors[(old.Id, chunk)].Vector.ToArray());
        (await Repo<IndexedDocument>().GetAsync(InvoiceDoc)).FailedAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Vectors_are_not_written_into_a_generation_abandoned_while_the_job_computed()
    {
        await IndexBothAsync();
        var old = await ServingAsync();
        UseNewModel(keepPrevious: true);
        await SeedAsync();
        await RunQueuedAsync(a => a is IndexBackfillJobArgs);
        await RunEmbedAsync(InvoiceDoc);
        var building = await BuildingAsync();

        // The sync of a changed document computes for the building generation; meanwhile the build is abandoned.
        _previous.DuringGenerate = () => WithUnitOfWorkAsync(() => GetRequiredService<IndexGenerationManager>().AbandonStaleBuildingAsync(OldModel));
        await IndexAsync(LeaseDoc, LeaseType, Lease + "\n\n追記。", "賃貸借契約書");

        (await Repo<IndexGeneration>().GetAsync(building.Id)).Status.ShouldBe(IndexGenerationStatus.Retired);
        VectorChunkIds(building.Id, LeaseDoc).ShouldBeEmpty();
        VectorChunkIds(old.Id, LeaseDoc).ShouldBe(await ChunkIdsAsync(LeaseDoc), ignoreOrder: true);

        // The vectors are not where the configured model's belong, so the job asks to run once more.
        Collect();
        _queue.OfType<IndexDocumentJobArgs>().ShouldContain(a => a.DocumentId == LeaseDoc && a.Kind == IndexDocumentJobKind.Sync);
    }

    private void UseNewModel(bool keepPrevious)
    {
        _model.ModelId = NewModel;
        _previous.ModelId = keepPrevious ? OldModel : NoModel;
    }

    private async Task IndexBothAsync()
    {
        await IndexAsync(LeaseDoc, LeaseType, Lease, "賃貸借契約書");
        await IndexAsync(InvoiceDoc, InvoiceType, Invoice, "請求書");
        Collect();
        _queue.Clear();
    }

    private async Task IndexAsync(Guid documentId, Guid typeId, string markdown, string title)
    {
        _extract.FindReadyAsync(documentId).Returns(new DocumentIntegrationDto
        {
            Id = documentId,
            DocumentTypeId = typeId,
            OwnerId = Owner,
            Title = title,
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

    private Task SeedAsync() => GetRequiredService<IndexBackfillDataSeedContributor>().SeedAsync(new DataSeedContext());

    /// <summary>Moves what the jobs enqueued since the last look into the test's queue.</summary>
    private void Collect()
    {
        foreach (var call in _jobs.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobManager.EnqueueAsync)))
        {
            if (call.GetArguments()[0] is IndexBackfillJobArgs or IndexDocumentJobArgs)
            {
                _queue.Add(call.GetArguments()[0]!);
            }
        }

        _jobs.ClearReceivedCalls();
    }

    private async Task RunQueuedAsync(Func<object, bool> which)
    {
        Collect();
        foreach (var args in _queue.Where(which).ToList())
        {
            _queue.Remove(args);
            await RunAsync(args);
            Collect();
        }
    }

    /// <summary>The queued embed job of one document. A failing job stays queued, as ABP keeps it for a retry.</summary>
    private async Task RunEmbedAsync(Guid documentId)
    {
        Collect();
        var args = _queue.OfType<IndexDocumentJobArgs>().First(a => a.DocumentId == documentId && a.Kind == IndexDocumentJobKind.Embed);
        try
        {
            await RunAsync(args);
        }
        finally
        {
            Collect();
        }

        _queue.Remove(args);
    }

    private async Task DrainAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            Collect();
            if (_queue.Count == 0)
            {
                return;
            }

            var args = _queue[0];
            _queue.RemoveAt(0);
            await RunAsync(args);
        }

        throw new InvalidOperationException("The queue did not drain.");
    }

    private Task RunAsync(object args) => args switch
    {
        IndexBackfillJobArgs walk => GetRequiredService<IndexBackfillJob>().ExecuteAsync(walk),
        IndexDocumentJobArgs job => GetRequiredService<IndexDocumentJob>().ExecuteAsync(job),
        _ => Task.CompletedTask
    };

    private async Task<List<Guid>> FoundAsync(string query)
        => (await _search.SearchAsync(new IndexSearchInput { Query = query, MaxResultCount = IndexSearchConsts.MaxResultCount }))
            .Hits.Select(h => h.DocumentId).Distinct().ToList();

    private async Task<IndexGeneration> ServingAsync()
        => (await Repo<IndexGeneration>().GetListAsync()).Single(g => g.Status == IndexGenerationStatus.Serving);

    private async Task<IndexGeneration> BuildingAsync()
        => (await Repo<IndexGeneration>().GetListAsync()).Single(g => g.Status == IndexGenerationStatus.Building);

    private async Task<List<Guid>> ChunkIdsAsync(Guid? documentId = null)
        => (await GetRequiredService<IChunkRepository>().GetListAsync(c => documentId == null || c.DocumentId == documentId))
            .Select(c => c.Id).ToList();

    private List<Guid> VectorChunkIds(Guid generationId, Guid documentId)
        => _vectors.Vectors.Where(p => p.Key.GenerationId == generationId && p.Value.DocumentId == documentId).Select(p => p.Key.ChunkId).ToList();

    private IRepository<TEntity, Guid> Repo<TEntity>() where TEntity : class, Volo.Abp.Domain.Entities.IEntity<Guid>
        => GetRequiredService<IRepository<TEntity, Guid>>();
}

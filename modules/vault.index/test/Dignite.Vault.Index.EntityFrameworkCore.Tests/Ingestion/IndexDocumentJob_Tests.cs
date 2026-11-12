using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Runs;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Timing;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// The index job against SQLite with doubles for Extract, the queue, the vector store and the model. Covers the
/// acceptance of implementation step 4: a Ready document ends up in the chunk and vector stores, and a redelivered
/// or concurrent event does not start a second job on the same document.
/// </summary>
public class IndexDocumentJob_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    private const string Contract = """
        # 賃貸借契約書

        本契約は、甲と乙の間で締結される。

        ## 第1条（目的）

        甲は乙に対し、下記の物件を住居として賃貸する。

        ## 第2条（賃料）

        乙は毎月末日までに賃料を支払うものとする。
        """;

    private static readonly Guid DocumentId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid TypeA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid TypeB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Owner = Guid.Parse("0e000000-0000-0000-0000-0000000000e1");
    private static readonly DateTime T1 = new(2026, 11, 12, 1, 0, 0);
    private static readonly DateTime T2 = T1.AddMinutes(1);
    private static readonly DateTime T3 = T1.AddMinutes(2);

    private readonly IDocumentIntegrationService _extract;
    private readonly IBackgroundJobManager _jobs;
    private readonly InMemoryChunkSearchIndex _vectors;
    private readonly FakeEmbeddingGenerator _model;

    public IndexDocumentJob_Tests()
    {
        _extract = GetRequiredService<IDocumentIntegrationService>();
        _jobs = GetRequiredService<IBackgroundJobManager>();
        _vectors = GetRequiredService<InMemoryChunkSearchIndex>();
        _model = GetRequiredService<FakeEmbeddingGenerator>();
    }

    [Fact]
    public async Task A_Ready_document_ends_up_in_the_chunk_and_vector_stores()
    {
        ExtractReturns(Contract, TypeA);

        await RunAsync(IndexDocumentJobKind.Sync, T1);

        var chunks = await ChunksAsync();
        chunks.Count.ShouldBeGreaterThanOrEqualTo(3);
        chunks.Select(c => c.Ordinal).ShouldBe(Enumerable.Range(0, chunks.Count));
        chunks.ShouldAllBe(c => c.DocumentTypeId == TypeA && c.OwnerId == Owner);
        chunks.ShouldContain(c => c.HeadingPath != null && c.HeadingPath.Contains("第2条") && c.Text.Contains("賃料"));

        var generation = (await Repo<IndexGeneration>().GetListAsync()).Single();
        generation.Status.ShouldBe(IndexGenerationStatus.Serving);
        generation.ModelId.ShouldBe("fake-embedding");
        generation.Dimensions.ShouldBe(8);

        _vectors.Vectors.Keys.Select(k => k.ChunkId).ShouldBe(chunks.Select(c => c.Id), ignoreOrder: true);
        _vectors.Vectors.Keys.ShouldAllBe(k => k.GenerationId == generation.Id);
        _model.Inputs.ShouldAllBe(i => i.StartsWith("賃貸借契約書\n"));

        var document = await Repo<IndexedDocument>().GetAsync(DocumentId);
        document.Status.ShouldBe(IndexedDocumentStatus.Indexed);
        document.ChunkCount.ShouldBe(chunks.Count);
        document.GenerationId.ShouldBe(generation.Id);
        document.LastEventTime.ShouldBe(T1);
        document.IndexingStartedTime.ShouldBeNull();

        var run = (await Repo<IndexRun>().GetListAsync()).Single();
        run.Status.ShouldBe(IndexRunStatus.Succeeded);
        run.Trigger.ShouldBe(IndexRunTrigger.DocumentReady);
        run.EmbeddingTokenCount.ShouldBe(chunks.Count * 10);
    }

    [Fact]
    public async Task A_redelivered_event_is_dropped()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);
        var calls = _model.Calls;

        await RunAsync(IndexDocumentJobKind.Sync, T1);

        _model.Calls.ShouldBe(calls);
        (await Repo<IndexRun>().GetCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_event_during_a_live_claim_asks_for_a_rerun_instead_of_running_a_second_job()
    {
        await SeedClaimAsync(startedMinutesAgo: 1);
        ExtractReturns(Contract, TypeA);

        await RunAsync(IndexDocumentJobKind.Sync, T2);

        _model.Calls.ShouldBe(0);
        (await Repo<IndexRun>().GetCountAsync()).ShouldBe(0);
        var document = await Repo<IndexedDocument>().GetAsync(DocumentId);
        document.Status.ShouldBe(IndexedDocumentStatus.Indexing);
        document.RerunRequested.ShouldBeTrue();
        document.LastEventTime.ShouldBe(T2);
    }

    [Fact]
    public async Task A_claim_older_than_the_timeout_is_taken_over()
    {
        await SeedClaimAsync(startedMinutesAgo: 31);
        ExtractReturns(Contract, TypeA);

        await RunAsync(IndexDocumentJobKind.Sync, T2);

        (await Repo<IndexedDocument>().GetAsync(DocumentId)).Status.ShouldBe(IndexedDocumentStatus.Indexed);
        (await ChunksAsync()).ShouldNotBeEmpty();
    }

    /// <summary>
    /// The acceptance case: a second Ready arrives while the first job is busy with the model. The second job finds
    /// the live claim, records its event and asks for a rerun; it never starts a run of its own. The first job then
    /// enqueues exactly one more sync when it finishes.
    /// </summary>
    [Fact]
    public async Task An_event_arriving_mid_job_runs_once_afterwards_not_alongside()
    {
        ExtractReturns(Contract, TypeA);
        long runsSeenByTheSecondEvent = -1;
        _model.DuringGenerate = async () =>
        {
            await RunAsync(IndexDocumentJobKind.Sync, T2);
            runsSeenByTheSecondEvent = await Repo<IndexRun>().GetCountAsync();
        };

        await RunAsync(IndexDocumentJobKind.Sync, T1);

        runsSeenByTheSecondEvent.ShouldBe(1);
        _model.Calls.ShouldBe(1);
        (await Repo<IndexRun>().GetCountAsync()).ShouldBe(1);
        await _jobs.Received(1).EnqueueAsync(
            Arg.Is<IndexDocumentJobArgs>(a => a.DocumentId == DocumentId && a.Kind == IndexDocumentJobKind.Sync && a.EventTime == null),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());

        var document = await Repo<IndexedDocument>().GetAsync(DocumentId);
        document.Status.ShouldBe(IndexedDocumentStatus.Indexed);
        document.RerunRequested.ShouldBeFalse();
        document.LastEventTime.ShouldBe(T2);
    }

    [Fact]
    public async Task Unchanged_text_and_settings_skip_chunking_and_embedding()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);
        var chunkIds = (await ChunksAsync()).Select(c => c.Id).ToList();
        var calls = _model.Calls;

        await RunAsync(IndexDocumentJobKind.Sync, T2);

        _model.Calls.ShouldBe(calls);
        (await ChunksAsync()).Select(c => c.Id).ShouldBe(chunkIds, ignoreOrder: true);
        (await Repo<IndexRun>().GetListAsync()).OrderBy(r => r.StartedTime).Last().Status.ShouldBe(IndexRunStatus.Skipped);
    }

    [Fact]
    public async Task Changed_text_replaces_the_chunks_and_vectors()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);
        var before = (await ChunksAsync()).Select(c => c.Id).ToHashSet();

        ExtractReturns(Contract + "\n\n## 第3条（解除）\n\n乙が賃料を滞納したときは解除できる。", TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T2);

        var after = await ChunksAsync();
        after.ShouldAllBe(c => !before.Contains(c.Id));
        after.ShouldContain(c => c.Text.Contains("解除"));
        _vectors.Vectors.Keys.Select(k => k.ChunkId).ShouldBe(after.Select(c => c.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task A_reclassification_moves_the_read_scope_facts_and_ends_the_withhold()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);

        await RunAsync(IndexDocumentJobKind.Withhold, T2);
        (await Repo<IndexedDocument>().GetAsync(DocumentId)).IsWithheld.ShouldBeTrue();

        var calls = _model.Calls;
        ExtractReturns(Contract, TypeB);
        await RunAsync(IndexDocumentJobKind.Sync, T3);

        _model.Calls.ShouldBe(calls);
        (await ChunksAsync()).ShouldAllBe(c => c.DocumentTypeId == TypeB);
        _vectors.Vectors.Values.ShouldAllBe(v => v.DocumentTypeId == TypeB);
        var document = await Repo<IndexedDocument>().GetAsync(DocumentId);
        document.IsWithheld.ShouldBeFalse();
        document.DocumentTypeId.ShouldBe(TypeB);
    }

    [Fact]
    public async Task A_document_no_longer_Ready_keeps_its_last_Ready_version()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);
        var chunkCount = (await ChunksAsync()).Count;

        _extract.FindReadyAsync(DocumentId).Returns((DocumentIntegrationDto?)null);
        await RunAsync(IndexDocumentJobKind.Sync, T2);

        (await ChunksAsync()).Count.ShouldBe(chunkCount);
        var document = await Repo<IndexedDocument>().GetAsync(DocumentId);
        document.Status.ShouldBe(IndexedDocumentStatus.Indexed);
        document.LastEventTime.ShouldBe(T2);
    }

    [Fact]
    public async Task A_deterministic_failure_ends_the_document_at_once_without_a_retry()
    {
        GetRequiredService<IOptions<VaultIndexIngestionOptions>>().Value.MaxMarkdownLength = 10;
        ExtractReturns(Contract, TypeA);

        await RunAsync(IndexDocumentJobKind.Sync, T1);

        (await Repo<IndexedDocument>().GetAsync(DocumentId)).Status.ShouldBe(IndexedDocumentStatus.Failed);
        var run = (await Repo<IndexRun>().GetListAsync()).Single();
        run.Status.ShouldBe(IndexRunStatus.Failed);
        run.ErrorMessage.ShouldStartWith(VaultIndexErrorCodes.DocumentTooLarge);
        _model.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_until_the_cap_then_ends_the_document()
    {
        ExtractReturns(Contract, TypeA);
        _model.FailWith = new TimeoutException("model timed out");

        await Should.ThrowAsync<TimeoutException>(() => RunAsync(IndexDocumentJobKind.Sync, T1));
        var document = await Repo<IndexedDocument>().GetAsync(DocumentId);
        document.Status.ShouldBe(IndexedDocumentStatus.Pending);
        document.FailedAttempts.ShouldBe(1);

        // ABP retries the same job arguments; the second failure reaches MaxFailedAttempts (2 in this host).
        await RunAsync(IndexDocumentJobKind.Sync, null);

        document = await Repo<IndexedDocument>().GetAsync(DocumentId);
        document.Status.ShouldBe(IndexedDocumentStatus.Failed);
        (await Repo<IndexRun>().GetListAsync()).Select(r => r.AttemptNumber).OrderBy(n => n).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Another_embedding_model_than_the_serving_generation_is_refused()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);

        _model.ModelId = "another-model";
        ExtractReturns(Contract + "\n\n追記。", TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T2);

        (await Repo<IndexedDocument>().GetAsync(DocumentId)).Status.ShouldBe(IndexedDocumentStatus.Failed);
        (await Repo<IndexRun>().GetListAsync()).OrderBy(r => r.StartedTime).Last().ErrorMessage
            .ShouldStartWith(VaultIndexErrorCodes.EmbeddingModelChanged);
    }

    [Fact]
    public async Task Vectors_of_another_dimension_than_the_storage_are_refused()
    {
        GetRequiredService<InMemoryChunkSearchIndex>().VectorDimensions = 16;
        ExtractReturns(Contract, TypeA);

        await RunAsync(IndexDocumentJobKind.Sync, T1);

        (await Repo<IndexedDocument>().GetAsync(DocumentId)).Status.ShouldBe(IndexedDocumentStatus.Failed);
        _vectors.Vectors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recycle_bin_marks_follow_the_events_and_a_stale_one_is_dropped()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);

        await RunAsync(IndexDocumentJobKind.Archive, T2);
        (await Repo<IndexedDocument>().GetAsync(DocumentId)).IsArchived.ShouldBeTrue();

        // Restored with an older stamp than the deletion: a reordered delivery, dropped.
        await RunAsync(IndexDocumentJobKind.Unarchive, T1);
        (await Repo<IndexedDocument>().GetAsync(DocumentId)).IsArchived.ShouldBeTrue();

        _jobs.ClearReceivedCalls();
        await RunAsync(IndexDocumentJobKind.Unarchive, T3);
        (await Repo<IndexedDocument>().GetAsync(DocumentId)).IsArchived.ShouldBeFalse();
        await _jobs.Received(1).EnqueueAsync(
            Arg.Is<IndexDocumentJobArgs>(a => a.Kind == IndexDocumentJobKind.Sync && a.DocumentId == DocumentId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Removing_a_document_deletes_everything_derived_from_it()
    {
        ExtractReturns(Contract, TypeA);
        await RunAsync(IndexDocumentJobKind.Sync, T1);
        await WithUnitOfWorkAsync(() => Repo<Artifact>().InsertAsync(
            new Artifact(Guid.NewGuid(), null, ArtifactLevel.Document, "outline", TypeA, DocumentId, "{}", new string('a', 64))));

        await RunAsync(IndexDocumentJobKind.Remove, T2);

        (await ChunksAsync()).ShouldBeEmpty();
        _vectors.Vectors.ShouldBeEmpty();
        (await Repo<Artifact>().GetCountAsync()).ShouldBe(0);
        (await Repo<IndexRun>().GetCountAsync()).ShouldBe(0);
        (await Repo<IndexedDocument>().FindAsync(DocumentId)).ShouldBeNull();
    }

    private void ExtractReturns(string markdown, Guid typeId)
        => _extract.FindReadyAsync(DocumentId).Returns(Dto(markdown, typeId));

    private static DocumentIntegrationDto Dto(string markdown, Guid typeId) => new()
    {
        Id = DocumentId,
        DocumentTypeId = typeId,
        OwnerId = Owner,
        Title = "賃貸借契約書",
        Language = "ja",
        Markdown = markdown
    };

    private static IndexDocumentJobArgs Args(IndexDocumentJobKind kind, DateTime? eventTime) => new()
    {
        DocumentId = DocumentId,
        Kind = kind,
        Trigger = IndexRunTrigger.DocumentReady,
        EventTime = eventTime
    };

    private Task RunAsync(IndexDocumentJobKind kind, DateTime? eventTime)
        => GetRequiredService<IndexDocumentJob>().ExecuteAsync(Args(kind, eventTime));

    private async Task SeedClaimAsync(int startedMinutesAgo)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var row = new IndexedDocument(DocumentId, null, TypeA, Owner);
            row.RecordEvent(T1);
            row.BeginIndexing(GetRequiredService<IClock>().Now.AddMinutes(-startedMinutesAgo));
            await Repo<IndexedDocument>().InsertAsync(row);
        });
    }

    private async Task<List<Chunk>> ChunksAsync()
        => (await GetRequiredService<IChunkRepository>().GetListAsync(c => c.DocumentId == DocumentId)).OrderBy(c => c.Ordinal).ToList();

    private IRepository<TEntity, Guid> Repo<TEntity>() where TEntity : class, Volo.Abp.Domain.Entities.IEntity<Guid>
        => GetRequiredService<IRepository<TEntity, Guid>>();
}

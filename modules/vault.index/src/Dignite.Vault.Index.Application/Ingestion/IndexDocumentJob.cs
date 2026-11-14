using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Compilation;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Runs;
using Dignite.Vault.Index.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// The one job that changes what Index holds about a document. Lifecycle kinds flip a mark or delete the derived
/// data. <see cref="IndexDocumentJobKind.Sync"/> runs in three short units of work so no transaction is held across
/// model calls:
/// <list type="number">
/// <item><b>Claim</b> the document, or, if another job holds a live claim, ask it to run again and stop. This is what
/// keeps one job per document at a time.</item>
/// <item><b>Compute</b> outside any transaction: hash, chunk, embed. Skipped when the text, the chunking settings and
/// the embedding generation are all unchanged.</item>
/// <item><b>Replace</b> the chunks and the vectors and record the run, in one transaction, so retrieval never sees a
/// document with neither the old chunks nor the new ones.</item>
/// </list>
/// Errors carrying a <c>VaultIndex:</c> code are deterministic and end the document in a terminal state at once.
/// Anything else is transient: it is counted on the document and rethrown so ABP retries, until
/// <see cref="VaultIndexIngestionOptions.MaxFailedAttempts"/>.
/// <para>
/// <b>A new embedding model.</b> Vectors go into the generation of the configured model. Once that is no longer the
/// serving generation's model, the first vectors from it create a building generation and start the walk that queues
/// an <see cref="IndexDocumentJobKind.Embed"/> for every document with chunks. Meanwhile a sync whose text is unchanged
/// embeds the existing chunks into the new generation instead of skipping, and one that re-chunks also writes the
/// serving generation, from its own model, when the host still has it, so search keeps finding the new text. After
/// each write into the building generation the job tries to switch to it.
/// </para>
/// </summary>
public class IndexDocumentJob : AsyncBackgroundJob<IndexDocumentJobArgs>, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDocumentIntegrationService _integrationService;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IChunkRepository _chunkRepository;
    private readonly IRepository<IndexRun, Guid> _runRepository;
    private readonly IRepository<Artifact, Guid> _artifactRepository;
    private readonly IChunkSearchIndex _searchIndex;
    private readonly IndexProfileResolver _profileResolver;
    private readonly IndexGenerationManager _generationManager;
    private readonly IndexChunker _chunker;
    private readonly IndexEmbedder _embedder;
    private readonly IndexEnricherProvider _enricherProvider;
    private readonly DocumentSummaryCompilerProvider _compilerProvider;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guidGenerator;
    private readonly VaultIndexIngestionOptions _options;

    public IndexDocumentJob(
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IDocumentIntegrationService integrationService,
        IRepository<IndexedDocument, Guid> documentRepository,
        IChunkRepository chunkRepository,
        IRepository<IndexRun, Guid> runRepository,
        IRepository<Artifact, Guid> artifactRepository,
        IChunkSearchIndex searchIndex,
        IndexProfileResolver profileResolver,
        IndexGenerationManager generationManager,
        IndexChunker chunker,
        IndexEmbedder embedder,
        IndexEnricherProvider enricherProvider,
        DocumentSummaryCompilerProvider compilerProvider,
        IBackgroundJobManager backgroundJobManager,
        IClock clock,
        IGuidGenerator guidGenerator,
        IOptions<VaultIndexIngestionOptions> options)
    {
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _integrationService = integrationService;
        _documentRepository = documentRepository;
        _chunkRepository = chunkRepository;
        _runRepository = runRepository;
        _artifactRepository = artifactRepository;
        _searchIndex = searchIndex;
        _profileResolver = profileResolver;
        _generationManager = generationManager;
        _chunker = chunker;
        _embedder = embedder;
        _enricherProvider = enricherProvider;
        _compilerProvider = compilerProvider;
        _backgroundJobManager = backgroundJobManager;
        _clock = clock;
        _guidGenerator = guidGenerator;
        _options = options.Value;
    }

    public override async Task ExecuteAsync(IndexDocumentJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            switch (args.Kind)
            {
                case IndexDocumentJobKind.Sync:
                    await SyncAsync(args);
                    break;
                case IndexDocumentJobKind.Embed:
                    await EmbedChunksAsync(args);
                    break;
                default:
                    await ApplyLifecycleAsync(args);
                    break;
            }
        }
    }

    protected virtual async Task SyncAsync(IndexDocumentJobArgs args)
    {
        if (!_embedder.IsConfigured)
        {
            Logger.LogWarning("Vault Index has no embedding model; document {DocumentId} is not indexed.", args.DocumentId);
            return;
        }

        var document = await _integrationService.FindReadyAsync(args.DocumentId);

        var claim = await ClaimAsync(args, document);
        if (claim is null)
        {
            return;
        }

        try
        {
            var plan = await ComputeAsync(document!, claim, args.Force);
            var rerun = await ReplaceAsync(document!, claim, plan);
            if (plan.TargetIsBuilding && plan.Vectors is not null)
            {
                await TrySwitchAsync(plan.GenerationId, rethrow: false);
            }

            if (rerun)
            {
                await EnqueueSyncAsync(args.DocumentId, args.Trigger);
            }
            else if (plan.Compilation != IndexCompilation.None && _compilerProvider.IsAvailable)
            {
                // The artifacts follow the chunks: the compile job compiles only what this sync made stale.
                await EnqueueCompileAsync(args.DocumentId);
            }
        }
        catch (BusinessException exception) when (exception.Code?.StartsWith(VaultIndexErrorCodes.Namespace + ":", StringComparison.Ordinal) == true)
        {
            Logger.LogWarning("Indexing document {DocumentId} failed for good: {Code}.", args.DocumentId, exception.Code);
            await FailAsync(args, claim, Describe(exception), terminal: true);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Indexing document {DocumentId} failed; attempt {Attempt}.", args.DocumentId, claim.AttemptNumber);
            if (!await FailAsync(args, claim, exception.Message, terminal: false))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Takes the document for this job. Returns <c>null</c> when there is nothing to do: a redelivered or stale event,
    /// a document that is no longer Ready (its last Ready version keeps serving), or a live claim held by another
    /// job, which is then asked to run once more.
    /// </summary>
    protected virtual async Task<SyncClaim?> ClaimAsync(IndexDocumentJobArgs args, DocumentIntegrationDto? document)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        var now = _clock.Now;
        var row = await _documentRepository.FindAsync(args.DocumentId);

        if (row is not null && args.EventTime is { } eventTime && !row.IsNewerThanApplied(eventTime))
        {
            await uow.CompleteAsync();
            return null;
        }

        if (document is null)
        {
            if (row is not null)
            {
                row.RecordEvent(args.EventTime);
                await _documentRepository.UpdateAsync(row);
            }

            await uow.CompleteAsync();
            return null;
        }

        if (row is null)
        {
            row = new IndexedDocument(document.Id, _currentTenant.Id, document.DocumentTypeId, document.OwnerId);
            row.RecordEvent(args.EventTime);
            row.SetTitle(document.Title);
            row.BeginIndexing(now);
            await _documentRepository.InsertAsync(row);
        }
        else if (row.IsClaimed(now, _options.ClaimTimeout))
        {
            row.RequestRerun(args.EventTime);
            await _documentRepository.UpdateAsync(row);
            await uow.CompleteAsync();
            return null;
        }
        else
        {
            row.RecordEvent(args.EventTime);
            row.SetTitle(document.Title);
            row.BeginIndexing(now);
            await _documentRepository.UpdateAsync(row);
        }

        var run = new IndexRun(_guidGenerator.Create(), _currentTenant.Id, row.Id, args.Trigger, row.FailedAttempts + 1, now);
        await _runRepository.InsertAsync(run);
        await uow.CompleteAsync();

        return new SyncClaim(
            run.Id,
            run.AttemptNumber,
            row.LastEventTime,
            row.ContentHash,
            row.ProfileStamp,
            row.GenerationId,
            row.DocumentTypeId,
            row.OwnerId);
    }

    /// <summary>
    /// Everything that calls out (the model) or takes time, with no transaction open. Three outcomes: nothing changed;
    /// the text and settings are unchanged but the chunks have no vectors yet in the configured model's generation, so
    /// they are embedded again as they are (no re-chunking, no enrichment call); or the document is chunked and embedded.
    /// </summary>
    protected virtual async Task<SyncPlan> ComputeAsync(DocumentIntegrationDto document, SyncClaim claim, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(document.Markdown))
        {
            throw new BusinessException(VaultIndexErrorCodes.EmptyDocument);
        }

        if (document.Markdown.Length > _options.MaxMarkdownLength)
        {
            throw new BusinessException(VaultIndexErrorCodes.DocumentTooLarge)
                .WithData("Length", document.Markdown.Length)
                .WithData("MaxLength", _options.MaxMarkdownLength);
        }

        // The settings as they can be applied: with no chat model the profile's enrichment is off, and the stamp
        // says so, so adding a model later re-enriches on the next rebuild.
        var profile = await _profileResolver.ResolveAsync(document.DocumentTypeId);
        profile = profile with { Enrichment = _enricherProvider.Effective(profile.Enrichment) };
        var text = _chunker.Prepare(document.Markdown);
        var contentHash = Sha256(text);
        var modelId = _embedder.GetModelId();
        var current = await _generationManager.FindTargetAsync(modelId);

        var sameText = !force && claim.ContentHash == contentHash && claim.ProfileStamp == profile.Stamp;
        if (sameText && current is not null && claim.GenerationId == current.Id)
        {
            return new SyncPlan(contentHash, profile.Stamp, current.Id, null, null, null)
            {
                ModelId = modelId,
                Compilation = profile.Compilation
            };
        }

        if (sameText && await EmbedExistingAsync(document.Id, document.Title) is { } existing)
        {
            var reembedTarget = await GetOrCreateTargetAsync(modelId, existing.Dimensions);
            return new SyncPlan(contentHash, profile.Stamp, reembedTarget.Id, null, existing.Embedding.Vectors, existing.Embedding.InputTokenCount)
            {
                ModelId = modelId,
                Compilation = profile.Compilation,
                EmbeddedChunkIds = existing.ChunkIds,
                TargetIsBuilding = reembedTarget.Status == IndexGenerationStatus.Building
            };
        }

        var enricher = _enricherProvider.Create(profile.Enrichment, document.Language);
        var drafts = await _chunker.ChunkAsync(text, document.Id.ToString(), profile, enricher);
        if (enricher is { CachedChunkCount: > 0 } reusing)
        {
            Logger.LogInformation(
                "Document {DocumentId}: {Reused} of {Chunks} chunks reused their enrichment; {Calls} enrichment calls.",
                document.Id, reusing.CachedChunkCount, drafts.Count, reusing.CallCount);
        }
        if (drafts.Count == 0)
        {
            throw new BusinessException(VaultIndexErrorCodes.EmptyDocument);
        }

        if (drafts.Count > _options.MaxChunksPerDocument)
        {
            throw new BusinessException(VaultIndexErrorCodes.TooManyChunks)
                .WithData("Count", drafts.Count)
                .WithData("Max", _options.MaxChunksPerDocument);
        }

        // The document title in front of every chunk (which already starts with its heading path) gives the vector
        // the context a bare passage lacks, and the questions an enricher found pull it toward how people ask. The
        // stored chunk text stays as the chunker cut it.
        var inputs = drafts
            .Select(d => ChunkContext.ComposeForEmbedding(document.Title, d.Text, d.Questions))
            .ToList();
        var embedding = await _embedder.EmbedAsync(inputs);
        var generation = await GetOrCreateTargetAsync(modelId, CheckDimensions(embedding.Vectors));
        var building = generation.Status == IndexGenerationStatus.Building;

        // While the new model's generation is built, the serving one keeps answering searches: it gets the new chunks
        // too, embedded by the model it was built with, when the host still has that model.
        var serving = building ? await _generationManager.FindServingAsync() : null;
        EmbeddingBatch? servingEmbedding = null;
        if (serving is not null && !string.Equals(serving.ModelId, modelId, StringComparison.Ordinal) && _embedder.CanEmbed(serving.ModelId))
        {
            servingEmbedding = await _embedder.EmbedAsync(serving.ModelId, inputs);
            CheckDimensions(servingEmbedding.Vectors);
        }

        return new SyncPlan(
            contentHash, profile.Stamp, generation.Id, drafts, embedding.Vectors,
            servingEmbedding is null ? embedding.InputTokenCount : embedding.InputTokenCount + servingEmbedding.InputTokenCount)
        {
            ModelId = modelId,
            TargetIsBuilding = building,
            ServingGenerationId = servingEmbedding is null ? null : serving!.Id,
            ServingVectors = servingEmbedding?.Vectors,
            Compilation = profile.Compilation,
            LlmInputTokenCount = enricher?.InputTokenCount,
            LlmOutputTokenCount = enricher?.OutputTokenCount,
            Note = enricher is { FailedChunkCount: > 0 } failed
                ? $"Enrichment failed for {failed.FailedChunkCount} of {drafts.Count} chunks; they are indexed without it."
                : null
        };
    }

    /// <summary>
    /// Replaces the document's chunks and vectors and completes the run, in one transaction. Returns whether another
    /// sync must follow: an event arrived while this one ran, or the generations moved (a new model's build started,
    /// switched or was abandoned), so the vectors may not be where the configured model's belong now.
    /// </summary>
    protected virtual async Task<bool> ReplaceAsync(DocumentIntegrationDto document, SyncClaim claim, SyncPlan plan)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        var now = _clock.Now;
        var row = await _documentRepository.FindAsync(document.Id);
        if (row is null)
        {
            // Removed while this job computed (permanently deleted, or turned into a container). Write nothing.
            await uow.CompleteAsync();
            return false;
        }

        // Vectors go only into a generation still in use; a retired one's would never be read.
        var serving = await _generationManager.FindServingAsync();
        var building = await _generationManager.FindBuildingAsync();
        bool IsLive(Guid generationId) => generationId == serving?.Id || generationId == building?.Id;
        var stale = (await _generationManager.FindTargetAsync(plan.ModelId))?.Id != plan.GenerationId;

        List<Chunk>? existing = null;
        if (plan.EmbeddedChunkIds is not null)
        {
            // Re-embedded as they were; only another job taking over an expired claim could have replaced them since.
            existing = (await _chunkRepository.GetListAsync(c => c.DocumentId == document.Id)).OrderBy(c => c.Ordinal).ToList();
            if (!existing.Select(c => c.Id).SequenceEqual(plan.EmbeddedChunkIds))
            {
                throw new InvalidOperationException($"The chunks of document {document.Id} changed while they were embedded.");
            }
        }

        // A lifecycle event applied while this job ran (the document went to the recycle bin, or was reclassified)
        // is newer than the version just indexed: keep its mark and sync once more to see where the document stands.
        var lifecycleChanged = row.LastEventTime != claim.LastEventTime;
        var wasHidden = row.IsHidden;
        var chunkCount = plan.Drafts?.Count ?? row.ChunkCount;
        row.CompleteIndexing(
            plan.ContentHash, plan.ProfileStamp, plan.GenerationId, chunkCount, document.DocumentTypeId, document.OwnerId,
            document.Title, document.Language, now, clearLifecycleMarks: !lifecycleChanged);
        if (lifecycleChanged || stale)
        {
            row.RequestRerun(null);
        }

        if (plan.Drafts is not null)
        {
            // An operator's disable mark belongs to the text, not to the chunk row: a chunk with the same text after the
            // rebuild keeps it, and a changed text ends it.
            var disabled = (await _chunkRepository.GetDisabledContentHashesAsync(document.Id)).ToHashSet(StringComparer.Ordinal);
            await _chunkRepository.DeleteDirectAsync(c => c.DocumentId == document.Id);
            var chunks = plan.Drafts
                .Select(d =>
                {
                    var hash = Sha256(d.Text);
                    return new Chunk(
                        _guidGenerator.Create(), _currentTenant.Id, document.Id, document.DocumentTypeId, document.OwnerId,
                        d.Ordinal, d.HeadingPath, d.Text, hash, row.IsHidden, disabled.Contains(hash), d.Keywords, d.Questions);
                })
                .ToList();
            await _chunkRepository.InsertManyAsync(chunks);

            // The old chunks' vectors go from every generation; the new ones into each generation they were computed for.
            await _searchIndex.DeleteDocumentAsync(document.Id);
            if (IsLive(plan.GenerationId))
            {
                await WriteVectorsAsync(row, document.DocumentTypeId, document.OwnerId, chunks, plan.GenerationId, plan.Vectors!);
            }

            if (plan.ServingGenerationId is { } servingId && IsLive(servingId))
            {
                await WriteVectorsAsync(row, document.DocumentTypeId, document.OwnerId, chunks, servingId, plan.ServingVectors!);
            }
        }
        else
        {
            if (existing is not null && IsLive(plan.GenerationId))
            {
                await WriteVectorsAsync(row, document.DocumentTypeId, document.OwnerId, existing, plan.GenerationId, plan.Vectors!);
            }

            if (claim.DocumentTypeId != document.DocumentTypeId || claim.OwnerId != document.OwnerId || wasHidden != row.IsHidden)
            {
                // Same text, but a new type or owner (a reclassification), or back in retrieval: only the read-scope facts move.
                await CopyFiltersAsync(row);
            }
        }

        if (!plan.Compilation.HasFlag(IndexCompilation.Summaries))
        {
            // The profile no longer asks for summaries: the rebuild that follows turning them off removes them.
            await _artifactRepository.DeleteDirectAsync(a => a.DocumentId == document.Id && a.TemplateName == ArtifactTemplateNames.Summaries);
        }

        var rerun = row.TakeRerunRequest();
        await _documentRepository.UpdateAsync(row);

        var run = await _runRepository.FindAsync(claim.RunId);
        if (run is not null)
        {
            if (plan.Vectors is not null)
            {
                run.Succeed(now, chunkCount, plan.EmbeddingTokenCount, plan.LlmInputTokenCount, plan.LlmOutputTokenCount, plan.Note);
            }
            else
            {
                run.Skip(now, "Text, chunking settings and embedding generation unchanged.");
            }

            await _runRepository.UpdateAsync(run);
        }

        await uow.CompleteAsync();
        return rerun;
    }

    /// <summary>Records a failure. Returns whether it is terminal, so the caller knows not to rethrow.</summary>
    protected virtual async Task<bool> FailAsync(IndexDocumentJobArgs args, SyncClaim claim, string message, bool terminal)
    {
        bool rerun;
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true))
        {
            var now = _clock.Now;
            var row = await _documentRepository.FindAsync(args.DocumentId);
            if (row is null)
            {
                await uow.CompleteAsync();
                return true;
            }

            if (!terminal)
            {
                row.FailIndexing(terminal: false);
                terminal = row.FailedAttempts >= _options.MaxFailedAttempts;
            }

            if (terminal)
            {
                row.FailIndexing(terminal: true);
            }

            // A newer event that arrived during a run that has now failed for good deserves its own try.
            rerun = terminal && row.TakeRerunRequest();
            await _documentRepository.UpdateAsync(row);

            var run = await _runRepository.FindAsync(claim.RunId);
            if (run is not null)
            {
                run.Fail(now, message);
                await _runRepository.UpdateAsync(run);
            }

            await uow.CompleteAsync();
        }

        if (rerun)
        {
            await EnqueueSyncAsync(args.DocumentId, args.Trigger);
        }

        return terminal;
    }

    /// <summary>
    /// <see cref="IndexDocumentJobKind.Embed"/>: the document's chunks, as they are, embedded by the configured model into
    /// its generation. Takes no claim and leaves the document's status alone: it changes only which generation the
    /// unchanged chunks have vectors in. A sync that replaces the chunks meanwhile writes that generation itself, and the
    /// row's concurrency stamp orders the write against a lifecycle job changing the read-scope facts. A failure is
    /// recorded as a run; a transient one is rethrown so ABP retries, until its own job timeout.
    /// </summary>
    protected virtual async Task EmbedChunksAsync(IndexDocumentJobArgs args)
    {
        if (!_embedder.IsConfigured)
        {
            return;
        }

        var modelId = _embedder.GetModelId();
        Guid runId;
        string? title;
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true))
        {
            var row = await _documentRepository.FindAsync(args.DocumentId);
            var current = await _generationManager.FindTargetAsync(modelId);
            if (row is null || row.ChunkCount == 0 || (current is not null && row.GenerationId == current.Id))
            {
                await uow.CompleteAsync();
                if (current is { Status: IndexGenerationStatus.Building })
                {
                    // Already there: on a retry after a switch that failed, this is what tries it again.
                    await TrySwitchAsync(current.Id, rethrow: true);
                }

                return;
            }

            var since = current?.CreationTime ?? DateTime.MinValue;
            var failed = await _runRepository.CountAsync(r =>
                r.DocumentId == row.Id && r.Trigger == args.Trigger && r.Status == IndexRunStatus.Failed && r.StartedTime >= since);
            var run = new IndexRun(_guidGenerator.Create(), _currentTenant.Id, row.Id, args.Trigger, failed + 1, _clock.Now);
            await _runRepository.InsertAsync(run);
            runId = run.Id;
            title = row.Title;
            await uow.CompleteAsync();
        }

        try
        {
            // The title Index kept, which a sync's pulled title equals unless it was cut at IndexingConsts.MaxTitleLength.
            var existing = await EmbedExistingAsync(args.DocumentId, title);
            if (existing is null)
            {
                await FinishRunAsync(runId, run => run.Skip(_clock.Now, "The document has no chunks."));
                return;
            }

            var target = await GetOrCreateTargetAsync(modelId, existing.Dimensions);
            if (await WriteEmbeddingAsync(args.DocumentId, runId, target.Id, existing) && target.Status == IndexGenerationStatus.Building)
            {
                await TrySwitchAsync(target.Id, rethrow: true);
            }
        }
        catch (BusinessException exception) when (exception.Code?.StartsWith(VaultIndexErrorCodes.Namespace + ":", StringComparison.Ordinal) == true)
        {
            Logger.LogWarning("Embedding document {DocumentId} into the new generation failed for good: {Code}.", args.DocumentId, exception.Code);
            await FinishRunAsync(runId, run => run.Fail(_clock.Now, Describe(exception)));
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Embedding document {DocumentId} into the new generation failed.", args.DocumentId);
            await FinishRunAsync(runId, run => run.Fail(_clock.Now, exception.Message));
            throw;
        }
    }

    /// <summary>
    /// Writes the vectors of <see cref="EmbedChunksAsync"/> and moves the document to <paramref name="generationId"/>, in
    /// one transaction, unless the chunks changed while they were embedded. Returns whether it wrote.
    /// </summary>
    protected virtual async Task<bool> WriteEmbeddingAsync(Guid documentId, Guid runId, Guid generationId, ExistingChunksEmbedding embedding)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        var now = _clock.Now;
        var run = await _runRepository.GetAsync(runId);
        var row = await _documentRepository.FindAsync(documentId);
        var chunks = (await _chunkRepository.GetListAsync(c => c.DocumentId == documentId)).OrderBy(c => c.Ordinal).ToList();
        var serving = await _generationManager.FindServingAsync();
        var building = await _generationManager.FindBuildingAsync();

        string? skipped = null;
        if (row is null)
        {
            skipped = "The document was removed while its chunks were embedded.";
        }
        else if (!chunks.Select(c => c.Id).SequenceEqual(embedding.ChunkIds))
        {
            // The sync that replaced them wrote the generation itself, or asked for another sync that will.
            skipped = "The chunks changed while they were embedded.";
        }
        else if (generationId != serving?.Id && generationId != building?.Id)
        {
            skipped = "The generation was abandoned while the chunks were embedded.";
        }
        else if (row.GenerationId == generationId)
        {
            skipped = "Another job embedded the chunks first.";
        }

        if (skipped is not null)
        {
            run.Skip(now, skipped);
            await _runRepository.UpdateAsync(run);
            await uow.CompleteAsync();
            return false;
        }

        await WriteVectorsAsync(row!, row!.DocumentTypeId, row.OwnerId, chunks, generationId, embedding.Embedding.Vectors);
        row.SetGeneration(generationId);
        await _documentRepository.UpdateAsync(row);
        run.Succeed(now, chunks.Count, embedding.Embedding.InputTokenCount);
        await _runRepository.UpdateAsync(run);
        await uow.CompleteAsync();
        return true;
    }

    /// <summary>The document's current chunks embedded by the configured model, in chunk order; <c>null</c> when it has none.</summary>
    protected virtual async Task<ExistingChunksEmbedding?> EmbedExistingAsync(Guid documentId, string? title)
    {
        var chunks = (await _chunkRepository.GetListAsync(c => c.DocumentId == documentId)).OrderBy(c => c.Ordinal).ToList();
        if (chunks.Count == 0)
        {
            return null;
        }

        var embedding = await _embedder.EmbedAsync(chunks.Select(c => ChunkContext.ComposeForEmbedding(title, c.Text, c.Questions)).ToList());
        return new ExistingChunksEmbedding(chunks.Select(c => c.Id).ToList(), embedding, CheckDimensions(embedding.Vectors));
    }

    protected virtual async Task WriteVectorsAsync(
        IndexedDocument row,
        Guid documentTypeId,
        Guid? ownerId,
        IReadOnlyList<Chunk> chunks,
        Guid generationId,
        IReadOnlyList<ReadOnlyMemory<float>> vectors)
    {
        var records = chunks
            .Select((c, i) => new ChunkVector(c.Id, row.Id, documentTypeId, ownerId, row.IsHidden, vectors[i], c.IsDisabled))
            .ToList();
        await _searchIndex.ReplaceDocumentAsync(row.Id, generationId, records);
    }

    /// <summary>
    /// The generation for the configured model, created when missing, in its own transaction. Creating a building one
    /// starts the walk that embeds every document into it; abandoning one deletes its vectors.
    /// </summary>
    protected virtual async Task<IndexGeneration> GetOrCreateTargetAsync(string modelId, int dimensions)
    {
        IndexGenerationTarget target;
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true))
        {
            target = await _generationManager.GetOrCreateTargetAsync(modelId, dimensions);
            await uow.CompleteAsync();
        }

        if (target.StartedBuilding)
        {
            Logger.LogInformation("Embedding model {ModelId} is not the serving generation's: building a new generation.", modelId);
            await _backgroundJobManager.EnqueueAsync(
                new IndexBackfillJobArgs { TenantId = _currentTenant.Id, EmbedOnly = true },
                BackgroundJobPriority.Low);
        }

        if (target.Abandoned)
        {
            await DeleteRetiredVectorsAsync();
        }

        return target.Generation;
    }

    /// <summary>
    /// Switches to the building generation if this write completed it. Two jobs completing it at once collide on the
    /// generation rows; the loser finds it switched. A sync swallows any other failure, since its own work is done;
    /// an embed job rethrows it, and its retry tries again.
    /// </summary>
    protected virtual async Task TrySwitchAsync(Guid generationId, bool rethrow)
    {
        bool switched;
        try
        {
            using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
            switched = await _generationManager.TrySwitchAsync(generationId);
            await uow.CompleteAsync();
        }
        catch (AbpDbConcurrencyException)
        {
            return;
        }
        catch (Exception exception) when (!rethrow)
        {
            Logger.LogWarning(exception, "Switching to generation {GenerationId} failed; the next write into it tries again.", generationId);
            return;
        }

        if (switched)
        {
            Logger.LogInformation("Every document has vectors in generation {GenerationId}; search now reads it.", generationId);
            await DeleteRetiredVectorsAsync();
        }
    }

    protected virtual async Task DeleteRetiredVectorsAsync()
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: false);
        await _generationManager.DeleteRetiredVectorsAsync();
        await uow.CompleteAsync();
    }

    protected virtual async Task FinishRunAsync(Guid runId, Action<IndexRun> finish)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        var run = await _runRepository.FindAsync(runId);
        if (run is not null)
        {
            finish(run);
            await _runRepository.UpdateAsync(run);
        }

        await uow.CompleteAsync();
    }

    /// <summary>The vectors' common length, refused when they differ from each other or from what the store was created for.</summary>
    protected virtual int CheckDimensions(IReadOnlyList<ReadOnlyMemory<float>> vectors)
    {
        var dimensions = vectors[0].Length;
        if (vectors.Any(v => v.Length != dimensions)
            || (_searchIndex.VectorDimensions is { } storageDimensions && storageDimensions != dimensions))
        {
            throw new BusinessException(VaultIndexErrorCodes.EmbeddingDimensionMismatch)
                .WithData("Dimensions", dimensions)
                .WithData("StorageDimensions", _searchIndex.VectorDimensions ?? dimensions);
        }

        return dimensions;
    }

    protected virtual async Task ApplyLifecycleAsync(IndexDocumentJobArgs args)
    {
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true))
        {
            var row = await _documentRepository.FindAsync(args.DocumentId);
            var apply = row is not null && (args.EventTime is not { } eventTime || row.IsNewerThanApplied(eventTime));
            if (apply)
            {
                switch (args.Kind)
                {
                    case IndexDocumentJobKind.Archive:
                        row!.Archive(args.EventTime);
                        await _documentRepository.UpdateAsync(row);
                        await CopyFiltersAsync(row);
                        break;
                    case IndexDocumentJobKind.Unarchive:
                        row!.Unarchive(args.EventTime);
                        await _documentRepository.UpdateAsync(row);
                        await CopyFiltersAsync(row);
                        break;
                    case IndexDocumentJobKind.Withhold:
                        row!.Withhold(args.EventTime);
                        await _documentRepository.UpdateAsync(row);
                        await CopyFiltersAsync(row);
                        break;
                    case IndexDocumentJobKind.Remove:
                        await RemoveAsync(row!);
                        break;
                }
            }

            await uow.CompleteAsync();
        }

        // A restored document may be Ready and never indexed, or changed while in the bin: look at it again.
        if (args.Kind == IndexDocumentJobKind.Unarchive)
        {
            await EnqueueSyncAsync(args.DocumentId, IndexRunTrigger.DocumentReady);
        }
    }

    /// <summary>Copies the document's read-scope facts and its hidden state onto its chunks and vectors.</summary>
    protected virtual async Task CopyFiltersAsync(IndexedDocument row)
    {
        await _chunkRepository.UpdateDocumentFiltersAsync(row.Id, row.DocumentTypeId, row.OwnerId, row.IsHidden);
        await _searchIndex.UpdateDocumentFiltersAsync(row.Id, row.DocumentTypeId, row.OwnerId, row.IsHidden);
    }

    /// <summary>Deletes everything derived from the document, so nothing of a deleted document survives in Index.</summary>
    protected virtual async Task RemoveAsync(IndexedDocument row)
    {
        await _chunkRepository.DeleteDirectAsync(c => c.DocumentId == row.Id);
        await _searchIndex.DeleteDocumentAsync(row.Id);
        await _artifactRepository.DeleteDirectAsync(a => a.DocumentId == row.Id);
        await _runRepository.DeleteDirectAsync(r => r.DocumentId == row.Id);
        await _documentRepository.DeleteAsync(row);
    }

    protected virtual Task EnqueueSyncAsync(Guid documentId, IndexRunTrigger trigger)
    {
        return _backgroundJobManager.EnqueueAsync(new IndexDocumentJobArgs
        {
            DocumentId = documentId,
            TenantId = _currentTenant.Id,
            Kind = IndexDocumentJobKind.Sync,
            Trigger = trigger
        });
    }

    protected virtual Task EnqueueCompileAsync(Guid documentId)
    {
        return _backgroundJobManager.EnqueueAsync(
            new IndexCompileJobArgs { DocumentId = documentId, TenantId = _currentTenant.Id },
            BackgroundJobPriority.Low);
    }

    private static string Describe(BusinessException exception)
    {
        var data = string.Join(", ", exception.Data.Keys.Cast<object>().Select(k => $"{k}={exception.Data[k]}"));
        return data.Length == 0 ? exception.Code! : $"{exception.Code} ({data})";
    }

    protected static string Sha256(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>What the claim saw of the document's previous state, for the change check and the replace.</summary>
    protected sealed record SyncClaim(
        Guid RunId,
        int AttemptNumber,
        DateTime? LastEventTime,
        string? ContentHash,
        string? ProfileStamp,
        Guid? GenerationId,
        Guid DocumentTypeId,
        Guid? OwnerId);

    /// <summary>A document's existing chunks embedded by the configured model: their ids in chunk order, and the vectors.</summary>
    protected sealed record ExistingChunksEmbedding(IReadOnlyList<Guid> ChunkIds, EmbeddingBatch Embedding, int Dimensions);

    /// <summary>
    /// The computed result. <see cref="Drafts"/> is <c>null</c> when the chunks stay: then <see cref="Vectors"/> is set
    /// when they were embedded again for a new generation (<see cref="EmbeddedChunkIds"/>), and <c>null</c> when only
    /// the read-scope facts may need refreshing.
    /// </summary>
    protected sealed record SyncPlan(
        string ContentHash,
        string ProfileStamp,
        Guid GenerationId,
        IReadOnlyList<IndexChunkDraft>? Drafts,
        IReadOnlyList<ReadOnlyMemory<float>>? Vectors,
        long? EmbeddingTokenCount)
    {
        /// <summary>What the profile asks to compile; the artifacts are compiled by their own job after the sync.</summary>
        public IndexCompilation Compilation { get; init; }

        public long? LlmInputTokenCount { get; init; }

        public long? LlmOutputTokenCount { get; init; }

        /// <summary>Something the run should say although it succeeded, such as chunks the enricher could not enrich.</summary>
        public string? Note { get; init; }

        /// <summary>The configured model the plan was computed with; <see cref="GenerationId"/> is its generation.</summary>
        public string ModelId { get; init; } = default!;

        /// <summary><see cref="GenerationId"/> was being built when the plan was computed, so the write may complete it.</summary>
        public bool TargetIsBuilding { get; init; }

        /// <summary>The existing chunks <see cref="Vectors"/> belong to, in order, when the plan re-embedded them.</summary>
        public IReadOnlyList<Guid>? EmbeddedChunkIds { get; init; }

        /// <summary>
        /// The serving generation, when <see cref="GenerationId"/> is a building one and the host still has the serving
        /// model: <see cref="ServingVectors"/> keep the new chunks findable until the switch.
        /// </summary>
        public Guid? ServingGenerationId { get; init; }

        public IReadOnlyList<ReadOnlyMemory<float>>? ServingVectors { get; init; }
    }
}

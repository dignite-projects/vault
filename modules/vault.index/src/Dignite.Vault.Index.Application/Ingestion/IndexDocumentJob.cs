using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
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
        _backgroundJobManager = backgroundJobManager;
        _clock = clock;
        _guidGenerator = guidGenerator;
        _options = options.Value;
    }

    public override async Task ExecuteAsync(IndexDocumentJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            if (args.Kind == IndexDocumentJobKind.Sync)
            {
                await SyncAsync(args);
            }
            else
            {
                await ApplyLifecycleAsync(args);
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
            if (rerun)
            {
                await EnqueueSyncAsync(args.DocumentId, args.Trigger);
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

    /// <summary>Everything that calls out (the model) or takes time, with no transaction open.</summary>
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

        // The settings as they can be applied: with no enrichment model the profile's enrichment is off, and the stamp
        // says so, so adding a model later re-enriches on the next rebuild.
        var profile = await _profileResolver.ResolveAsync(document.DocumentTypeId);
        profile = profile with { Enrichment = _enricherProvider.Effective(profile.Enrichment) };
        var text = _chunker.Prepare(document.Markdown);
        var contentHash = Sha256(text);
        var modelId = _embedder.GetModelId();
        var serving = await _generationManager.FindServingAsync();

        var unchanged = !force
                        && claim.ContentHash == contentHash
                        && claim.ProfileStamp == profile.Stamp
                        && serving is not null
                        && claim.GenerationId == serving.Id
                        && serving.ModelId == modelId;
        if (unchanged)
        {
            return new SyncPlan(contentHash, profile.Stamp, serving!.Id, null, null, null);
        }

        var enricher = _enricherProvider.Create(profile.Enrichment, document.Language);
        var drafts = await _chunker.ChunkAsync(text, document.Id.ToString(), profile, enricher);
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

        var dimensions = embedding.Vectors[0].Length;
        if (embedding.Vectors.Any(v => v.Length != dimensions)
            || (_searchIndex.VectorDimensions is { } storageDimensions && storageDimensions != dimensions))
        {
            throw new BusinessException(VaultIndexErrorCodes.EmbeddingDimensionMismatch)
                .WithData("Dimensions", dimensions)
                .WithData("StorageDimensions", _searchIndex.VectorDimensions ?? dimensions);
        }

        var generation = await _generationManager.GetOrCreateServingAsync(modelId, dimensions);
        return new SyncPlan(contentHash, profile.Stamp, generation.Id, drafts, embedding.Vectors, embedding.InputTokenCount)
        {
            LlmInputTokenCount = enricher?.InputTokenCount,
            LlmOutputTokenCount = enricher?.OutputTokenCount,
            Note = enricher is { FailedChunkCount: > 0 } failed
                ? $"Enrichment failed for {failed.FailedChunkCount} of {drafts.Count} chunks; they are indexed without it."
                : null
        };
    }

    /// <summary>
    /// Replaces the document's chunks and vectors and completes the run, in one transaction. Returns whether another
    /// sync must follow: an event arrived while this one ran.
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

        // A lifecycle event applied while this job ran (the document went to the recycle bin, or was reclassified)
        // is newer than the version just indexed: keep its mark and sync once more to see where the document stands.
        var lifecycleChanged = row.LastEventTime != claim.LastEventTime;
        var wasHidden = row.IsHidden;
        var chunkCount = plan.Drafts?.Count ?? row.ChunkCount;
        row.CompleteIndexing(
            plan.ContentHash, plan.ProfileStamp, plan.GenerationId, chunkCount, document.DocumentTypeId, document.OwnerId,
            document.Title, now, clearLifecycleMarks: !lifecycleChanged);
        if (lifecycleChanged)
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

            var vectors = chunks
                .Select((c, i) => new ChunkVector(
                    c.Id, document.Id, document.DocumentTypeId, document.OwnerId, row.IsHidden, plan.Vectors![i], c.IsDisabled))
                .ToList();
            await _searchIndex.ReplaceDocumentAsync(document.Id, plan.GenerationId, vectors);
        }
        else if (claim.DocumentTypeId != document.DocumentTypeId || claim.OwnerId != document.OwnerId || wasHidden != row.IsHidden)
        {
            // Same text, but a new type or owner (a reclassification), or back in retrieval: only the read-scope facts move.
            await CopyFiltersAsync(row);
        }

        var rerun = row.TakeRerunRequest();
        await _documentRepository.UpdateAsync(row);

        var run = await _runRepository.FindAsync(claim.RunId);
        if (run is not null)
        {
            if (plan.Drafts is not null)
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

    /// <summary>The computed result. <see cref="Drafts"/> is <c>null</c> when only the read-scope facts may need refreshing.</summary>
    protected sealed record SyncPlan(
        string ContentHash,
        string ProfileStamp,
        Guid GenerationId,
        IReadOnlyList<IndexChunkDraft>? Drafts,
        IReadOnlyList<ReadOnlyMemory<float>>? Vectors,
        long? EmbeddingTokenCount)
    {
        public long? LlmInputTokenCount { get; init; }

        public long? LlmOutputTokenCount { get; init; }

        /// <summary>Something the run should say although it succeeded, such as chunks the enricher could not enrich.</summary>
        public string? Note { get; init; }
    }
}

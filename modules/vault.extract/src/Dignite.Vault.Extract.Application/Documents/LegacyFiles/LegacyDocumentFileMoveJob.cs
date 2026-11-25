using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.LegacyFiles;

/// <summary>The job name is a serialized string: renaming it orphans queued jobs.</summary>
[BackgroundJobName("VaultExtract.LegacyDocumentFileMove")]
public class LegacyDocumentFileMoveJobArgs
{
    public Guid? TenantId { get; set; }

    /// <summary>Keyset cursor: only documents with <c>Id &gt; AfterId</c>; <c>null</c> for the first page.</summary>
    public Guid? AfterId { get; set; }
}

/// <summary>
/// Moves a layer's document files from Extract's own container to Blob Management, a page at a time
/// (<see cref="LegacyDocumentFileMover"/>), then enqueues itself for the next page. A document whose file cannot be moved
/// is logged with the reason and left as it is; the cursor passes it, and the next data seed starts a new walk that
/// tries it again. Low priority, so it never holds up the pipeline jobs.
/// </summary>
public class LegacyDocumentFileMoveJob : AsyncBackgroundJob<LegacyDocumentFileMoveJobArgs>, ITransientDependency
{
    /// <summary>Documents per page. Each one's file is read into memory on its own, so this bounds time, not memory.</summary>
    public const int PageSize = 50;

    private readonly IDocumentRepository _documentRepository;
    private readonly LegacyDocumentFileMover _mover;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;

    public LegacyDocumentFileMoveJob(
        IDocumentRepository documentRepository,
        LegacyDocumentFileMover mover,
        IBackgroundJobManager backgroundJobManager,
        ICurrentTenant currentTenant)
    {
        _documentRepository = documentRepository;
        _mover = mover;
        _backgroundJobManager = backgroundJobManager;
        _currentTenant = currentTenant;
    }

    public override async Task ExecuteAsync(LegacyDocumentFileMoveJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            var ids = await _documentRepository.GetIdsWithLegacyFileAsync(args.AfterId, PageSize);

            int moved = 0, left = 0;
            foreach (var documentId in ids)
            {
                LegacyDocumentFileMoveResult result;
                try
                {
                    result = await _mover.MoveAsync(documentId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "The file of document {DocumentId} could not be moved to Blob Management; it stays in extract-documents.",
                        documentId);
                    left++;
                    continue;
                }

                switch (result.Outcome)
                {
                    case LegacyDocumentFileMoveOutcome.Moved:
                        moved++;
                        break;
                    case LegacyDocumentFileMoveOutcome.NotLegacy:
                        break;
                    default:
                        Logger.LogWarning(
                            "The file of document {DocumentId} was not moved to Blob Management ({Outcome} {Reason}); it stays in extract-documents.",
                            documentId, result.Outcome, result.Reason);
                        left++;
                        break;
                }
            }

            if (ids.Count == PageSize)
            {
                await _backgroundJobManager.EnqueueAsync(
                    new LegacyDocumentFileMoveJobArgs { TenantId = args.TenantId, AfterId = ids[^1] },
                    BackgroundJobPriority.Low);
            }

            Logger.LogInformation(
                "Document files moved to Blob Management in tenant {TenantId}: {Moved} moved, {Left} left in extract-documents (afterId={AfterId}, continued={Continued}).",
                args.TenantId, moved, left, args.AfterId, ids.Count == PageSize);
        }
    }
}

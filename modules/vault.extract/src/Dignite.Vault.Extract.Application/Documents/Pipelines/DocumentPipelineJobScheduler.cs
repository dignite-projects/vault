using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Creates the observable PipelineRun row before enqueueing the matching background job.
/// A queued job must always carry the PipelineRunId it is expected to execute.
/// </summary>
public class DocumentPipelineJobScheduler : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public DocumentPipelineJobScheduler(
        IDocumentRepository documentRepository,
        DocumentPipelineRunManager pipelineRunManager,
        IBackgroundJobManager backgroundJobManager)
    {
        _documentRepository = documentRepository;
        _pipelineRunManager = pipelineRunManager;
        _backgroundJobManager = backgroundJobManager;
    }

    /// <summary>
    /// Queues a pipeline run for the document. Optional <paramref name="delay"/> controls
    /// how long the background job waits before being picked up.
    /// <para>
    /// <paramref name="expectedEventTypeCode"/> (#527 §8) is the field-extraction cascade's stale-reclassify
    /// early-exit hint: when the classification stage schedules the cascade field-extraction run transactionally, it
    /// forwards the just-assigned <c>DocumentType.TypeCode</c> so a queued run whose type has since changed can
    /// short-circuit in <see cref="FieldExtractionService"/> phase 1. It is only meaningful for
    /// <see cref="VaultExtractPipelines.FieldExtraction"/>; other pipeline codes ignore it.
    /// </para>
    /// <para>
    /// <paramref name="isReparse"/> (#660) marks a <see cref="VaultExtractPipelines.Parse"/> run as a re-parse of a
    /// document that already has Markdown. The mode is decided here, by the caller that queues the run, and travels in
    /// the job args, so a redelivered first-parse job can never take the replacing path. Other pipeline codes ignore it.
    /// </para>
    /// </summary>
    public virtual async Task<DocumentPipelineRun> QueueAsync(
        Document document,
        string pipelineCode,
        TimeSpan? delay = null,
        string? expectedEventTypeCode = null,
        bool isReparse = false)
    {
        var run = await _pipelineRunManager.QueueAsync(document, pipelineCode);

        await _documentRepository.UpdateAsync(document, autoSave: true);
        await EnqueueAsync(document.Id, document.TenantId, pipelineCode, run.Id, delay, expectedEventTypeCode, isReparse);

        return run;
    }

    protected virtual Task EnqueueAsync(
        Guid documentId,
        Guid? tenantId,
        string pipelineCode,
        Guid pipelineRunId,
        TimeSpan? delay = null,
        string? expectedEventTypeCode = null,
        bool isReparse = false)
    {
        var effectiveDelay = delay ?? default;
        return pipelineCode switch
        {
            VaultExtractPipelines.Parse => _backgroundJobManager.EnqueueAsync(
                new DocumentParseJobArgs
                {
                    DocumentId = documentId,
                    TenantId = tenantId,
                    PipelineRunId = pipelineRunId,
                    IsReparse = isReparse
                },
                delay: effectiveDelay),
            VaultExtractPipelines.Classification => _backgroundJobManager.EnqueueAsync(
                new DocumentClassificationJobArgs
                {
                    DocumentId = documentId,
                    TenantId = tenantId,
                    PipelineRunId = pipelineRunId
                },
                delay: effectiveDelay),
            VaultExtractPipelines.FieldExtraction => _backgroundJobManager.EnqueueAsync(
                new DocumentFieldExtractionJobArgs
                {
                    DocumentId = documentId,
                    TenantId = tenantId,
                    PipelineRunId = pipelineRunId,
                    ExpectedEventTypeCode = expectedEventTypeCode
                },
                delay: effectiveDelay),
            _ => throw new BusinessException(VaultExtractErrorCodes.Pipeline.UnknownCode)
                .WithData("PipelineCode", pipelineCode)
        };
    }
}

public class DocumentPipelineRunAccessor : ITransientDependency
{
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly ILogger<DocumentPipelineRunAccessor> _logger;

    public DocumentPipelineRunAccessor(
        DocumentPipelineRunManager pipelineRunManager,
        IDocumentPipelineRunRepository runRepository,
        ILogger<DocumentPipelineRunAccessor> logger)
    {
        _pipelineRunManager = pipelineRunManager;
        _runRepository = runRepository;
        _logger = logger;
    }

    /// <summary>
    /// Begins the run a pipeline job was queued for, or starts one. Returns <c>null</c> when the job's run is no
    /// longer one to execute (see <see cref="BeginIfCurrentAsync"/>): the job then ends without doing its work.
    /// </summary>
    public virtual async Task<DocumentPipelineRun?> BeginOrStartAsync(
        Document document,
        Guid? pipelineRunId,
        string pipelineCode)
    {
        if (!pipelineRunId.HasValue)
        {
            return await _pipelineRunManager.StartAsync(document, pipelineCode);
        }

        // #216: after PipelineRun became an independent aggregate root, locate it directly by runId
        // through the repository instead of Document.GetRun.
        var run = await _runRepository.FindAsync(pipelineRunId.Value);
        if (run?.PipelineCode == pipelineCode)
        {
            return await BeginIfCurrentAsync(document, run) ? run : null;
        }

        if (run != null)
        {
            _logger.LogWarning(
                "Pipeline run {PipelineRunId} on document {DocumentId} belongs to {ActualPipelineCode}, but the job expects {ExpectedPipelineCode}.",
                pipelineRunId.Value, document.Id, run.PipelineCode, pipelineCode);
        }

        var pendingRun = await _runRepository.FindLatestByDocumentAndCodeAsync(document.Id, pipelineCode);
        if (pendingRun?.Status == PipelineRunStatus.Pending)
        {
            _logger.LogWarning(
                "Pipeline run {PipelineRunId} was not found for {PipelineCode} on document {DocumentId}; using latest pending run {ReplacementRunId}.",
                pipelineRunId.Value, pipelineCode, document.Id, pendingRun.Id);
            await _pipelineRunManager.BeginAsync(document, pendingRun);
            return pendingRun;
        }

        _logger.LogWarning(
            "Pipeline run {PipelineRunId} was not found for {PipelineCode} on document {DocumentId}; creating a replacement run.",
            pipelineRunId.Value, pipelineCode, document.Id);
        var replacementRun = await _pipelineRunManager.StartAsync(
            document,
            pipelineCode,
            pipelineRunId.Value);
        return replacementRun;
    }

    /// <summary>
    /// Begins <paramref name="run"/> when its job still has it to execute, and returns whether it did. A run that has
    /// ended (Succeeded or Skipped) was executed by an earlier delivery of the job. A run that a later attempt of the
    /// same pipeline has superseded is not executed either: ABP retries a failed job on its own schedule, so attempt 1's
    /// retry can come round after an operator's retry queued attempt 2, and with parallel workers the two run at once.
    /// The parse that finishes second then fails on the Markdown the first one wrote and leaves the document Failed.
    /// A superseded run that never ended is marked Skipped; a Failed one keeps its failure.
    /// </summary>
    protected virtual async Task<bool> BeginIfCurrentAsync(Document document, DocumentPipelineRun run)
    {
        if (run.Status is PipelineRunStatus.Succeeded or PipelineRunStatus.Skipped)
        {
            _logger.LogInformation(
                "Pipeline run {PipelineRunId} ({PipelineCode}) on document {DocumentId} has already ended as {Status}; its job ends.",
                run.Id, run.PipelineCode, document.Id, run.Status);
            return false;
        }

        var latest = await _runRepository.FindLatestByDocumentAndCodeAsync(document.Id, run.PipelineCode);
        if (latest != null && latest.AttemptNumber > run.AttemptNumber)
        {
            if (run.Status is PipelineRunStatus.Pending or PipelineRunStatus.Running)
            {
                await _pipelineRunManager.SkipAsync(document, run, $"Superseded by attempt {latest.AttemptNumber}.");
            }

            _logger.LogInformation(
                "Pipeline run {PipelineRunId} ({PipelineCode}, attempt {AttemptNumber}) on document {DocumentId} was superseded by attempt {LatestAttemptNumber}; its job ends.",
                run.Id, run.PipelineCode, run.AttemptNumber, document.Id, latest.AttemptNumber);
            return false;
        }

        await _pipelineRunManager.BeginAsync(document, run);
        return true;
    }
}

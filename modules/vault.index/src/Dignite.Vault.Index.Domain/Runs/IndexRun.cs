using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Runs;

/// <summary>
/// One index job for one document: why it ran, how it ended, what it cost. It also carries the attempt number,
/// because ABP's background job retries until a timeout with no count limit, so the cap on retries is Index's own.
/// </summary>
public class IndexRun : AggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; protected set; }

    public virtual Guid DocumentId { get; protected set; }

    public virtual IndexRunTrigger Trigger { get; protected set; }

    public virtual IndexRunStatus Status { get; protected set; }

    /// <summary>1 for the first attempt.</summary>
    public virtual int AttemptNumber { get; protected set; }

    public virtual DateTime StartedTime { get; protected set; }

    public virtual DateTime? FinishedTime { get; protected set; }

    public virtual string? ErrorMessage { get; protected set; }

    public virtual int? ChunkCount { get; protected set; }

    public virtual long? EmbeddingTokenCount { get; protected set; }

    public virtual long? LlmInputTokenCount { get; protected set; }

    public virtual long? LlmOutputTokenCount { get; protected set; }

    protected IndexRun()
    {
    }

    public IndexRun(
        Guid id,
        Guid? tenantId,
        Guid documentId,
        IndexRunTrigger trigger,
        int attemptNumber,
        DateTime startedTime)
        : base(id)
    {
        TenantId = tenantId;
        DocumentId = documentId;
        Trigger = trigger;
        AttemptNumber = Check.Range(attemptNumber, nameof(attemptNumber), 1, int.MaxValue);
        StartedTime = startedTime;
        Status = IndexRunStatus.Running;
    }
}

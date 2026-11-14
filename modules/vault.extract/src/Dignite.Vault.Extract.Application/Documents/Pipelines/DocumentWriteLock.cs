using System;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.DistributedLocking;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Serializes the background jobs' write units of work on one document. Background jobs run in parallel, and several
/// jobs of one document are queued together: text extraction queues classification and the cabinet suggestion,
/// classification queues field extraction and segmentation. Each of them saves the document, so two that load it at
/// the same time fail the later save on its concurrency stamp, and the job then fails its run (or, for the cabinet
/// suggestion, drops the suggestion) after its model call was paid for.
/// <para>
/// Hold it around a write unit of work only, never across a model call or OCR, so a job never waits for another job's
/// external work; and take it before the unit of work begins, so no transaction waits for it. Never take it twice in
/// one call chain: it is not reentrant. Operator writes over HTTP do not take it and still meet the concurrency stamp.
/// </para>
/// </summary>
public class DocumentWriteLock : ITransientDependency
{
    /// <summary>How long a write waits for another job's write on the same document before its job fails and retries.</summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    protected IAbpDistributedLock DistributedLock { get; }

    public DocumentWriteLock(IAbpDistributedLock distributedLock)
    {
        DistributedLock = distributedLock;
    }

    public static string GetName(Guid documentId) => $"VaultExtract:Document:{documentId}";

    public virtual async Task<IAsyncDisposable> AcquireAsync(Guid documentId)
    {
        return await DistributedLock.TryAcquireAsync(GetName(documentId), WaitTimeout)
            ?? throw new AbpException(
                $"Document {documentId} has been written by another background job for {WaitTimeout.TotalSeconds:0} seconds; the job will be retried.");
    }
}

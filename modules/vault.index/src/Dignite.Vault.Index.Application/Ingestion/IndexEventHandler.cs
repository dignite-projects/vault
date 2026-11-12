using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Index.Indexing;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Turns Extract's integration events into index jobs, and does nothing else: the handler runs in the publisher's
/// event pipeline, so it only enqueues, and every read and write happens in <see cref="IndexDocumentJob"/>, which
/// also drops redelivered and stale events by their <c>EventTime</c>.
/// <list type="table">
/// <item><term>Ready, Restored</term><description>sync from Extract (Restored also un-archives first)</description></item>
/// <item><term>Deleted</term><description>archive: out of retrieval, kept</description></item>
/// <item><term>Classified</term><description>withhold until the next Ready, so the old type's readers lose it now</description></item>
/// <item><term>PermanentlyDeleted, ReclassifiedToContainer</term><description>remove everything derived</description></item>
/// </list>
/// </summary>
public class IndexEventHandler :
    IDistributedEventHandler<DocumentReadyEto>,
    IDistributedEventHandler<DocumentRestoredEto>,
    IDistributedEventHandler<DocumentDeletedEto>,
    IDistributedEventHandler<DocumentClassifiedEto>,
    IDistributedEventHandler<DocumentPermanentlyDeletedEto>,
    IDistributedEventHandler<DocumentReclassifiedToContainerEto>,
    ITransientDependency
{
    private readonly IBackgroundJobManager _backgroundJobManager;

    public IndexEventHandler(IBackgroundJobManager backgroundJobManager)
    {
        _backgroundJobManager = backgroundJobManager;
    }

    public virtual Task HandleEventAsync(DocumentReadyEto eventData)
        => EnqueueAsync(eventData.DocumentId, eventData.TenantId, eventData.EventTime, IndexDocumentJobKind.Sync);

    public virtual Task HandleEventAsync(DocumentRestoredEto eventData)
        => EnqueueAsync(eventData.DocumentId, eventData.TenantId, eventData.EventTime, IndexDocumentJobKind.Unarchive);

    public virtual Task HandleEventAsync(DocumentDeletedEto eventData)
        => EnqueueAsync(eventData.DocumentId, eventData.TenantId, eventData.EventTime, IndexDocumentJobKind.Archive);

    public virtual Task HandleEventAsync(DocumentClassifiedEto eventData)
        => EnqueueAsync(eventData.DocumentId, eventData.TenantId, eventData.EventTime, IndexDocumentJobKind.Withhold);

    public virtual Task HandleEventAsync(DocumentPermanentlyDeletedEto eventData)
        => EnqueueAsync(eventData.DocumentId, eventData.TenantId, eventData.EventTime, IndexDocumentJobKind.Remove);

    public virtual Task HandleEventAsync(DocumentReclassifiedToContainerEto eventData)
        => EnqueueAsync(eventData.DocumentId, eventData.TenantId, eventData.EventTime, IndexDocumentJobKind.Remove);

    protected virtual Task EnqueueAsync(Guid documentId, Guid? tenantId, DateTime eventTime, IndexDocumentJobKind kind)
    {
        return _backgroundJobManager.EnqueueAsync(new IndexDocumentJobArgs
        {
            DocumentId = documentId,
            TenantId = tenantId,
            Kind = kind,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = eventTime
        });
    }
}

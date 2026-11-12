using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp.BackgroundJobs;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>What an index job does to one document.</summary>
public enum IndexDocumentJobKind
{
    /// <summary>Pull the document from Extract and bring its chunks and vectors up to date.</summary>
    Sync = 0,

    /// <summary>It went to the recycle bin: exclude it from retrieval, keep its data.</summary>
    Archive = 1,

    /// <summary>It came back from the recycle bin; followed by a sync.</summary>
    Unarchive = 2,

    /// <summary>It was reclassified: exclude it from retrieval until its next Ready.</summary>
    Withhold = 3,

    /// <summary>It was permanently deleted, or became a container: delete everything derived from it.</summary>
    Remove = 4
}

/// <summary>The job name is a serialized string: renaming it orphans queued jobs.</summary>
[BackgroundJobName("VaultIndex.IndexDocument")]
public class IndexDocumentJobArgs
{
    public Guid DocumentId { get; set; }

    public Guid? TenantId { get; set; }

    public IndexDocumentJobKind Kind { get; set; }

    public IndexRunTrigger Trigger { get; set; }

    /// <summary>The Extract event's <c>EventTime</c>; <c>null</c> for a backfill or a rerun, which skip the redelivery check.</summary>
    public DateTime? EventTime { get; set; }

    /// <summary>
    /// Re-chunk and re-embed even when the text, the chunking settings and the embedding generation are unchanged: an
    /// operator's rebuild. A rerun the job requests for itself does not carry it.
    /// </summary>
    public bool Force { get; set; }
}

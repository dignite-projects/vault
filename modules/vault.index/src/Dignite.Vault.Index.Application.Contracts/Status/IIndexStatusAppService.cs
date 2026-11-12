using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Dignite.Vault.Index.Indexing;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Index.Status;

/// <summary>
/// The index status page: what is in the index, what failed and why, and a rebuild for one document. Needs
/// <c>VaultIndex.Status</c>; the document list and runs are limited to the caller's Extract read scope, because they show
/// titles. Rebuilding needs <c>VaultIndex.Status.Rebuild</c>.
/// </summary>
public interface IIndexStatusAppService : IApplicationService
{
    /// <summary>Counts for the tenant, the serving embedding generation, and the state of the keyword index.</summary>
    Task<IndexStatusSummaryDto> GetSummaryAsync();

    /// <summary>The indexed documents within the caller's read scope, newest change first.</summary>
    Task<PagedResultDto<IndexedDocumentStatusDto>> GetDocumentListAsync(GetIndexedDocumentListInput input);

    /// <summary>A document's most recent runs, newest first.</summary>
    Task<ListResultDto<IndexRunDto>> GetRunsAsync(Guid documentId);

    /// <summary>Queues the document to be indexed again from its current Extract text, even if nothing changed.</summary>
    Task RebuildAsync(Guid documentId);
}

public class IndexStatusSummaryDto
{
    public int PendingCount { get; set; }

    public int IndexingCount { get; set; }

    public int IndexedCount { get; set; }

    public int FailedCount { get; set; }

    /// <summary>Indexed but out of retrieval: in Extract's recycle bin.</summary>
    public int ArchivedCount { get; set; }

    /// <summary>Indexed but out of retrieval until their next release, after a reclassification.</summary>
    public int WithheldCount { get; set; }

    public long ChunkCount { get; set; }

    /// <summary>Vectors of the serving generation in this tenant.</summary>
    public long VectorCount { get; set; }

    /// <summary>
    /// Vector search is exact, and the storage PoC sized it for about this many chunks after the read-scope filter;
    /// past it, a search that reaches the whole tenant slows down.
    /// </summary>
    public long ExactSearchChunkLimit { get; set; }

    /// <summary>The model the serving vectors were built with; <c>null</c> before the first document is indexed.</summary>
    public string? ServingModelId { get; set; }

    public int? ServingDimensions { get; set; }

    public DateTime? ServingSince { get; set; }

    /// <summary>The host's embedding model; <c>null</c> when none is configured and ingestion is off.</summary>
    public string? ConfiguredModelId { get; set; }

    public bool KeywordIndexAvailable { get; set; }

    /// <summary>Changes the keyword index has not absorbed yet, across every tenant of the database.</summary>
    public long? KeywordIndexPendingChanges { get; set; }

    public bool? KeywordIndexPopulating { get; set; }
}

public class GetIndexedDocumentListInput : PagedAndSortedResultRequestDto
{
    public IndexedDocumentStatus? Status { get; set; }

    /// <summary>Part of the title.</summary>
    [StringLength(256)]
    public string? Filter { get; set; }
}

public class IndexedDocumentStatusDto
{
    public Guid DocumentId { get; set; }

    public string? Title { get; set; }

    public Guid DocumentTypeId { get; set; }

    public string? DocumentTypeCode { get; set; }

    public IndexedDocumentStatus Status { get; set; }

    public bool IsArchived { get; set; }

    public bool IsWithheld { get; set; }

    public int ChunkCount { get; set; }

    public int FailedAttempts { get; set; }

    public DateTime? LastIndexedTime { get; set; }

    public DateTime? LastModificationTime { get; set; }

    /// <summary>The error of the latest run, when it failed.</summary>
    public string? LastError { get; set; }
}

public class IndexRunDto
{
    public Guid Id { get; set; }

    public IndexRunTrigger Trigger { get; set; }

    public IndexRunStatus Status { get; set; }

    public int AttemptNumber { get; set; }

    public DateTime StartedTime { get; set; }

    public DateTime? FinishedTime { get; set; }

    /// <summary>Why it failed, or why it was skipped.</summary>
    public string? ErrorMessage { get; set; }

    public int? ChunkCount { get; set; }

    public long? EmbeddingTokenCount { get; set; }
}

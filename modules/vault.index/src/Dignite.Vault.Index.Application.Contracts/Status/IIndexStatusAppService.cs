using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Dignite.Vault.Index.Indexing;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Index.Status;

/// <summary>
/// The index status page: what is in the index, what failed and why, where each document's summaries stand, and a
/// rebuild or a recompile for one document. Needs <c>VaultIndex.Status</c>; the document list and runs are limited to
/// the caller's Extract read scope, because they show titles. Rebuilding and recompiling need
/// <c>VaultIndex.Status.Rebuild</c>.
/// </summary>
public interface IIndexStatusAppService : IApplicationService
{
    /// <summary>
    /// Counts for the tenant, the serving embedding generation and the one being built for a new model, and the state
    /// of the keyword index.
    /// </summary>
    Task<IndexStatusSummaryDto> GetSummaryAsync();

    /// <summary>The indexed documents within the caller's read scope, newest change first.</summary>
    Task<PagedResultDto<IndexedDocumentStatusDto>> GetDocumentListAsync(GetIndexedDocumentListInput input);

    /// <summary>A document's most recent runs, newest first.</summary>
    Task<ListResultDto<IndexRunDto>> GetRunsAsync(Guid documentId);

    /// <summary>Queues the document to be indexed again from its current Extract text, even if nothing changed.</summary>
    Task RebuildAsync(Guid documentId);

    /// <summary>
    /// Queues the document's summaries to be compiled again from its chunks, even if they are current. Refused when
    /// the host has no chat model or the document's type does not compile summaries.
    /// </summary>
    Task RecompileAsync(Guid documentId);
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

    /// <summary>
    /// The host can embed queries with the serving model: it is the configured one, or registered as the previous one
    /// while a new generation is built. When not, search is refused until the new generation serves.
    /// </summary>
    public bool ServingModelAvailable { get; set; }

    /// <summary>The model a new generation is being built for, while the serving one keeps answering; <c>null</c> when none is.</summary>
    public string? BuildingModelId { get; set; }

    public int? BuildingDimensions { get; set; }

    /// <summary>When the building generation was started.</summary>
    public DateTime? BuildingSince { get; set; }

    /// <summary>Documents with chunks that already have vectors in the building generation.</summary>
    public int BuildingDocumentCount { get; set; }

    /// <summary>Documents with chunks: the building generation takes over once it has vectors for all of them.</summary>
    public int BuildingTotalCount { get; set; }

    /// <summary>
    /// After the configured model changed: documents not in the building generation whose last attempt to embed them
    /// with the new model failed, also before any success has created that generation. Their runs say why; a transient
    /// failure is retried.
    /// </summary>
    public int BuildingFailedCount { get; set; }

    public bool KeywordIndexAvailable { get; set; }

    /// <summary>Changes the keyword index has not absorbed yet, across every tenant of the database.</summary>
    public long? KeywordIndexPendingChanges { get; set; }

    public bool? KeywordIndexPopulating { get; set; }

    /// <summary>The host has Index's chat model, which compiles summaries.</summary>
    public bool ChatModelAvailable { get; set; }

    /// <summary>Documents with compiled summaries, current or not.</summary>
    public int SummaryDocumentCount { get; set; }

    /// <summary>Of those, the ones whose last compilation left something out (see <see cref="IndexedDocumentStatusDto.SummariesNote"/>).</summary>
    public int SummaryNoteCount { get; set; }
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

    /// <summary>Where the document's section and document summaries stand.</summary>
    public IndexArtifactState SummariesState { get; set; }

    /// <summary>When the summaries were last compiled.</summary>
    public DateTime? SummariesCompiledTime { get; set; }

    public long? SummariesInputTokenCount { get; set; }

    public long? SummariesOutputTokenCount { get; set; }

    /// <summary>What the last compilation left out or could not do, such as sections the model did not summarize.</summary>
    public string? SummariesNote { get; set; }
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

    /// <summary>What chunk enrichment sent to the model, when it ran.</summary>
    public long? LlmInputTokenCount { get; set; }

    public long? LlmOutputTokenCount { get; set; }
}

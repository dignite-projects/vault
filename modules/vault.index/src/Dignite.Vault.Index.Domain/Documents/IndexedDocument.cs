using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Documents;

/// <summary>
/// One Extract document as Index knows it. Its id is the Extract document id, so there is one row per document and
/// an event finds it without a lookup. Everything here is derived and can be rebuilt from Extract.
/// <para>
/// The type and owner are the two facts Extract's read rule reads about a document; they are copied here and onto
/// the document's chunks so retrieval can filter by the caller's read scope without asking Extract per row.
/// </para>
/// <para>
/// <b>One index job per document at a time.</b> A job claims the row (<see cref="BeginIndexing"/>); an event that
/// arrives while the claim is live only sets <see cref="RerunRequested"/>, and the job runs again when it ends. A
/// claim older than the caller's stale limit is treated as abandoned (a crashed process), so a document cannot be
/// stuck forever.
/// </para>
/// </summary>
public class IndexedDocument : AggregateRoot<Guid>, IMultiTenant, IHasCreationTime, IHasModificationTime
{
    public virtual Guid? TenantId { get; protected set; }

    /// <summary>The confirmed type at the last Ready version Index pulled.</summary>
    public virtual Guid DocumentTypeId { get; protected set; }

    /// <summary>The uploader, the owner arm of Extract's read rule; <c>null</c> for a row created with no user.</summary>
    public virtual Guid? OwnerId { get; protected set; }

    /// <summary>
    /// The document's title at the last Ready version Index pulled, cut to <see cref="IndexingConsts.MaxTitleLength"/>,
    /// so a search hit can name its source without asking Extract per hit.
    /// </summary>
    public virtual string? Title { get; protected set; }

    public virtual IndexedDocumentStatus Status { get; protected set; }

    /// <summary>The document is in Extract's recycle bin: kept, but excluded from retrieval until restored.</summary>
    public virtual bool IsArchived { get; protected set; }

    /// <summary>
    /// Withheld from retrieval because Extract reclassified it and its next Ready has not arrived yet; until then
    /// Index only knows the old type, and the old type's grant holders must not keep reading it.
    /// </summary>
    public virtual bool IsWithheld { get; protected set; }

    /// <summary>Out of retrieval for everyone; copied onto the document's chunks and vectors.</summary>
    public virtual bool IsHidden => IsArchived || IsWithheld;

    /// <summary>An event arrived while a job was running for this document, so the job runs again when it ends.</summary>
    public virtual bool RerunRequested { get; protected set; }

    /// <summary>The newest Extract <c>EventTime</c> applied; an older or equal one is a redelivery and is dropped.</summary>
    public virtual DateTime? LastEventTime { get; protected set; }

    /// <summary>SHA-256 of the Markdown the current chunks were built from.</summary>
    public virtual string? ContentHash { get; protected set; }

    /// <summary>
    /// The effective chunking settings the current chunks were built with (<c>ResolvedIndexProfile.Stamp</c>). Not a
    /// profile id or version: switching a type from its own profile to the layer default changes nothing if the
    /// settings are the same, and must not re-chunk.
    /// </summary>
    public virtual string? ProfileStamp { get; protected set; }

    /// <summary>The embedding generation the current vectors belong to.</summary>
    public virtual Guid? GenerationId { get; protected set; }

    public virtual int ChunkCount { get; protected set; }

    public virtual DateTime? LastIndexedTime { get; protected set; }

    /// <summary>When the live claim was taken; <c>null</c> when no job holds the document.</summary>
    public virtual DateTime? IndexingStartedTime { get; protected set; }

    /// <summary>
    /// Transient failures since the last success. ABP retries a failed job with no count limit, so this is where the
    /// cap is counted; the job ends the document in <see cref="IndexedDocumentStatus.Failed"/> when it is reached.
    /// </summary>
    public virtual int FailedAttempts { get; protected set; }

    public virtual DateTime CreationTime { get; protected set; }

    public virtual DateTime? LastModificationTime { get; set; }

    protected IndexedDocument()
    {
    }

    public IndexedDocument(Guid documentId, Guid? tenantId, Guid documentTypeId, Guid? ownerId)
        : base(documentId)
    {
        TenantId = tenantId;
        DocumentTypeId = documentTypeId;
        OwnerId = ownerId;
        Status = IndexedDocumentStatus.Pending;
    }

    /// <summary>Whether an event stamped <paramref name="eventTime"/> is newer than everything applied so far.</summary>
    public virtual bool IsNewerThanApplied(DateTime eventTime) => LastEventTime is null || eventTime > LastEventTime;

    /// <summary>Records an applied event; an older one never moves the mark back.</summary>
    public virtual void RecordEvent(DateTime? eventTime)
    {
        if (eventTime is { } time && IsNewerThanApplied(time))
        {
            LastEventTime = time;
        }
    }

    /// <summary>Whether a job holds a claim on this document that is younger than <paramref name="staleAfter"/>.</summary>
    public virtual bool IsClaimed(DateTime now, TimeSpan staleAfter)
        => Status == IndexedDocumentStatus.Indexing && IndexingStartedTime is { } started && now - started < staleAfter;

    public virtual void BeginIndexing(DateTime now)
    {
        if (Status == IndexedDocumentStatus.Failed)
        {
            // A new attempt after a terminal failure starts a new retry budget.
            FailedAttempts = 0;
        }

        Status = IndexedDocumentStatus.Indexing;
        IndexingStartedTime = now;
        RerunRequested = false;
    }

    public virtual void RequestRerun(DateTime? eventTime)
    {
        RerunRequested = true;
        RecordEvent(eventTime);
    }

    /// <summary>Hands the rerun request to the caller and clears it, so it is acted on once.</summary>
    public virtual bool TakeRerunRequest()
    {
        var requested = RerunRequested;
        RerunRequested = false;
        return requested;
    }

    /// <summary>
    /// The job finished: the chunks and vectors now match this content, settings and generation. Clears the
    /// recycle-bin and withheld marks only when <paramref name="clearLifecycleMarks"/> is set, which the job does
    /// when no lifecycle event was applied while it ran.
    /// </summary>
    public virtual void CompleteIndexing(
        string contentHash,
        string profileStamp,
        Guid generationId,
        int chunkCount,
        Guid documentTypeId,
        Guid? ownerId,
        string? title,
        DateTime now,
        bool clearLifecycleMarks)
    {
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash));
        ProfileStamp = Check.NotNullOrWhiteSpace(profileStamp, nameof(profileStamp));
        GenerationId = generationId;
        ChunkCount = Check.Range(chunkCount, nameof(chunkCount), 0, int.MaxValue);
        DocumentTypeId = documentTypeId;
        OwnerId = ownerId;
        Title = CutTitle(title);
        LastIndexedTime = now;
        Status = IndexedDocumentStatus.Indexed;
        IndexingStartedTime = null;
        FailedAttempts = 0;

        if (clearLifecycleMarks)
        {
            IsArchived = false;
            IsWithheld = false;
        }
    }

    /// <summary>
    /// The job did nothing (the document was no longer Ready when pulled). Releases the claim; the chunks of the
    /// last Ready version, if any, keep serving.
    /// </summary>
    public virtual void ReleaseClaim()
    {
        Status = LastIndexedTime is null ? IndexedDocumentStatus.Pending : IndexedDocumentStatus.Indexed;
        IndexingStartedTime = null;
    }

    /// <summary>
    /// The job failed. A terminal failure ends in <see cref="IndexedDocumentStatus.Failed"/>; a transient one counts
    /// an attempt and leaves the document as it was before the claim, so the last indexed version keeps serving.
    /// </summary>
    public virtual void FailIndexing(bool terminal)
    {
        IndexingStartedTime = null;
        if (terminal)
        {
            Status = IndexedDocumentStatus.Failed;
            return;
        }

        FailedAttempts++;
        Status = LastIndexedTime is null ? IndexedDocumentStatus.Pending : IndexedDocumentStatus.Indexed;
    }

    public virtual void Archive(DateTime? eventTime)
    {
        IsArchived = true;
        RecordEvent(eventTime);
    }

    public virtual void Unarchive(DateTime? eventTime)
    {
        IsArchived = false;
        RecordEvent(eventTime);
    }

    public virtual void Withhold(DateTime? eventTime)
    {
        IsWithheld = true;
        RecordEvent(eventTime);
    }

    private static string? CutTitle(string? title)
    {
        if (title is null || title.Length <= IndexingConsts.MaxTitleLength)
        {
            return title;
        }

        // Never split a surrogate pair.
        var length = char.IsHighSurrogate(title[IndexingConsts.MaxTitleLength - 1])
            ? IndexingConsts.MaxTitleLength - 1
            : IndexingConsts.MaxTitleLength;
        return title[..length];
    }
}

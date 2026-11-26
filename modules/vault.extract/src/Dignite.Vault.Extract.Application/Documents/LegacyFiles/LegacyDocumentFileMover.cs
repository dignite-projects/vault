using System;
using System.IO;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Threading;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.LegacyFiles;

/// <summary>
/// Moves one document's file from Extract's own container (<see cref="FileOrigin.BlobName"/>) to Blob Management
/// (<see cref="FileOrigin.FileId"/>), in the phases a background job's slow work runs in:
/// <list type="number">
/// <item>Read the document's file name and hash.</item>
/// <item>Read the bytes, check them against <see cref="FileOrigin.ContentHash"/>, and store them as a new file, which
/// Blob Management verifies against the same hash.</item>
/// <item>Under <see cref="DocumentWriteLock"/>, in one transaction: if the document still names the same blob, point it
/// at the new file - in the recycle bin with it when the document is - and save.</item>
/// <item>Once that has committed, delete the old blob.</item>
/// </list>
/// Nothing is lost on the way: the old blob goes only after the document names a verified copy. A move that cannot finish
/// (the blob is missing, its bytes changed, Blob Management refuses them, the document changed meanwhile) leaves the
/// document as it was, and removes the copy it made. Running it again on a moved document does nothing.
/// </summary>
public class LegacyDocumentFileMover : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _legacyContainer;
    private readonly DocumentFileStore _documentFiles;
    private readonly DocumentWriteLock _documentWriteLock;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDataFilter _dataFilter;
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public ILogger<LegacyDocumentFileMover> Logger { get; set; } = NullLogger<LegacyDocumentFileMover>.Instance;

    public LegacyDocumentFileMover(
        IDocumentRepository documentRepository,
        IBlobContainer<VaultExtractDocumentContainer> legacyContainer,
        DocumentFileStore documentFiles,
        DocumentWriteLock documentWriteLock,
        IUnitOfWorkManager unitOfWorkManager,
        IDataFilter dataFilter,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        _documentRepository = documentRepository;
        _legacyContainer = legacyContainer;
        _documentFiles = documentFiles;
        _documentWriteLock = documentWriteLock;
        _unitOfWorkManager = unitOfWorkManager;
        _dataFilter = dataFilter;
        _cancellationTokenProvider = cancellationTokenProvider;
    }

    /// <summary>Moves the file of <paramref name="documentId"/>, in the current tenant, and says how it went.</summary>
    public virtual async Task<LegacyDocumentFileMoveResult> MoveAsync(Guid documentId)
    {
        var cancellationToken = _cancellationTokenProvider.Token;

        var (origin, creatorId) = await LoadAsync(documentId);
        if (origin?.BlobName is not { } blobName)
        {
            return LegacyDocumentFileMoveResult.NotLegacy;
        }

        byte[] bytes;
        await using (var stream = await _legacyContainer.GetOrNullAsync(blobName, cancellationToken))
        {
            if (stream is null)
            {
                return LegacyDocumentFileMoveResult.Left(LegacyDocumentFileMoveOutcome.FileMissing);
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        if (!string.Equals(ContentHasher.Sha256Hex(bytes), origin.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return LegacyDocumentFileMoveResult.Left(LegacyDocumentFileMoveOutcome.ContentChanged);
        }

        Guid fileId;
        try
        {
            fileId = await _documentFiles.CreateAsync(
                origin.OriginalFileName ?? blobName, bytes, origin.ContentHash, creatorId);
        }
        catch (BusinessException ex)
        {
            return LegacyDocumentFileMoveResult.Left(LegacyDocumentFileMoveOutcome.Refused, ex.Code);
        }
        catch (AbpAuthorizationException ex)
        {
            // Blob Management is switched off for the tenant (BlobManagement.Enable).
            return LegacyDocumentFileMoveResult.Left(LegacyDocumentFileMoveOutcome.Refused, ex.Message);
        }

        var moved = origin.KeptAs(fileId);
        bool attached;
        try
        {
            attached = await AttachAsync(documentId, blobName, fileId);
        }
        catch
        {
            await DiscardAsync(moved);
            throw;
        }

        if (!attached)
        {
            await DiscardAsync(moved);
            return LegacyDocumentFileMoveResult.NotLegacy;
        }

        try
        {
            await _legacyContainer.DeleteAsync(blobName, cancellationToken);
        }
        catch (Exception ex)
        {
            // The document names its new file already; the old bytes are only left behind.
            Logger.LogWarning(ex, "Document {DocumentId} moved its file, but the old blob {BlobName} could not be deleted.",
                documentId, blobName);
        }

        return LegacyDocumentFileMoveResult.Moved;
    }

    /// <summary>
    /// The document's file as it is now, in the recycle bin or not (<c>null</c> when there is none), and its owner, whom
    /// the file counts for in Blob Management's usage figures.
    /// </summary>
    protected virtual async Task<(FileOrigin? Origin, Guid? CreatorId)> LoadAsync(Guid documentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: false);
        Document? document;
        using (_dataFilter.Disable<ISoftDelete>())
        {
            document = await _documentRepository.FindAsync(d => d.Id == documentId, includeDetails: false);
        }

        await uow.CompleteAsync();
        return (document?.FileOrigin, document?.CreatorId);
    }

    /// <summary>
    /// Points the document at <paramref name="fileId"/> if it still names <paramref name="blobName"/>, and sends the
    /// file to the recycle bin when the document is there. <c>false</c> when the document changed meanwhile: deleted for
    /// good, or moved by another run.
    /// </summary>
    protected virtual async Task<bool> AttachAsync(Guid documentId, string blobName, Guid fileId)
    {
        await using var documentLock = await _documentWriteLock.AcquireAsync(documentId);
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);

        Document? document;
        using (_dataFilter.Disable<ISoftDelete>())
        {
            document = await _documentRepository.FindAsync(d => d.Id == documentId, includeDetails: false);
        }

        if (document?.FileOrigin?.BlobName != blobName)
        {
            await uow.CompleteAsync();
            return false;
        }

        document.MoveFileTo(fileId);
        if (document.IsDeleted)
        {
            await _documentFiles.DeleteAsync(document.FileOrigin!);
        }

        await _documentRepository.UpdateAsync(document);
        await uow.CompleteAsync();
        return true;
    }

    /// <summary>Removes a copy the document did not take.</summary>
    protected virtual async Task DiscardAsync(FileOrigin copy)
    {
        try
        {
            await _documentFiles.DeletePermanentlyAsync(copy);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The unused copy {FileId} could not be deleted.", copy.FileId);
        }
    }
}

/// <summary>How moving a document's file went.</summary>
public enum LegacyDocumentFileMoveOutcome
{
    /// <summary>The document names its file in Blob Management now; the old blob is gone.</summary>
    Moved,

    /// <summary>Nothing to move: the file was moved before, or the document has none or is gone.</summary>
    NotLegacy,

    /// <summary>The blob is not in Extract's container. The document is left as it is.</summary>
    FileMissing,

    /// <summary>The blob's bytes are not the ones the document recorded. The document is left as it is.</summary>
    ContentChanged,

    /// <summary>Blob Management refused the bytes (<see cref="LegacyDocumentFileMoveResult.Reason"/>). The document is left as it is.</summary>
    Refused
}

public sealed record LegacyDocumentFileMoveResult(LegacyDocumentFileMoveOutcome Outcome, string? Reason = null)
{
    public static LegacyDocumentFileMoveResult Moved { get; } = new(LegacyDocumentFileMoveOutcome.Moved);

    public static LegacyDocumentFileMoveResult NotLegacy { get; } = new(LegacyDocumentFileMoveOutcome.NotLegacy);

    public static LegacyDocumentFileMoveResult Left(LegacyDocumentFileMoveOutcome outcome, string? reason = null) => new(outcome, reason);
}

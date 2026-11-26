using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.BlobManagement;
using Dignite.Abp.BlobManagement.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// A document's own file (<see cref="FileOrigin"/>) through its lifecycle: stored, read, sent to the recycle bin with
/// its document, restored with it, deleted for good with it.
/// <para>
/// A file is kept by Blob Management, by id (<see cref="FileOrigin.FileId"/>), in the managed container
/// <see cref="DocumentFileConsts.ContainerName"/>: counted in the tenant's storage, soft-deleted and restored with the
/// document, its bytes removed only when the document is deleted for good. A file stored before that move is still
/// named by <see cref="FileOrigin.BlobName"/> in Extract's own container, where it is read from and deleted for good;
/// it has no recycle-bin state of its own, as before.
/// </para>
/// <para>
/// Every call joins the caller's unit of work: the file's row changes with the document's. A file stored in a unit of
/// work that rolls back is removed with it (Blob Management deletes the bytes of a write that does not last).
/// </para>
/// </summary>
public class DocumentFileStore : ITransientDependency
{
    private readonly IBlobContainer<VaultExtractDocumentContainer> _legacyContainer;
    private readonly IAbpLazyServiceProvider _lazyServiceProvider;

    public ILogger<DocumentFileStore> Logger { get; set; } = NullLogger<DocumentFileStore>.Instance;

    public DocumentFileStore(
        IBlobContainer<VaultExtractDocumentContainer> legacyContainer,
        IAbpLazyServiceProvider lazyServiceProvider)
    {
        _legacyContainer = legacyContainer;
        _lazyServiceProvider = lazyServiceProvider;
    }

    /// <summary>Blob Management's file service, which the host installs.</summary>
    protected IBlobManagementFileService FileService => _lazyServiceProvider.LazyGetRequiredService<IBlobManagementFileService>();

    /// <summary>
    /// Stores <paramref name="bytes"/> as a new file named <paramref name="fileName"/> and returns its id. Blob Management
    /// verifies them against <paramref name="contentHash"/> (the SHA-256 Extract computed, lowercase hex) and detects
    /// their type, which must agree with the name's extension. <paramref name="creatorId"/> is the user the file counts
    /// for in the usage figures; <c>null</c> is the current user.
    /// </summary>
    public virtual async Task<Guid> CreateAsync(string fileName, byte[] bytes, string contentHash, Guid? creatorId = null)
    {
        var name = FitName(fileName);
        var file = await FileService.CreateAsync(new CreateBlobManagementFileInput
        {
            ContainerName = DocumentFileConsts.ContainerName,
            Name = name,
            CreatorId = creatorId,
            Sha256 = Convert.ToBase64String(Convert.FromHexString(contentHash)),
            File = new RemoteStreamContent(new MemoryStream(bytes, writable: false), name, disposeStream: true)
        });

        return file.Id;
    }

    /// <summary>The file's bytes, or <c>null</c> when it is not there (or is in the recycle bin with its document).</summary>
    public virtual async Task<Stream?> OpenOrNullAsync(FileOrigin file, CancellationToken cancellationToken = default)
    {
        if (file.FileId is { } fileId)
        {
            try
            {
                return (await FileService.GetContentAsync(fileId)).GetStream();
            }
            catch (EntityNotFoundException)
            {
                return null;
            }
        }

        return await _legacyContainer.GetOrNullAsync(file.BlobName!, cancellationToken);
    }

    /// <summary>Whether the file's bytes can be read.</summary>
    public virtual async Task<bool> ExistsAsync(FileOrigin file, CancellationToken cancellationToken = default)
    {
        if (file.FileId.HasValue)
        {
            await using var stream = await OpenOrNullAsync(file, cancellationToken);
            return stream != null;
        }

        return await _legacyContainer.ExistsAsync(file.BlobName!, cancellationToken);
    }

    /// <summary>
    /// Sends the file to the recycle bin with its document: it keeps its bytes until <see cref="DeletePermanentlyAsync"/>.
    /// A file named by <see cref="FileOrigin.BlobName"/> is left as it is.
    /// </summary>
    public virtual async Task DeleteAsync(FileOrigin file)
    {
        if (file.FileId is { } fileId)
        {
            await FileService.DeleteAsync(fileId);
        }
    }

    /// <summary>
    /// Takes the file back with its restored document. A file that is no longer there is logged and left: the document
    /// comes back without it, and reading it reports the file missing.
    /// </summary>
    public virtual async Task RestoreAsync(FileOrigin file)
    {
        if (file.FileId is not { } fileId)
        {
            return;
        }

        try
        {
            await FileService.RestoreAsync(fileId);
        }
        catch (EntityNotFoundException)
        {
            Logger.LogWarning("File {FileId} could not be restored: it is no longer there.", fileId);
        }
    }

    /// <summary>
    /// Deletes the file for good with its document. Blob Management removes the row in the caller's unit of work and the
    /// bytes once it commits. A file named by <see cref="FileOrigin.BlobName"/> is deleted at once, best-effort: a
    /// storage failure is logged and leaves the bytes behind, not the document. Call it once no document names the file:
    /// a blob another document still names (a sub-document's from #481, see
    /// <see cref="IDocumentRepository.AnyNamingBlobAsync"/>) is kept for that one.
    /// </summary>
    public virtual async Task DeletePermanentlyAsync(FileOrigin file)
    {
        if (file.FileId is { } fileId)
        {
            await FileService.DeleteAsync(fileId, permanently: true);
            return;
        }

        try
        {
            if (await _lazyServiceProvider.LazyGetRequiredService<IDocumentRepository>().AnyNamingBlobAsync(file.BlobName!))
            {
                Logger.LogInformation("Blob {BlobName} is kept: another document still names it.", file.BlobName);
                return;
            }

            await _legacyContainer.DeleteAsync(file.BlobName!);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete blob {BlobName}.", file.BlobName);
        }
    }

    /// <summary>
    /// <paramref name="fileName"/> within Blob Management's name length, its extension kept: the extension is what the
    /// detected type is reconciled with.
    /// </summary>
    protected static string FitName(string fileName)
    {
        var max = FileDescriptorConsts.MaxNameLength;
        if (fileName.Length <= max)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        if (extension.Length >= max)
        {
            return fileName[..max];
        }

        return fileName[..(max - extension.Length)] + extension;
    }
}

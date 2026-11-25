using System;
using System.Collections.Generic;
using Volo.Abp;
using Volo.Abp.Domain.Values;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// File origin information. Immutable after write and treated as a system-trusted anchor, with one exception:
/// <see cref="UploadedByUserName"/> is replaced by <c>IDocumentRepository.AnonymizeUploaderAsync</c> when the uploader's
/// data is erased (#698), and nothing else writes it.
/// </summary>
public class FileOrigin : ValueObject
{
    /// <summary>
    /// The file's id in Blob Management, in the container <see cref="DocumentFileConsts.ContainerName"/>; <c>null</c> for
    /// a file stored before documents moved there, which <see cref="BlobName"/> names instead. Exactly one of the two is
    /// set.
    /// </summary>
    public Guid? FileId { get; private set; }

    /// <summary>
    /// The file's name in Extract's own container (<c>extract-documents</c>), for a file stored before documents moved to
    /// Blob Management; <c>null</c> for one kept by <see cref="FileId"/>.
    /// </summary>
    public string? BlobName { get; private set; }

    /// <summary>
    /// Snapshot of the uploader's display name, redundantly stored so the document still says who uploaded it after the
    /// account is gone. When that user's data is erased (#698) it becomes <see cref="FileOriginConsts.AnonymizedUploaderName"/>.
    /// </summary>
    public string UploadedByUserName { get; private set; } = default!;

    /// <summary>Original file name.</summary>
    public string? OriginalFileName { get; private set; }

    /// <summary>File MIME type.</summary>
    public string ContentType { get; private set; } = default!;

    /// <summary>SHA-256 hash of file content in lowercase hex, length 64. Used for byte-level deduplication per uploader within each tenant (#655).</summary>
    public string ContentHash { get; private set; } = default!;

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; private set; }

    protected FileOrigin() { }

    /// <summary>A file kept by Blob Management, under <paramref name="fileId"/>.</summary>
    public FileOrigin(
        Guid fileId,
        string uploadedByUserName,
        string contentType,
        string contentHash,
        long fileSize,
        string? originalFileName = null)
        : this(uploadedByUserName, contentType, contentHash, fileSize, originalFileName)
    {
        FileId = Check.NotDefaultOrNull<Guid>(fileId, nameof(fileId));
    }

    /// <summary>
    /// A file under <paramref name="blobName"/> in Extract's own container: how a document stored before the move to
    /// Blob Management names its file. Nothing stores a new file that way.
    /// </summary>
    public FileOrigin(
        string blobName,
        string uploadedByUserName,
        string contentType,
        string contentHash,
        long fileSize,
        string? originalFileName = null)
        : this(uploadedByUserName, contentType, contentHash, fileSize, originalFileName)
    {
        BlobName = Check.NotNullOrWhiteSpace(blobName, nameof(blobName), FileOriginConsts.MaxBlobNameLength);
    }

    private FileOrigin(
        string uploadedByUserName,
        string contentType,
        string contentHash,
        long fileSize,
        string? originalFileName)
    {
        UploadedByUserName = Check.NotNullOrWhiteSpace(
            uploadedByUserName,
            nameof(uploadedByUserName),
            FileOriginConsts.MaxUploadedByUserNameLength);
        ContentType = Check.NotNullOrWhiteSpace(contentType, nameof(contentType), FileOriginConsts.MaxContentTypeLength);
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash), FileOriginConsts.MaxContentHashLength);
        FileSize = Check.Range(fileSize, nameof(fileSize), 0, long.MaxValue);
        OriginalFileName = NormalizeOptionalString(originalFileName, nameof(originalFileName), FileOriginConsts.MaxOriginalFileNameLength);
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return FileId ?? Guid.Empty;
        yield return BlobName ?? string.Empty;
        yield return UploadedByUserName;
        yield return OriginalFileName ?? string.Empty;
        yield return ContentType;
        yield return ContentHash;
        yield return FileSize;
    }

    private static string? NormalizeOptionalString(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            throw new AbpException($"{parameterName} can not be longer than {maxLength} characters.");
        }

        return value;
    }
}

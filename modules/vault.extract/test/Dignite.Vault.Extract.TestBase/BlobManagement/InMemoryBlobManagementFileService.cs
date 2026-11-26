using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dignite.Abp.BlobManagement;
using Dignite.Abp.BlobManagement.Files;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Content;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.BlobManagement;

/// <summary>
/// Blob Management's file service as Extract uses it, in memory: files stored by id, verified against the checksum the
/// caller sends, soft-deleted with their bytes kept, restored, deleted for good. Registered by
/// <see cref="VaultExtractTestBaseModule"/> unless a test module installs another (the real module, or a substitute).
/// What Extract does not call is not supported.
/// </summary>
public class InMemoryBlobManagementFileService : IBlobManagementFileService
{
    private readonly ConcurrentDictionary<Guid, StoredFile> _files = new();
    private readonly ConcurrentQueue<Guid> _created = new();

    /// <summary>The id of every file stored, in order, including those deleted for good since.</summary>
    public IReadOnlyCollection<Guid> CreatedIds => _created.ToArray();

    /// <summary>A stored file, whether in the recycle bin or not; <c>null</c> once deleted for good or never stored.</summary>
    public StoredFile? Find(Guid id) => _files.TryGetValue(id, out var file) ? file : null;

    public int Count => _files.Count;

    /// <summary>
    /// When set, <see cref="CreateAsync"/> throws it and stores nothing, as Blob Management refuses a file: the tenant is
    /// over its storage quota (<see cref="BlobManagementErrorCodes.StorageQuotaExceeded"/>), or Blob Management is switched
    /// off for it.
    /// </summary>
    public Exception? Refusal { get; set; }

    public Task<BlobManagementFileDto> CreateAsync(CreateBlobManagementFileInput input)
    {
        if (Refusal != null)
        {
            throw Refusal;
        }

        using var content = input.File;
        using var buffer = new MemoryStream();
        content.GetStream().CopyTo(buffer);
        var bytes = buffer.ToArray();

        if (input.Sha256 != null && input.Sha256 != Convert.ToBase64String(SHA256.HashData(bytes)))
        {
            throw new BusinessException(BlobManagementErrorCodes.ChecksumMismatch);
        }

        var dto = new BlobManagementFileDto
        {
            Id = Guid.NewGuid(),
            ContainerName = input.ContainerName,
            Name = input.Name ?? content.FileName ?? "file",
            MimeType = content.ContentType ?? "application/octet-stream",
            Size = bytes.LongLength,
            CreatorId = input.CreatorId
        };
        _files[dto.Id] = new StoredFile(dto, bytes);
        _created.Enqueue(dto.Id);
        return Task.FromResult(dto);
    }

    public Task<BlobManagementFileDto> GetAsync(Guid id) => Task.FromResult(GetLive(id).File);

    public Task<IRemoteStreamContent> GetContentAsync(Guid id)
    {
        var file = GetLive(id);
        return Task.FromResult<IRemoteStreamContent>(
            new RemoteStreamContent(new MemoryStream(file.Bytes, writable: false), file.File.Name, file.File.MimeType));
    }

    public Task DeleteAsync(Guid id, bool permanently = false)
    {
        if (permanently)
        {
            _files.TryRemove(id, out _);
        }
        else if (_files.TryGetValue(id, out var file))
        {
            file.IsDeleted = true;
        }

        return Task.CompletedTask;
    }

    public Task<BlobManagementFileDto> RestoreAsync(Guid id)
    {
        if (!_files.TryGetValue(id, out var file))
        {
            throw new EntityNotFoundException(typeof(BlobManagementFileDto), id);
        }

        file.IsDeleted = false;
        return Task.FromResult(file.File);
    }

    private StoredFile GetLive(Guid id)
        => _files.TryGetValue(id, out var file) && !file.IsDeleted
            ? file
            : throw new EntityNotFoundException(typeof(BlobManagementFileDto), id);

    public Task<PagedResultDto<BlobManagementFileDto>> GetListAsync(GetBlobManagementFilesInput input) => throw new NotSupportedException();

    public Task<BlobManagementFileDto> UpdateAsync(Guid id, UpdateBlobManagementFileInput input) => throw new NotSupportedException();

    public Task<BlobManagementFileDto> MoveAsync(Guid id, MoveBlobManagementFileInput input) => throw new NotSupportedException();

    public Task<BlobManagementFileDto> SetPublicAsync(Guid id, SetBlobManagementFilePublicInput input) => throw new NotSupportedException();

    public Task<BlobManagementUploadTicketDto> CreateUploadTicketAsync(CreateBlobManagementUploadTicketInput input) => throw new NotSupportedException();

    public Task<BlobManagementFileDto> ConfirmAsync(Guid id) => throw new NotSupportedException();

    public Task<BlobManagementReadLinkDto> CreateReadLinkAsync(Guid id, CreateBlobManagementReadLinkInput input) => throw new NotSupportedException();

    public Task<ListResultDto<BlobManagementReadLinkDto>> CreateReadLinksAsync(CreateBlobManagementReadLinksInput input) => throw new NotSupportedException();

    public sealed class StoredFile(BlobManagementFileDto file, byte[] bytes)
    {
        public BlobManagementFileDto File { get; } = file;

        public byte[] Bytes { get; } = bytes;

        public bool IsDeleted { get; set; }
    }
}

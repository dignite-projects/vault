using System;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dignite.Abp.BlobManagement.Files;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines.Segmentation;
using Dignite.Vault.Extract.Documents.Segments;
using Shouldly;
using Volo.Abp.Content;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// A document's own file through its lifecycle, on the real Blob Management modules: stored by id in the documents'
/// container, sent to the recycle bin and restored with its document, deleted for good with it.
/// </summary>
public class DocumentFileLifecycle_Tests : VaultExtractTestBase<DocumentSubmissionBlobManagementTestModule>
{
    private static readonly Guid UserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // The smallest PDF a signature probe accepts: Blob Management decides the type from the bytes.
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\n"
        + "trailer<</Root 1 0 R>>\n%%EOF\n");

    private readonly IDocumentAppService _appService;
    private readonly IBlobManagementFileService _fileService;
    private readonly IDocumentRepository _documentRepository;

    public DocumentFileLifecycle_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _fileService = GetRequiredService<IBlobManagementFileService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task An_uploads_file_follows_its_document_into_the_recycle_bin_back_out_and_away_for_good()
    {
        await WithUnitOfWorkAsync(async () => await GetRequiredService<IRepository<DocumentType, Guid>>()
            .InsertAsync(new DocumentType(Guid.NewGuid(), tenantId: null, "contract", "Contract"), autoSave: true));

        using var principal = GetRequiredService<ICurrentPrincipalAccessor>().Change(User());

        var dto = await WithUnitOfWorkAsync(() => _appService.UploadAsync(new UploadDocumentInput
        {
            File = new RemoteStreamContent(new MemoryStream(Pdf), "contract.pdf", "application/pdf")
        }));

        var fileId = await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(dto.Id, includeDetails: false);
            document.FileOrigin!.BlobName.ShouldBeNull();
            return document.FileOrigin.FileId.ShouldNotBeNull();
        });

        var file = await _fileService.GetAsync(fileId);
        file.ContainerName.ShouldBe(DocumentFileConsts.ContainerName);
        file.Name.ShouldBe("contract.pdf");
        file.MimeType.ShouldBe("application/pdf");
        file.CreatorId.ShouldBe(UserId);
        (await ReadOriginalAsync(dto.Id)).ShouldBe(Pdf);

        // The recycle bin: the file goes with the document and keeps its bytes.
        await WithUnitOfWorkAsync(() => _appService.DeleteAsync(dto.Id));
        await Should.ThrowAsync<EntityNotFoundException>(() => _fileService.GetAsync(fileId));

        await WithUnitOfWorkAsync(() => _appService.RestoreAsync(dto.Id));
        (await _fileService.GetAsync(fileId)).Size.ShouldBe(Pdf.Length);
        (await ReadOriginalAsync(dto.Id)).ShouldBe(Pdf);

        // Deleted for good: nothing is left to restore.
        await WithUnitOfWorkAsync(() => _appService.DeleteAsync(dto.Id));
        await WithUnitOfWorkAsync(() => _appService.PermanentDeleteAsync(dto.Id));
        await Should.ThrowAsync<EntityNotFoundException>(() => _fileService.RestoreAsync(fileId));
    }

    [Fact]
    public async Task A_withdrawn_sub_documents_own_file_goes_to_the_recycle_bin_with_it()
    {
        var sourceId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        var fileId = await WithUnitOfWorkAsync(async () =>
        {
            var cut = await GetRequiredService<DocumentFileStore>()
                .CreateAsync("bundle (p2).pdf", Pdf, Convert.ToHexStringLower(SHA256.HashData(Pdf)));

            await _documentRepository.InsertAsync(new Document(sourceId, tenantId: null, fileOrigin: null), autoSave: true);
            await _documentRepository.InsertAsync(
                new Document(childId, tenantId: null, new FileOrigin(
                    cut, "test-user", "application/pdf", Convert.ToHexStringLower(SHA256.HashData(Pdf)), Pdf.Length,
                    "bundle (p2).pdf")),
                autoSave: true);

            var segment = new DocumentSegment(
                Guid.NewGuid(), tenantId: null, sourceId, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("slice"))),
                "slice", ordinal: 0, DocumentSegmentKind.Text, firstPage: 2, lastPage: 2);
            segment.MarkSpawned(childId);
            await GetRequiredService<IRepository<DocumentSegment, Guid>>().InsertAsync(segment, autoSave: true);
            return cut;
        });

        await WithUnitOfWorkAsync(() => GetRequiredService<SubDocumentRetractor>().RetractAllAsync(sourceId));

        await WithUnitOfWorkAsync(async () =>
        {
            using (GetRequiredService<IDataFilter>().Disable<ISoftDelete>())
            {
                (await _documentRepository.GetAsync(childId, includeDetails: false)).IsDeleted.ShouldBeTrue();
            }
        });

        // In the recycle bin, not gone: restoring brings it back.
        await Should.ThrowAsync<EntityNotFoundException>(() => _fileService.GetAsync(fileId));
        (await _fileService.RestoreAsync(fileId)).Size.ShouldBe(Pdf.Length);
    }

    private async Task<byte[]> ReadOriginalAsync(Guid documentId)
    {
        var content = await WithUnitOfWorkAsync(() => _appService.GetBlobAsync(documentId));
        await using var stream = content.GetStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static ClaimsPrincipal User()
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, UserId.ToString()),
                new Claim(AbpClaimTypes.UserName, "uploader")
            ],
            authenticationType: "Test"));
}

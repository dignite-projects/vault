using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dignite.Abp.BlobManagement.Files;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.LegacyFiles;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Files stored in Extract's own container before documents moved to Blob Management, moved over one document at a
/// time on the real Blob Management modules: verified against the recorded hash, never lost on the way, and moved once.
/// </summary>
public class LegacyDocumentFileMove_Tests : VaultExtractTestBase<DocumentSubmissionBlobManagementTestModule>
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\n"
        + "trailer<</Root 1 0 R>>\n%%EOF\n");

    private readonly IDocumentRepository _documentRepository;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _legacyContainer;
    private readonly IBlobManagementFileService _fileService;
    private readonly LegacyDocumentFileMover _mover;

    public LegacyDocumentFileMove_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _legacyContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _fileService = GetRequiredService<IBlobManagementFileService>();
        _mover = GetRequiredService<LegacyDocumentFileMover>();
    }

    [Fact]
    public async Task A_documents_file_moves_to_Blob_Management_once_and_leaves_Extracts_container()
    {
        var (documentId, blobName) = await ArrangeLegacyDocumentAsync(Pdf);

        (await _mover.MoveAsync(documentId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.Moved);

        var origin = await LoadOriginAsync(documentId);
        origin.BlobName.ShouldBeNull();
        origin.OriginalFileName.ShouldBe("contract.pdf");
        origin.ContentHash.ShouldBe(Sha256Hex(Pdf));
        var file = await _fileService.GetAsync(origin.FileId!.Value);
        file.ContainerName.ShouldBe(DocumentFileConsts.ContainerName);
        file.Name.ShouldBe("contract.pdf");
        (await ReadAsync(origin.FileId.Value)).ShouldBe(Pdf);
        (await _legacyContainer.ExistsAsync(blobName)).ShouldBeFalse();

        // A second run finds nothing to do.
        (await _mover.MoveAsync(documentId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.NotLegacy);
        (await LoadOriginAsync(documentId)).FileId.ShouldBe(origin.FileId);
    }

    [Fact]
    public async Task A_document_in_the_recycle_bin_moves_with_its_file_in_the_recycle_bin()
    {
        var (documentId, _) = await ArrangeLegacyDocumentAsync(Pdf, deleted: true);

        (await _mover.MoveAsync(documentId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.Moved);

        var fileId = (await LoadOriginAsync(documentId)).FileId!.Value;
        await Should.ThrowAsync<EntityNotFoundException>(() => _fileService.GetAsync(fileId));
        (await _fileService.RestoreAsync(fileId)).Size.ShouldBe(Pdf.Length);
    }

    [Fact]
    public async Task A_blob_whose_bytes_changed_or_that_is_gone_stays_where_it_is()
    {
        var (changedId, changedBlob) = await ArrangeLegacyDocumentAsync(Pdf, storedBytes: Encoding.ASCII.GetBytes("%PDF-1.4 other"));
        var (missingId, _) = await ArrangeLegacyDocumentAsync(Pdf, storeBlob: false);

        (await _mover.MoveAsync(changedId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.ContentChanged);
        (await _mover.MoveAsync(missingId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.FileMissing);

        (await LoadOriginAsync(changedId)).BlobName.ShouldBe(changedBlob);
        (await LoadOriginAsync(changedId)).FileId.ShouldBeNull();
        (await _legacyContainer.ExistsAsync(changedBlob)).ShouldBeTrue();
        (await LoadOriginAsync(missingId)).FileId.ShouldBeNull();
    }

    [Fact]
    public async Task A_file_Blob_Management_refuses_stays_where_it_is()
    {
        // Bytes that are not a PDF under a .pdf name: Blob Management detects the type and refuses the pair.
        var text = Encoding.UTF8.GetBytes("plain text, not a PDF");
        var (documentId, blobName) = await ArrangeLegacyDocumentAsync(text);

        var result = await _mover.MoveAsync(documentId);

        result.Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.Refused);
        result.Reason.ShouldNotBeNullOrEmpty();
        (await LoadOriginAsync(documentId)).BlobName.ShouldBe(blobName);
        (await _legacyContainer.ExistsAsync(blobName)).ShouldBeTrue();
    }

    /// <summary>
    /// A permanent delete reads the document, then deletes its row and reclaims the file it read. If the file moved in
    /// between, reclaiming the old one would leave the new one in Blob Management with no document, counted against the
    /// quota and holding what was to be deleted. The row is deleted only as it was read.
    /// </summary>
    [Fact]
    public async Task A_permanent_delete_that_read_the_document_before_its_file_moved_deletes_nothing()
    {
        var (documentId, _) = await ArrangeLegacyDocumentAsync(Pdf);
        var readStamp = await WithUnitOfWorkAsync(async () => (await _documentRepository.GetAsync(documentId, includeDetails: false)).ConcurrencyStamp);

        (await _mover.MoveAsync(documentId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.Moved);

        await Should.ThrowAsync<AbpDbConcurrencyException>(
            () => WithUnitOfWorkAsync(() => _documentRepository.HardDeleteAsync(documentId, readStamp)));
        (await LoadOriginAsync(documentId)).FileId.ShouldNotBeNull();

        var currentStamp = await WithUnitOfWorkAsync(async () => (await _documentRepository.GetAsync(documentId, includeDetails: false)).ConcurrencyStamp);
        await WithUnitOfWorkAsync(() => _documentRepository.HardDeleteAsync(documentId, currentStamp));
        (await WithUnitOfWorkAsync(() => _documentRepository.FindAsync(documentId))).ShouldBeNull();
    }

    [Fact]
    public async Task A_run_that_finds_the_file_moved_meanwhile_keeps_the_other_runs_copy_and_drops_its_own()
    {
        var (documentId, _) = await ArrangeLegacyDocumentAsync(Pdf);
        var mover = new RacingMover(this) { Rival = _mover };

        (await mover.MoveAsync(documentId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.NotLegacy);

        var files = await _fileService.GetListAsync(new GetBlobManagementFilesInput
        {
            ContainerName = DocumentFileConsts.ContainerName,
            MaxResultCount = 10
        });
        files.TotalCount.ShouldBe(1);
        (await LoadOriginAsync(documentId)).FileId.ShouldBe(files.Items[0].Id);
    }

    [Fact]
    public async Task The_data_seed_starts_a_walk_that_moves_every_file_it_can()
    {
        var jobs = GetRequiredService<IBackgroundJobManager>();
        await GetRequiredService<LegacyDocumentFileMoveDataSeedContributor>().SeedAsync(new DataSeedContext());
        await jobs.DidNotReceiveWithAnyArgs().EnqueueAsync(default(LegacyDocumentFileMoveJobArgs)!);

        var (first, _) = await ArrangeLegacyDocumentAsync(Pdf);
        var (missing, _) = await ArrangeLegacyDocumentAsync(Pdf, storeBlob: false);
        var (second, _) = await ArrangeLegacyDocumentAsync(Pdf, deleted: true);

        await GetRequiredService<LegacyDocumentFileMoveDataSeedContributor>().SeedAsync(new DataSeedContext());
        await jobs.Received(1).EnqueueAsync(
            Arg.Is<LegacyDocumentFileMoveJobArgs>(a => a.TenantId == null && a.AfterId == null),
            BackgroundJobPriority.Low,
            Arg.Any<TimeSpan?>());

        await GetRequiredService<LegacyDocumentFileMoveJob>().ExecuteAsync(new LegacyDocumentFileMoveJobArgs());

        (await LoadOriginAsync(first)).FileId.ShouldNotBeNull();
        (await LoadOriginAsync(second)).FileId.ShouldNotBeNull();
        (await LoadOriginAsync(missing)).FileId.ShouldBeNull();
        (await WithUnitOfWorkAsync(() => _documentRepository.GetIdsWithLegacyFileAsync(null, 10))).ShouldBe([missing]);
    }

    private async Task<(Guid DocumentId, string BlobName)> ArrangeLegacyDocumentAsync(
        byte[] recorded, bool deleted = false, bool storeBlob = true, byte[]? storedBytes = null)
    {
        var documentId = Guid.NewGuid();
        var blobName = $"{Guid.NewGuid():N}.pdf";
        var stored = storeBlob ? storedBytes ?? recorded : null;
        if (stored != null)
        {
            await _legacyContainer.SaveAsync(blobName, stored);
        }

        await WithUnitOfWorkAsync(async () =>
        {
            var document = new Document(documentId, tenantId: null, new FileOrigin(
                blobName, "uploader", "application/pdf", Sha256Hex(recorded), recorded.Length, "contract.pdf"));
            await _documentRepository.InsertAsync(document, autoSave: true);
            if (deleted)
            {
                await _documentRepository.DeleteAsync(document, autoSave: true);
            }
        });

        return (documentId, blobName);
    }

    private Task<FileOrigin> LoadOriginAsync(Guid documentId)
        => WithUnitOfWorkAsync(async () =>
        {
            using (GetRequiredService<IDataFilter>().Disable<ISoftDelete>())
            {
                return (await _documentRepository.FindAsync(d => d.Id == documentId, includeDetails: false))!.FileOrigin!;
            }
        });

    private async Task<byte[]> ReadAsync(Guid fileId)
    {
        var content = await _fileService.GetContentAsync(fileId);
        await using var stream = content.GetStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>A run that another run overtakes between storing its copy and pointing the document at it.</summary>
    private sealed class RacingMover(LegacyDocumentFileMove_Tests test) : LegacyDocumentFileMover(
        test.GetRequiredService<IDocumentRepository>(),
        test.GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>(),
        test.GetRequiredService<DocumentFileStore>(),
        test.GetRequiredService<Dignite.Vault.Extract.Documents.Pipelines.DocumentWriteLock>(),
        test.GetRequiredService<Volo.Abp.Uow.IUnitOfWorkManager>(),
        test.GetRequiredService<IDataFilter>(),
        test.GetRequiredService<Volo.Abp.Threading.ICancellationTokenProvider>())
    {
        public LegacyDocumentFileMover? Rival { get; set; }

        protected override async Task<bool> AttachAsync(Guid documentId, string blobName, Guid fileId)
        {
            if (Rival is { } rival)
            {
                Rival = null;
                (await rival.MoveAsync(documentId)).Outcome.ShouldBe(LegacyDocumentFileMoveOutcome.Moved);
            }

            return await base.AttachAsync(documentId, blobName, fileId);
        }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

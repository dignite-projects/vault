using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.BlobManagement.Files;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Users;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentSubmissionStagedFilesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<DocumentExtractionClient>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        context.Services.AddKeyedSingleton(VaultExtractConsts.TitleGeneratorChatClientKey, Substitute.For<IChatClient>());
        // Blob Management as Extract sees it: the interface only. It keeps what it is given under a new id.
        var fileService = Substitute.For<IBlobManagementFileService>();
        fileService.CreateAsync(Arg.Any<CreateBlobManagementFileInput>())
            .Returns(call => new BlobManagementFileDto
            {
                Id = Guid.NewGuid(),
                ContainerName = call.Arg<CreateBlobManagementFileInput>().ContainerName,
                Name = call.Arg<CreateBlobManagementFileInput>().Name!
            });
        context.Services.AddSingleton(fileService);
        // A signed-in uploader, as a staged file records one.
        context.Services.AddSingleton(Substitute.For<ICurrentUser>());
    }
}

/// <summary>
/// A submission whose Markdown and original file were staged through Blob Management upload links: the file must be
/// the caller's pending upload in the submission container, the original is held to what an upload is held to and
/// stored as a file of the document's own, and both staged files are deleted once taken.
/// </summary>
public class DocumentSubmissionStagedFiles_Tests : VaultExtractTestBase<DocumentSubmissionStagedFilesTestModule>
{
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBlobManagementFileService _fileService;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _documentContainer;
    private readonly IDistributedEventBus _eventBus;
    private readonly DocumentExtractionClient _extractionClient;

    public DocumentSubmissionStagedFiles_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _fileService = GetRequiredService<IBlobManagementFileService>();
        _documentContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _extractionClient = GetRequiredService<DocumentExtractionClient>();

        var currentUser = GetRequiredService<ICurrentUser>();
        currentUser.Id.Returns(UserId);
        currentUser.UserName.Returns("agent-user");
        currentUser.IsAuthenticated.Returns(true);

        // Whatever Markdown arrives reads as one paragraph holding it, so the stored text shows which source was read.
        _extractionClient.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var text = new StreamReader(call.Arg<Stream>()).ReadToEnd();
                var document = new ExtractionDocument("submitted");
                var section = new ExtractionDocumentSection();
                section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(text)));
                document.Sections.Add(section);
                return document;
            });
    }

    [Fact]
    public async Task A_staged_markdown_and_original_become_the_documents_text_and_file()
    {
        await ArrangeTypeAsync();
        var markdownId = Stage("invoice.md", "text/markdown", Encoding.UTF8.GetBytes("Invoice INV-7 from a file"));
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7 staged original");
        var originalId = Stage("invoice.pdf", "application/pdf", pdf);

        var dto = await _appService.SubmitAsync(new SubmitDocumentInput
        {
            MarkdownFileId = markdownId,
            OriginalFileId = originalId,
            DocumentTypeCode = "invoice"
        });

        dto.Markdown.ShouldBe("Invoice INV-7 from a file");
        dto.TextOrigin.ShouldBe(DocumentTextOrigin.Submitted);
        dto.FileOrigin.ShouldNotBeNull();
        dto.FileOrigin.OriginalFileName.ShouldBe("invoice.pdf");
        dto.FileOrigin.ContentType.ShouldBe("application/pdf");
        dto.FileOrigin.FileSize.ShouldBe(pdf.Length);

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(dto.Id, includeDetails: false);
            document.FileOrigin!.ContentHash.ShouldBe(ContentHasher.Sha256Hex(pdf));
            document.FileOrigin.FileId.ShouldNotBeNull();
            document.FileOrigin.BlobName.ShouldBeNull();
            document.CreatorId.ShouldBe(UserId);
        });

        // Stored as a file of its own in the documents' container, checked against its hash; the staged copies go.
        await _fileService.Received(1).CreateAsync(Arg.Is<CreateBlobManagementFileInput>(input =>
            input.ContainerName == DocumentFileConsts.ContainerName
            && input.Name == "invoice.pdf"
            && input.Sha256 == Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(pdf))));
        await _fileService.Received(1).DeleteAsync(markdownId);
        await _fileService.Received(1).DeleteAsync(originalId);
        await _eventBus.Received(1).PublishAsync(Arg.Is<DocumentUploadedEto>(e =>
            e.DocumentId == dto.Id && e.FileName == "invoice.pdf" && e.ContentType == "application/pdf" && e.FileSize == pdf.Length));
    }

    [Fact]
    public async Task A_staged_file_of_another_user_is_not_found()
    {
        await ArrangeTypeAsync();
        var markdownId = Stage("invoice.md", "text/markdown", Encoding.UTF8.GetBytes("text"), creatorId: Guid.NewGuid());

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { MarkdownFileId = markdownId }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedFileNotFound);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task A_staged_file_already_taken_or_in_another_container_is_not_found()
    {
        await ArrangeTypeAsync();
        var taken = Stage("a.md", "text/markdown", Encoding.UTF8.GetBytes("text"), pending: false);
        var elsewhere = Stage("b.md", "text/markdown", Encoding.UTF8.GetBytes("text"), container: "media");
        var missing = Guid.NewGuid();
        _fileService.GetAsync(missing).ThrowsAsync(new EntityNotFoundException(typeof(object), missing));

        foreach (var id in new[] { taken, elsewhere, missing })
        {
            var ex = await Should.ThrowAsync<BusinessException>(
                () => _appService.SubmitAsync(new SubmitDocumentInput { MarkdownFileId = id }));
            ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedFileNotFound);
        }

        await NothingWrittenAsync();
    }

    [Fact]
    public async Task An_original_of_a_type_an_upload_refuses_is_refused()
    {
        await ArrangeTypeAsync();
        var originalId = Stage("notes.html", "text/html", Encoding.UTF8.GetBytes("<p>x</p>"));

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = "# Notes",
            OriginalFileId = originalId
        }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.UnsupportedFileType);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task A_markdown_file_that_is_not_utf8_is_refused()
    {
        await ArrangeTypeAsync();
        var markdownId = Stage("invoice.md", "text/plain", [0x96, 0x7B, 0x95, 0xB6]);

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { MarkdownFileId = markdownId }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedMarkdownNotUtf8);
    }

    /// <summary>With an original, the original's own hash recognises a retry; the same text over another file is a document of its own.</summary>
    [Fact]
    public async Task With_an_original_the_same_text_is_not_a_duplicate()
    {
        await ArrangeTypeAsync();
        await _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = "# Invoice",
            OriginalFileId = Stage("a.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7 a"))
        });

        var second = await _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = "# Invoice",
            OriginalFileId = Stage("b.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7 b"))
        });

        second.FileOrigin!.OriginalFileName.ShouldBe("b.pdf");
    }

    [Fact]
    public async Task Markdown_given_twice_is_refused()
    {
        await ArrangeTypeAsync();
        var markdownId = Stage("invoice.md", "text/markdown", Encoding.UTF8.GetBytes("text"));

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# x", MarkdownFileId = markdownId }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedMarkdownTwice);
    }

    [Fact]
    public async Task An_upload_link_is_a_ticket_into_the_submission_container_for_the_kind()
    {
        _fileService.CreateUploadTicketAsync(Arg.Any<CreateBlobManagementUploadTicketInput>()).Returns(new BlobManagementUploadTicketDto
        {
            FileId = Guid.NewGuid(),
            Ticket = "t",
            UploadUrl = "/api/blob-management/uploads/t",
            ExpirationTime = DateTime.UtcNow.AddMinutes(10)
        });

        var markdown = await _appService.CreateSubmissionUploadAsync(new CreateSubmissionUploadInput { Kind = SubmissionFileKind.Markdown });
        var original = await _appService.CreateSubmissionUploadAsync(new CreateSubmissionUploadInput { Kind = SubmissionFileKind.Original });

        markdown.UploadUrl.ShouldBe("/api/blob-management/uploads/t");
        await _fileService.Received(1).CreateUploadTicketAsync(Arg.Is<CreateBlobManagementUploadTicketInput>(t =>
            t.ContainerName == DocumentSubmissionConsts.UploadContainerName
            && t.AllowedContentTypes.Contains("text/markdown")
            && !t.AllowedContentTypes.Contains("application/pdf")
            && t.UploaderId == null));
        await _fileService.Received(1).CreateUploadTicketAsync(Arg.Is<CreateBlobManagementUploadTicketInput>(t =>
            t.ContainerName == DocumentSubmissionConsts.UploadContainerName
            && t.AllowedContentTypes.Contains("application/pdf")
            && t.MaxSize == DocumentConsts.MaxUploadFileBytes));
        original.MaxSize.ShouldBe(DocumentConsts.MaxUploadFileBytes);
    }

    private Guid Stage(string name, string mimeType, byte[] bytes, Guid? creatorId = null, bool pending = true, string? container = null)
    {
        var id = Guid.NewGuid();
        _fileService.GetAsync(id).Returns(new BlobManagementFileDto
        {
            Id = id,
            ContainerName = container ?? DocumentSubmissionConsts.UploadContainerName,
            Name = name,
            MimeType = mimeType,
            Size = bytes.Length,
            IsPending = pending,
            CreatorId = creatorId ?? UserId
        });
        _fileService.GetContentAsync(id).Returns(_ => new RemoteStreamContent(new MemoryStream(bytes), name, mimeType));
        return id;
    }

    private async Task ArrangeTypeAsync()
    {
        await WithUnitOfWorkAsync(async () =>
            await GetRequiredService<IRepository<DocumentType, Guid>>().InsertAsync(
                new DocumentType(Guid.NewGuid(), tenantId: null, "invoice", "Invoice"), autoSave: true));
    }

    private async Task NothingWrittenAsync()
    {
        await WithUnitOfWorkAsync(async () => (await _documentRepository.GetCountAsync()).ShouldBe(0));
        await _fileService.DidNotReceiveWithAnyArgs().DeleteAsync(default);
    }
}

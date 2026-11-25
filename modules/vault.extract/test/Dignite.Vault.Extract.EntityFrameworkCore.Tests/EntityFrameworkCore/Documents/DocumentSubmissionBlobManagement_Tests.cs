using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.BlobManagement;
using Dignite.Abp.BlobManagement.EntityFrameworkCore;
using Dignite.Abp.BlobManagement.Files;
using Dignite.Abp.BlobManagement.Presets;
using Dignite.Abp.BlobManagement.Uploads;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BackgroundWorkers;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.Content;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// The real Blob Management modules, on SQLite and a temporary folder, as a host installs them: what the substitute in
/// <see cref="DocumentSubmissionStagedFiles_Tests"/> stands for, run once for real.
/// </summary>
[DependsOn(
    typeof(VaultExtractEntityFrameworkCoreTestModule),
    typeof(BlobManagementApplicationModule),
    typeof(BlobManagementEntityFrameworkCoreModule),
    typeof(AbpBlobStoringFileSystemModule))]
public class DocumentSubmissionBlobManagementTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<DocumentExtractionClient>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        context.Services.AddKeyedSingleton(VaultExtractConsts.TitleGeneratorChatClientKey, Substitute.For<IChatClient>());

        Configure<AbpBackgroundWorkerOptions>(options => options.IsEnabled = false);

        var folder = Path.Combine(Path.GetTempPath(), "vault-submission-tests", Guid.NewGuid().ToString("N"));
        Configure<AbpBlobStoringOptions>(options =>
        {
            options.Containers.ConfigureDefault(container => container.UseFileSystem(fs => fs.BasePath = folder));
            options.Containers.Configure(DocumentSubmissionConsts.UploadContainerName, container =>
            {
                container.UseBlobManagementContainer();
                container.UseAnyPreset(preset => preset.MaxSizeInMegabytes = 20);
            });
            options.Containers.Configure(DocumentFileConsts.ContainerName, container =>
            {
                container.UseBlobManagementContainer();
                container.UseAnyPreset(preset => preset.MaxSizeInMegabytes = 20);
            });
        });

        // One database for both modules' tables: the base test module's connection holds Extract's only.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        new VaultExtractDbContext(new DbContextOptionsBuilder<VaultExtractDbContext>().UseSqlite(connection).Options)
            .GetService<IRelationalDatabaseCreator>().CreateTables();
        new BlobManagementDbContext(new DbContextOptionsBuilder<BlobManagementDbContext>().UseSqlite(connection).Options)
            .GetService<IRelationalDatabaseCreator>().CreateTables();
        Configure<AbpDbContextOptions>(options => options.Configure(c => c.UseSqlite(connection)));
    }
}

public class DocumentSubmissionBlobManagement_Tests : VaultExtractTestBase<DocumentSubmissionBlobManagementTestModule>
{
    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // The smallest PDF a signature probe accepts: Blob Management decides the type from the bytes.
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\n"
        + "trailer<</Root 1 0 R>>\n%%EOF\n");

    public DocumentSubmissionBlobManagement_Tests()
    {
        GetRequiredService<DocumentExtractionClient>()
            .ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var document = new ExtractionDocument("submitted");
                var section = new ExtractionDocumentSection();
                section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(new StreamReader(call.Arg<Stream>()).ReadToEnd())));
                document.Sections.Add(section);
                return document;
            });
    }

    [Fact]
    public async Task Files_uploaded_with_submission_links_become_the_documents_text_and_file_and_leave_the_staging_area()
    {
        await WithUnitOfWorkAsync(async () => await GetRequiredService<IRepository<DocumentType, Guid>>()
            .InsertAsync(new DocumentType(Guid.NewGuid(), tenantId: null, "invoice", "Invoice"), autoSave: true));

        var appService = GetRequiredService<IDocumentAppService>();
        var fileService = GetRequiredService<IBlobManagementFileService>();

        using (GetRequiredService<ICurrentPrincipalAccessor>().Change(User()))
        {
            var markdownLink = await appService.CreateSubmissionUploadAsync(new CreateSubmissionUploadInput { Kind = SubmissionFileKind.Markdown });
            var originalLink = await appService.CreateSubmissionUploadAsync(new CreateSubmissionUploadInput { Kind = SubmissionFileKind.Original });

            // What the upload endpoint does with the body the client's curl sends, without signing in.
            await UploadAsync(markdownLink, "invoice.md", "text/markdown", Encoding.UTF8.GetBytes("Invoice INV-9, staged"));
            await UploadAsync(originalLink, "invoice.pdf", "application/pdf", Pdf);

            var staged = await fileService.GetAsync(originalLink.FileId);
            staged.IsPending.ShouldBeTrue();
            staged.MimeType.ShouldBe("application/pdf");
            staged.CreatorId.ShouldBe(UserId);

            var dto = await WithUnitOfWorkAsync(() => appService.SubmitAsync(new SubmitDocumentInput
            {
                MarkdownFileId = markdownLink.FileId,
                OriginalFileId = originalLink.FileId,
                DocumentTypeCode = "invoice"
            }));

            dto.Markdown.ShouldBe("Invoice INV-9, staged");
            dto.FileOrigin!.OriginalFileName.ShouldBe("invoice.pdf");
            dto.FileOrigin.ContentType.ShouldBe("application/pdf");

            // The original is readable from the document; the staged copies are gone.
            var blob = await appService.GetBlobAsync(dto.Id);
            using (var reader = new MemoryStream())
            {
                await blob.GetStream().CopyToAsync(reader);
                reader.ToArray().ShouldBe(Pdf);
            }

            await Should.ThrowAsync<EntityNotFoundException>(() => fileService.GetAsync(markdownLink.FileId));
            await Should.ThrowAsync<EntityNotFoundException>(() => fileService.GetAsync(originalLink.FileId));
        }
    }

    private async Task UploadAsync(SubmissionUploadDto link, string name, string contentType, byte[] bytes)
    {
        var ticket = Uri.UnescapeDataString(link.UploadUrl[(link.UploadUrl.LastIndexOf('/') + 1)..]);
        using var principalScope = GetRequiredService<ICurrentPrincipalAccessor>().Change(new ClaimsPrincipal());
        await GetRequiredService<IBlobManagementUploadAppService>()
            .UploadAsync(ticket, new RemoteStreamContent(new MemoryStream(bytes), name, contentType));
    }

    private static ClaimsPrincipal User()
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, UserId.ToString()),
                new Claim(AbpClaimTypes.UserName, "agent-user")
            ],
            authenticationType: "Test"));
}

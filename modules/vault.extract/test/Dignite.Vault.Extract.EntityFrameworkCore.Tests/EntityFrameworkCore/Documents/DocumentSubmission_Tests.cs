using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Submissions;
using Dignite.Vault.Parse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentSubmissionTestModule : AbpModule
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
    }
}

/// <summary>
/// <c>DocumentAppService.SubmitAsync</c> against the real SQLite database: each stage the caller supplies completes in
/// the call with what it supplied, no model is queued for it, and the stages it leaves out are queued as for an upload.
/// The Parse router is a substitute answering the tree the Markdown engine builds for two pages, so the facts about the
/// stored text and layout hold for whatever the engine returns.
/// </summary>
public class DocumentSubmission_Tests : VaultExtractTestBase<DocumentSubmissionTestModule>
{
    private const string TypeCode = "invoice";

    /// <summary>The name the Parse Markdown engine gives itself.</summary>
    private const string MarkdownProvider = "Markdown";

    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentExtractionClient _extractionClient;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;

    public DocumentSubmission_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _extractionClient = GetRequiredService<DocumentExtractionClient>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();

        // The tree the Markdown engine builds for "# Invoice\n\nNo. INV-7\n\n<!-- PageBreak -->\n\nTotal 1200": two pages,
        // each element on its page. The page break itself is not in the tree.
        _extractionClient.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => TwoPages());
    }

    [Fact]
    public async Task A_document_submitted_with_its_type_and_fields_completes_every_stage_without_queueing_a_model()
    {
        var typeId = await ArrangeTypeAsync();

        var dto = await _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = "# Invoice\n\nNo. INV-7\n\n<!-- PageBreak -->\n\nTotal 1200",
            DocumentTypeCode = TypeCode,
            Fields = Fields(("invoice_no", "\"INV-7\""), ("total", "1200")),
            FieldSources = new Dictionary<string, List<string>>
            {
                ["invoice_no"] = ["No. INV-7"],
                ["total"] = ["Total 1200"]
            }
        });

        dto.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
        dto.TextOrigin.ShouldBe(DocumentTextOrigin.Submitted);
        dto.DocumentTypeCode.ShouldBe(TypeCode);
        dto.FileOrigin.ShouldBeNull();
        dto.Markdown.ShouldBe("# Invoice\n\nNo. INV-7\n\nTotal 1200");
        dto.Title.ShouldBe("Invoice");
        dto.ExtractedFields!.Keys.ShouldBe(["invoice_no", "total"], ignoreOrder: true);
        dto.FieldSources!.Keys.ShouldBe(["invoice_no", "total"], ignoreOrder: true);

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(dto.Id, includeDetails: false);
            document.DocumentTypeId.ShouldBe(typeId);
            document.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Confirmed);
            document.ExtractionMetadata!.TextOrigin.ShouldBe(DocumentTextOrigin.Submitted);
            document.ExtractionMetadata.ProviderName.ShouldBe(MarkdownProvider);
            document.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
            document.FieldFingerprint.ShouldNotBeNull();

            foreach (var pipeline in VaultExtractPipelines.KeyPipelines)
            {
                (await _runRepository.FindLatestByDocumentAndCodeAsync(dto.Id, pipeline))!.Status
                    .ShouldBe(PipelineRunStatus.Succeeded, pipeline);
            }
        });

        // Read as a Markdown file, so the router hands it to the Markdown engine.
        await _extractionClient.Received(1).ExtractAsync(
            Arg.Any<Stream>(), dto.Id.ToString(), "text/markdown",
            Arg.Is<DocumentExtractionOptions?>(o => o.GetFileExtension() == ".md"), Arg.Any<CancellationToken>());
        // The pages reach the stored layout.
        await _blobContainer.Received(1).SaveAsync(
            Extract.Documents.Layout.DocumentLayoutStore.BlobName(dto.Id), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentFieldExtractionJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentCabinetSuggestionJobArgs>(a => a.DocumentId == dto.Id), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());

        await _eventBus.Received(1).PublishAsync(Arg.Is<DocumentUploadedEto>(e => e.DocumentId == dto.Id));
        await _eventBus.Received(1).PublishAsync(Arg.Is<DocumentTextExtractedEto>(e => e.DocumentId == dto.Id));
        await _eventBus.Received(1).PublishAsync(Arg.Is<DocumentClassifiedEto>(e => e.DocumentId == dto.Id && e.DocumentTypeCode == TypeCode));
    }

    [Fact]
    public async Task A_missing_required_field_is_reported_and_does_not_hold_the_document_back()
    {
        await ArrangeTypeAsync();

        var dto = await _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = "# Invoice",
            DocumentTypeCode = TypeCode,
            Fields = Fields(("total", "1200"))
        });

        dto.ReviewReasons.ShouldBe(DocumentReviewReasons.MissingRequiredFields);
        dto.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
    }

    [Fact]
    public async Task A_type_without_fields_leaves_field_extraction_to_the_server()
    {
        await ArrangeTypeAsync();

        var dto = await _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice", DocumentTypeCode = TypeCode });

        dto.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentFieldExtractionJobArgs>(a => a.DocumentId == dto.Id), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentClassificationJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _eventBus.Received(1).PublishAsync(Arg.Is<DocumentClassifiedEto>(e => e.DocumentId == dto.Id));
    }

    [Fact]
    public async Task No_type_leaves_classification_to_the_server()
    {
        await ArrangeTypeAsync();

        var dto = await _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice" });

        dto.DocumentTypeCode.ShouldBeNull();
        dto.TextOrigin.ShouldBe(DocumentTextOrigin.Submitted);
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(a => a.DocumentId == dto.Id), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentClassifiedEto>());
    }

    [Fact]
    public async Task Field_values_without_their_type_are_refused_before_anything_is_written()
    {
        await ArrangeTypeAsync();

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = "# Invoice",
            Fields = Fields(("total", "1200"))
        }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedFieldsNeedType);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task A_value_that_does_not_read_as_its_field_type_is_refused_before_anything_is_written()
    {
        await ArrangeTypeAsync();

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = "# Invoice",
            DocumentTypeCode = TypeCode,
            Fields = Fields(("total", "\"twelve hundred\""))
        }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.InvalidValue);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task A_type_code_of_no_type_is_not_found()
    {
        await ArrangeTypeAsync();

        await Should.ThrowAsync<Volo.Abp.Domain.Entities.EntityNotFoundException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice", DocumentTypeCode = "receipt" }));
    }

    [Fact]
    public async Task Markdown_with_no_text_is_refused()
    {
        await ArrangeTypeAsync();
        _extractionClient.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ExtractionDocument("empty"));

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "<!-- PageBreak -->" }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedMarkdownEmpty);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task The_same_text_submitted_again_is_refused_with_the_document_it_created()
    {
        await ArrangeTypeAsync();
        var first = await _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice", DocumentTypeCode = TypeCode });

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice", DocumentTypeCode = TypeCode }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedTextDuplicate);
        ex.Data["ExistingDocumentId"].ShouldBe(first.Id);
        await WithUnitOfWorkAsync(async () => (await _documentRepository.GetCountAsync()).ShouldBe(1));
    }

    [Fact]
    public async Task A_text_whose_document_is_in_the_recycle_bin_is_refused_as_such()
    {
        await ArrangeTypeAsync();
        var first = await _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice" });
        await _appService.DeleteAsync(first.Id);

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice" }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedTextInRecycleBin);
        ex.Data["ExistingDocumentId"].ShouldBe(first.Id);
    }

    [Fact]
    public async Task The_text_hash_is_the_stored_texts_and_its_lookup_is_scoped_to_the_creator()
    {
        await ArrangeTypeAsync();
        var dto = await _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Invoice" });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(dto.Id, includeDetails: false);
            var hash = ContentHasher.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(document.Markdown!));
            document.SubmittedTextHash.ShouldBe(hash);
            (await _documentRepository.FindBySubmittedTextHashAsync(hash, document.CreatorId))!.Id.ShouldBe(dto.Id);
            (await _documentRepository.FindBySubmittedTextHashAsync(hash, Guid.NewGuid())).ShouldBeNull();
        });
    }

    [Fact]
    public async Task Markdown_over_the_limit_is_refused_without_reading_it()
    {
        await ArrangeTypeAsync();

        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.SubmitAsync(new SubmitDocumentInput
        {
            Markdown = new string('a', DocumentConsts.MaxSubmittedMarkdownLength + 1)
        }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.SubmittedMarkdownTooLong);
        await _extractionClient.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default!, default!);
    }

    private async Task<Guid> ArrangeTypeAsync()
    {
        var typeId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await GetRequiredService<IRepository<DocumentType, Guid>>().InsertAsync(
                new DocumentType(typeId, tenantId: null, TypeCode, "Invoice"), autoSave: true);
            var fields = GetRequiredService<IFieldRepository>();
            await fields.InsertAsync(new Field(
                Guid.NewGuid(), tenantId: null, typeId, "invoice_no", "Invoice No", TextFieldType.ControlName,
                isRequired: true, isUniqueKey: true), autoSave: true);
            await fields.InsertAsync(new Field(
                Guid.NewGuid(), tenantId: null, typeId, "total", "Total", NumberFieldType.ControlName), autoSave: true);
        });
        return typeId;
    }

    private async Task NothingWrittenAsync()
    {
        await WithUnitOfWorkAsync(async () => (await _documentRepository.GetCountAsync()).ShouldBe(0));
        await _eventBus.DidNotReceiveWithAnyArgs().PublishAsync<DocumentUploadedEto>(default!);
    }

    private static Dictionary<string, JsonElement> Fields(params (string Name, string Json)[] values)
        => values.ToDictionary(v => v.Name, v => JsonDocument.Parse(v.Json).RootElement.Clone());

    private static ExtractionDocument TwoPages()
    {
        var document = new ExtractionDocument("submitted").SetProvider(MarkdownProvider);
        AddPage(document, 1, new ExtractionDocumentTextElement(new TextContent("Invoice"), ParseElementRoles.Heading) { HeadingLevel = 1 },
            new ExtractionDocumentTextElement(new TextContent("No. INV-7")));
        AddPage(document, 2, new ExtractionDocumentTextElement(new TextContent("Total 1200")));
        return document;
    }

    private static void AddPage(ExtractionDocument document, int number, params ExtractionDocumentElement[] elements)
    {
        var page = new ExtractionDocumentSourcePage("page-" + number, number);
        document.SourcePages.Add(page);
        var section = new ExtractionDocumentSection();
        section.Sources.Add(new ExtractionDocumentSourceReference(page));
        foreach (var element in elements)
        {
            element.Sources.Add(new ExtractionDocumentSourceReference(page));
            section.Elements.Add(element);
        }

        document.Sections.Add(section);
    }
}

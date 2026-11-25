using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dignite.Abp.BlobManagement.Files;
using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.Duplicates;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Layout;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Review;
using Dignite.Vault.Extract.FlexFields;
using Dignite.Vault.Parse;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Users;
using Volo.Abp.Clients;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;

namespace Dignite.Vault.Extract.Documents.Submissions;

/// <summary>
/// Turns a submission (<see cref="SubmitDocumentInput"/>) into a document: each pipeline stage the caller supplied is
/// completed here, in this unit of work, with what it supplied, and the stages it left out are queued as for an upload.
/// The document then lives the same lifecycle, under the same review reasons, behind the same integration events.
/// <list type="bullet">
/// <item>Text extraction: the Markdown is read the way Parse reads a Markdown file (through the router, so its page
/// breaks become the pages of the stored layout) and rendered back; that rendering is the stored text, as for every
/// parse. The title is the caller's or the first heading: no model call.</item>
/// <item>Classification: a declared type completes it as an upload's declared type does; none queues the classifier.</item>
/// <item>Field extraction: submitted values complete it with the checks the extraction write applies (required
/// fields, the duplicate fingerprint, the quoted sources located in the stored text); none queues the extractor.</item>
/// </list>
/// The caller (<c>DocumentAppService.SubmitAsync</c>) has authorized the request and resolved its type and cabinet.
/// </summary>
public class DocumentSubmissionService : ITransientDependency
{
    /// <summary>The file name the submitted Markdown is read under: it routes it to the Markdown engine.</summary>
    public const string SubmittedFileName = "submitted.md";

    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly DocumentExtractionClient _extractionClient;
    private readonly DocumentLayoutStore _layoutStore;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly ManualClassificationApplier _manualClassificationApplier;
    private readonly IFlexFieldIndexManager<Document> _flexFieldIndexManager;
    private readonly ReviewStateEvaluator _reviewEvaluator;
    private readonly DuplicateDetectionEvaluator _duplicateDetectionEvaluator;
    private readonly IVaultExtractFieldTypeRegistry _fieldTypeExtensionRegistry;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentClient _currentClient;
    private readonly ICancellationTokenProvider _cancellationTokenProvider;
    private readonly ICurrentUser _currentUser;
    private readonly IAbpLazyServiceProvider _lazyServiceProvider;

    public ILogger<DocumentSubmissionService> Logger { get; set; } = NullLogger<DocumentSubmissionService>.Instance;

    public DocumentSubmissionService(
        IDocumentRepository documentRepository,
        IFieldRepository fieldRepository,
        DocumentExtractionClient extractionClient,
        DocumentLayoutStore layoutStore,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        ManualClassificationApplier manualClassificationApplier,
        IFlexFieldIndexManager<Document> flexFieldIndexManager,
        ReviewStateEvaluator reviewEvaluator,
        DuplicateDetectionEvaluator duplicateDetectionEvaluator,
        IVaultExtractFieldTypeRegistry fieldTypeExtensionRegistry,
        IDistributedEventBus distributedEventBus,
        IBackgroundJobManager backgroundJobManager,
        IGuidGenerator guidGenerator,
        IClock clock,
        ICurrentTenant currentTenant,
        ICurrentClient currentClient,
        ICancellationTokenProvider cancellationTokenProvider,
        ICurrentUser currentUser,
        IAbpLazyServiceProvider lazyServiceProvider)
    {
        _currentUser = currentUser;
        _lazyServiceProvider = lazyServiceProvider;
        _documentRepository = documentRepository;
        _fieldRepository = fieldRepository;
        _extractionClient = extractionClient;
        _layoutStore = layoutStore;
        _pipelineRunManager = pipelineRunManager;
        _pipelineJobScheduler = pipelineJobScheduler;
        _manualClassificationApplier = manualClassificationApplier;
        _flexFieldIndexManager = flexFieldIndexManager;
        _reviewEvaluator = reviewEvaluator;
        _duplicateDetectionEvaluator = duplicateDetectionEvaluator;
        _fieldTypeExtensionRegistry = fieldTypeExtensionRegistry;
        _distributedEventBus = distributedEventBus;
        _backgroundJobManager = backgroundJobManager;
        _guidGenerator = guidGenerator;
        _clock = clock;
        _currentTenant = currentTenant;
        _currentClient = currentClient;
        _cancellationTokenProvider = cancellationTokenProvider;
    }

    /// <param name="input">The submission.</param>
    /// <param name="type">The declared type, resolved in the caller's layer; <c>null</c> when none was declared.</param>
    public virtual async Task<Document> SubmitAsync(SubmitDocumentInput input, DocumentType? type)
    {
        // Everything that can refuse the submission is checked before anything is written.
        if (input.Markdown != null && input.MarkdownFileId.HasValue)
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedMarkdownTwice);
        }

        var submittedMarkdown = input.MarkdownFileId.HasValue
            ? await ReadStagedMarkdownAsync(input.MarkdownFileId.Value)
            : input.Markdown;

        if (string.IsNullOrWhiteSpace(submittedMarkdown))
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedMarkdownEmpty);
        }

        if (submittedMarkdown.Length > DocumentConsts.MaxSubmittedMarkdownLength)
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedMarkdownTooLong)
                .WithData("MaxLength", DocumentConsts.MaxSubmittedMarkdownLength);
        }

        if (input.Fields != null && type == null)
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedFieldsNeedType);
        }

        List<Field>? definitions = null;
        Dictionary<string, object?>? values = null;
        if (input.Fields != null)
        {
            definitions = await _fieldRepository.GetListAsync(type!.Id);
            values = FlexFieldInputReader.Read(input.Fields, definitions, type.TypeCode, _fieldTypeExtensionRegistry);
        }

        var original = input.OriginalFileId.HasValue
            ? await ReadStagedOriginalAsync(input.OriginalFileId.Value)
            : null;

        var documentId = _guidGenerator.Create();
        var tree = await ReadMarkdownAsync(documentId, submittedMarkdown);
        var markdown = ExtractionDocumentMarkdown.Render(tree);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedMarkdownEmpty);
        }

        // The stored text's hash, so a line-ending or blank-line difference in a retry is the same text. Without an
        // original, it is the only thing a retry can be recognised by (an original is checked by its own hash above).
        var textHash = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(markdown));
        if (original == null)
        {
            var submitted = await _documentRepository.FindBySubmittedTextHashAsync(textHash, _currentUser.Id);
            if (submitted != null)
            {
                throw new BusinessException(submitted.IsDeleted
                        ? VaultExtractErrorCodes.Document.SubmittedTextInRecycleBin
                        : VaultExtractErrorCodes.Document.SubmittedTextDuplicate)
                    .WithData("ExistingDocumentId", submitted.Id);
            }
        }

        // The original is stored as an upload stores its file, a file of its own in the documents' container: the
        // staged copy is the submission's and goes once the document holds its own.
        FileOrigin? fileOrigin = null;
        if (original != null)
        {
            fileOrigin = new FileOrigin(
                await DocumentFiles.CreateAsync(original.FileName, original.Bytes, original.ContentHash),
                _currentUser.UserName ?? string.Empty,
                original.ContentType,
                original.ContentHash,
                original.Bytes.LongLength,
                originalFileName: original.FileName);
        }

        var document = new Document(documentId, _currentTenant.Id, fileOrigin, cabinetId: input.CabinetId);
        document.SetSubmittedTextHash(textHash);
        if (type != null)
        {
            _pipelineRunManager.DeclareDocumentType(document, type.Id);
        }

        await _documentRepository.InsertAsync(document, autoSave: true);

        // With no file, the same shape a sub-document's announcement has.
        await _distributedEventBus.PublishAsync(
            new DocumentUploadedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = _clock.Now,
                FileName = fileOrigin?.OriginalFileName,
                FileSize = fileOrigin?.FileSize ?? 0,
                ContentType = fileOrigin?.ContentType
            });

        await CompleteTextExtractionAsync(document, tree, markdown, input);

        if (type == null)
        {
            await _pipelineJobScheduler.QueueAsync(document, VaultExtractPipelines.Classification);
        }
        else if (values == null)
        {
            // Field extraction runs on the server, as for an upload that declared its type.
            await _manualClassificationApplier.ApplyAsync(document, type);
        }
        else
        {
            var fieldRun = await _manualClassificationApplier.ApplyWithFieldValuesAsync(document, type);
            await WriteFieldValuesAsync(document, definitions!, values, input.FieldSources, markdown);
            await _pipelineRunManager.CompleteAsync(document, fieldRun);
        }

        // As after a parse: the cabinet suggestion decides for itself whether the document still needs one.
        await _backgroundJobManager.EnqueueAsync(
            new DocumentCabinetSuggestionJobArgs { DocumentId = document.Id, TenantId = document.TenantId });

        await _documentRepository.UpdateAsync(document, autoSave: true);
        if (values != null)
        {
            await _flexFieldIndexManager.SynchronizeAsync(document);
        }

        // The staged uploads have been taken; Blob Management removes their bytes once this unit of work commits.
        foreach (var stagedId in new[] { input.MarkdownFileId, input.OriginalFileId })
        {
            if (stagedId.HasValue)
            {
                await FileService.DeleteAsync(stagedId.Value);
            }
        }

        Logger.LogInformation(
            "Document {DocumentId} submitted by client {ClientId}: type {TypeCode}, {FieldCount} field values, {PageCount} pages.",
            document.Id, _currentClient.Id, type?.TypeCode, values?.Count, tree.SourcePages.Count);

        return document;
    }

    /// <summary>
    /// A link that stages one file for a submission: in <see cref="DocumentSubmissionConsts.UploadContainerName"/>, for
    /// the current user, limited to what the submission will accept - a Markdown file's longest text, or an upload's
    /// largest file of an accepted type (by the content type detected from its bytes).
    /// </summary>
    public virtual async Task<SubmissionUploadDto> CreateUploadAsync(SubmissionFileKind kind)
    {
        var maxSize = kind == SubmissionFileKind.Markdown ? MaxMarkdownFileBytes : DocumentConsts.MaxUploadFileBytes;
        var contentTypes = kind == SubmissionFileKind.Markdown
            ? new List<string> { "text/markdown", "text/plain" }
            : DocumentConsts.AllowedUploadContentTypesByExtension.Values
                .SelectMany(types => types)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var ticket = await FileService.CreateUploadTicketAsync(new CreateBlobManagementUploadTicketInput
        {
            ContainerName = DocumentSubmissionConsts.UploadContainerName,
            MaxSize = maxSize,
            AllowedContentTypes = contentTypes,
            LifetimeInSeconds = DocumentSubmissionConsts.UploadLinkLifetimeSeconds
        });

        return new SubmissionUploadDto
        {
            FileId = ticket.FileId,
            UploadUrl = ticket.UploadUrl,
            ExpirationTime = ticket.ExpirationTime,
            MaxSize = maxSize
        };
    }

    /// <summary>The bytes the longest submittable Markdown can take in UTF-8, plus a byte order mark.</summary>
    protected static long MaxMarkdownFileBytes => (long)DocumentConsts.MaxSubmittedMarkdownLength * 4 + 3;

    /// <summary>Where the document's own file is stored.</summary>
    protected DocumentFileStore DocumentFiles => _lazyServiceProvider.LazyGetRequiredService<DocumentFileStore>();

    /// <summary>Blob Management's file service: resolved only when a submission stages or names a file.</summary>
    protected IBlobManagementFileService FileService => _lazyServiceProvider.LazyGetRequiredService<IBlobManagementFileService>();

    /// <summary>A staged Markdown file's text: UTF-8, a byte order mark skipped; anything else is refused.</summary>
    protected virtual async Task<string> ReadStagedMarkdownAsync(Guid fileId)
    {
        var (_, bytes) = await ReadStagedAsync(fileId, MaxMarkdownFileBytes, VaultExtractErrorCodes.Document.SubmittedMarkdownTooLong);
        var start = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        try
        {
            return StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedMarkdownNotUtf8);
        }
    }

    /// <summary>
    /// A staged original file, held to what an upload is held to: an accepted extension and content type (the type
    /// Blob Management detected from the bytes, not one a client declared), the size limit, and the uploader's own
    /// duplicates by content hash.
    /// </summary>
    protected virtual async Task<StagedOriginal> ReadStagedOriginalAsync(Guid fileId)
    {
        var (file, bytes) = await ReadStagedAsync(fileId, DocumentConsts.MaxUploadFileBytes, VaultExtractErrorCodes.Document.FileTooLarge);
        var extension = Path.GetExtension(file.Name);
        if (!DocumentConsts.IsAllowedUploadType(extension, file.MimeType))
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.UnsupportedFileType)
                .WithData("FileName", file.Name)
                .WithData("ContentType", file.MimeType);
        }

        // UploadAsync's rule (#655): the caller's own copy of these bytes is a duplicate, another user's is not.
        var contentHash = ContentHasher.Sha256Hex(bytes);
        var existing = await _documentRepository.FindByContentHashAsync(contentHash, _currentUser.Id);
        if (existing != null)
        {
            throw new BusinessException(existing.IsDeleted
                    ? VaultExtractErrorCodes.Document.InRecycleBin
                    : VaultExtractErrorCodes.Document.Duplicate)
                .WithData("FileName", file.Name)
                .WithData("ExistingDocumentId", existing.Id);
        }

        return new StagedOriginal(file.Name, file.MimeType, contentHash, bytes);
    }

    /// <summary>
    /// A staged file of the caller's: pending (not yet taken by a submission), in the submission container, and uploaded
    /// for the current user. Anything else gets one answer, not found, so an id discloses nothing about another user's
    /// uploads. Its bytes are read in full; the size is checked first.
    /// </summary>
    protected virtual async Task<(BlobManagementFileDto File, byte[] Bytes)> ReadStagedAsync(Guid fileId, long maxBytes, string tooLargeCode)
    {
        BlobManagementFileDto? file;
        try
        {
            file = await FileService.GetAsync(fileId);
        }
        catch (EntityNotFoundException)
        {
            file = null;
        }

        if (file is not { IsPending: true }
            || !string.Equals(file.ContainerName, DocumentSubmissionConsts.UploadContainerName, StringComparison.Ordinal)
            || file.CreatorId != _currentUser.Id)
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedFileNotFound).WithData("FileId", fileId);
        }

        if (file.Size > maxBytes)
        {
            throw new BusinessException(tooLargeCode)
                .WithData("FileName", file.Name)
                .WithData("MaxBytes", maxBytes)
                .WithData("MaxLength", DocumentConsts.MaxSubmittedMarkdownLength);
        }

        var content = await FileService.GetContentAsync(fileId);
        await using var stream = content.GetStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, _cancellationTokenProvider.Token);
        return (file, buffer.ToArray());
    }

    /// <summary>A staged original file, checked and ready to be stored.</summary>
    protected sealed record StagedOriginal(string FileName, string ContentType, string ContentHash, byte[] Bytes);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The submitted Markdown read as Parse reads a Markdown file: pages from its page breaks.</summary>
    protected virtual async Task<ExtractionDocument> ReadMarkdownAsync(Guid documentId, string markdown)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown), writable: false);
        return await _extractionClient.ExtractAsync(
            stream,
            documentId.ToString(),
            "text/markdown",
            ParseExtractionOptions.Create(SubmittedFileName),
            _cancellationTokenProvider.Token);
    }

    protected virtual async Task CompleteTextExtractionAsync(
        Document document, ExtractionDocument tree, string markdown, SubmitDocumentInput input)
    {
        var run = await _pipelineRunManager.StartAsync(document, VaultExtractPipelines.Parse);
        var title = string.IsNullOrWhiteSpace(input.Title) ? MarkdownTitleExtractor.ExtractTitle(markdown) : input.Title;
        await _pipelineRunManager.CompleteParseAsync(
            document, run, markdown, title,
            language: input.Language,
            extractionMetadata: new DocumentParseMetadata(
                tree.GetProvider(),
                tree.IsComplete(),
                tree.GetIncompleteReason(),
                DocumentTextOrigin.Submitted,
                _currentClient.Id));

        try
        {
            await _layoutStore.SaveAsync(document.Id, tree, markdown, _cancellationTokenProvider.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // As after a parse: the layout fails open, the text does not depend on it.
            Logger.LogWarning(ex, "Failed to store the layout of submitted document {DocumentId}; it is kept without one.", document.Id);
        }

        await _distributedEventBus.PublishAsync(
            new DocumentTextExtractedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = _clock.Now
            });
    }

    /// <summary>
    /// Writes the submitted values with the checks the extraction write applies to the values a model returned: the
    /// missing required fields, the duplicate fingerprint and verdict, and each value's quoted passages located in the
    /// stored text (at most as many per field as the extractor may quote).
    /// </summary>
    protected virtual async Task WriteFieldValuesAsync(
        Document document,
        IReadOnlyList<Field> definitions,
        Dictionary<string, object?> values,
        IReadOnlyDictionary<string, List<string>>? sources,
        string markdown)
    {
        document.SetFlexFields(values);

        if (sources is { Count: > 0 })
        {
            document.SetFieldSources(FieldSourceLocator.Locate(
                markdown,
                sources
                    .Where(pair => values.ContainsKey(pair.Key) && pair.Value is { Count: > 0 })
                    .ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<string>)pair.Value.Take(FieldExtractionWorkflow.MaxSourcesPerField).ToList(),
                        StringComparer.Ordinal)));
        }

        var requiredIds = definitions.Where(d => d.IsRequired).Select(d => d.Id).ToList();
        var presentIds = definitions.Where(d => document.FlexFields.ContainsKey(d.Name)).Select(d => d.Id).ToList();
        document.SetReviewReason(
            DocumentReviewReasons.MissingRequiredFields,
            _reviewEvaluator.MissingRequiredFieldsPresent(requiredIds, presentIds));

        document.SetFieldFingerprint(FlexFieldFingerprintCalculator.Compute(document, definitions, _fieldTypeExtensionRegistry));
        document.SetReviewReason(
            DocumentReviewReasons.DuplicateSuspected,
            await _duplicateDetectionEvaluator.EvaluateAsync(document, _cancellationTokenProvider.Token));
    }
}

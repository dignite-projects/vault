using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
        ICancellationTokenProvider cancellationTokenProvider)
    {
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
        if (string.IsNullOrWhiteSpace(input.Markdown))
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedMarkdownEmpty);
        }

        if (input.Markdown.Length > DocumentConsts.MaxSubmittedMarkdownLength)
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

        var documentId = _guidGenerator.Create();
        var tree = await ReadMarkdownAsync(documentId, input.Markdown);
        var markdown = ExtractionDocumentMarkdown.Render(tree);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new BusinessException(VaultExtractErrorCodes.Document.SubmittedMarkdownEmpty);
        }

        var document = new Document(documentId, _currentTenant.Id, fileOrigin: null, cabinetId: input.CabinetId);
        if (type != null)
        {
            _pipelineRunManager.DeclareDocumentType(document, type.Id);
        }

        await _documentRepository.InsertAsync(document, autoSave: true);

        // No file: the same shape a sub-document's announcement has.
        await _distributedEventBus.PublishAsync(
            new DocumentUploadedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = _clock.Now
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

        Logger.LogInformation(
            "Document {DocumentId} submitted by client {ClientId}: type {TypeCode}, {FieldCount} field values, {PageCount} pages.",
            document.Id, _currentClient.Id, type?.TypeCode, values?.Count, tree.SourcePages.Count);

        return document;
    }

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

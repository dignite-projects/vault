using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class ReclassifyReadyRefireTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The out-of-process collaborators are substituted; the lifecycle derivation, the local
        // DocumentLifecycleStatusChangedEvent and DocumentReadyEventHandler all run for real on UoW completion.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Reclassifying a Ready document must withdraw Ready and announce it again under the new type once field extraction
/// has re-run. A consumer that keys derived data and its read filtering by type (Vault Index) learns the new type only
/// from that second <see cref="DocumentReadyEto"/>, and until then must not be told the document is Ready under either
/// type. This was read from code comments when Vault Index was designed and is pinned here end to end.
/// </summary>
public class ReclassifyReadyRefire_Tests : VaultExtractTestBase<ReclassifyReadyRefireTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineRunAccessor _pipelineRunAccessor;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;

    public ReclassifyReadyRefire_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _pipelineRunAccessor = GetRequiredService<DocumentPipelineRunAccessor>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Reclassifying_a_Ready_document_withdraws_Ready_and_re_fires_it_under_the_new_type()
    {
        var typeA = _guidGenerator.Create();
        var typeB = _guidGenerator.Create();
        var documentId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentTypeRepository.InsertAsync(new DocumentType(typeA, null, "type.a", "Type A"), autoSave: true);
            await _documentTypeRepository.InsertAsync(new DocumentType(typeB, null, "type.b", "Type B"), autoSave: true);

            var doc = new Document(documentId, null, DocumentTestData.NewFileOrigin(documentId));
            doc.SetMarkdown("# Body");
            doc.ApplyAutomaticClassificationResult(typeA, 0.99);
            await _documentRepository.InsertAsync(doc, autoSave: true);

            foreach (var pipelineCode in VaultExtractPipelines.KeyPipelines)
            {
                var run = await _pipelineRunManager.StartAsync(doc, pipelineCode);
                await _pipelineRunManager.CompleteAsync(doc, run);
            }

            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        (await ReloadAsync(documentId)).LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentReadyEto>(e => e.DocumentId == documentId && e.DocumentTypeCode == "type.a"),
            Arg.Any<bool>(),
            Arg.Any<bool>());
        _eventBus.ClearReceivedCalls();

        await _appService.ReclassifyAsync(documentId, new ReclassifyDocumentInput { DocumentTypeId = typeB });

        // Out of Ready at once, under the new type, with field extraction queued: nothing announces Ready yet.
        var reclassified = await ReloadAsync(documentId);
        reclassified.DocumentTypeId.ShouldBe(typeB);
        reclassified.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentReadyEto>(), Arg.Any<bool>(), Arg.Any<bool>());

        var jobArgs = _backgroundJobManager.ReceivedCalls()
            .Select(call => call.GetArguments().FirstOrDefault())
            .OfType<DocumentFieldExtractionJobArgs>()
            .Single(args => args.DocumentId == documentId);

        // The field-extraction job's run bookkeeping, without its LLM call.
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(documentId);
            var run = await _pipelineRunAccessor.BeginOrStartAsync(
                doc, jobArgs.PipelineRunId, VaultExtractPipelines.FieldExtraction);
            await _pipelineRunManager.CompleteAsync(doc, run.ShouldNotBeNull());
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        (await ReloadAsync(documentId)).LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentReadyEto>(e => e.DocumentId == documentId && e.DocumentTypeCode == "type.b"),
            Arg.Any<bool>(),
            Arg.Any<bool>());
    }

    private async Task<Document> ReloadAsync(Guid id)
    {
        Document? document = null;
        await WithUnitOfWorkAsync(async () => document = await _documentRepository.GetAsync(id));
        return document!;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Integration;
using Shouldly;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// <see cref="IDocumentIntegrationService"/> against the real database, because both of its boundaries are query
/// filters a substituted repository cannot exercise: the Ready-for-consumers predicate and the ambient
/// <c>IMultiTenant</c> / <c>ISoftDelete</c> filters. Every document seeded here except two is withheld for a
/// different reason, so each term of the predicate and each filter has a row that only it excludes.
/// </summary>
public class DocumentIntegrationService_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private static readonly Guid TypeId = DocumentTestData.DeterministicGuid("integration:type");
    private static readonly Guid FieldId = DocumentTestData.DeterministicGuid("integration:field");
    private static readonly Guid UploaderId = DocumentTestData.DeterministicGuid("integration:uploader");
    private static readonly Guid OtherTenantId = DocumentTestData.DeterministicGuid("integration:tenant");

    private readonly IDocumentIntegrationService _integrationService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly ICurrentTenant _currentTenant;

    public DocumentIntegrationService_Tests()
    {
        _integrationService = GetRequiredService<IDocumentIntegrationService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task FindReadyAsync_returns_a_Ready_document_with_what_a_consumer_derives_from()
    {
        var seeded = await SeedAsync();

        var dto = await _integrationService.FindReadyAsync(seeded.Ready);

        dto.ShouldNotBeNull();
        dto.Id.ShouldBe(seeded.Ready);
        dto.TenantId.ShouldBeNull();
        dto.DocumentTypeId.ShouldBe(TypeId);
        dto.OwnerId.ShouldBe(UploaderId);
        dto.Title.ShouldBe("Invoice 001");
        dto.Language.ShouldBe("ja");
        dto.Markdown.ShouldBe("# Invoice 001\n\nbody");
        dto.ExtractedFields.ShouldNotBeNull();
        dto.ExtractedFields!["invoice_no"].GetString().ShouldBe("INV-001");
    }

    [Fact]
    public async Task FindReadyAsync_withholds_every_document_that_is_not_Ready_for_consumers()
    {
        var seeded = await SeedAsync();

        (await _integrationService.FindReadyAsync(seeded.Processing)).ShouldBeNull("not Ready yet");
        (await _integrationService.FindReadyAsync(seeded.Container)).ShouldBeNull("a container is never announced");
        (await _integrationService.FindReadyAsync(seeded.Untyped)).ShouldBeNull("Ready with no type breaks the gate's invariant");
        (await _integrationService.FindReadyAsync(seeded.Deleted)).ShouldBeNull("in the recycle bin");
        (await _integrationService.FindReadyAsync(seeded.OtherTenant)).ShouldBeNull("another tenant's document");
        (await _integrationService.FindReadyAsync(Guid.NewGuid())).ShouldBeNull("unknown id");
    }

    [Fact]
    public async Task FindReadyAsync_reads_under_the_ambient_tenant()
    {
        var seeded = await SeedAsync();

        using (_currentTenant.Change(OtherTenantId))
        {
            (await _integrationService.FindReadyAsync(seeded.OtherTenant)).ShouldNotBeNull();
            (await _integrationService.FindReadyAsync(seeded.Ready)).ShouldBeNull("a host document, seen from a tenant");
        }
    }

    [Fact]
    public async Task GetReadyIdsAsync_walks_exactly_the_Ready_documents_by_keyset()
    {
        var seeded = await SeedAsync();

        var collected = new List<Guid>();
        Guid? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var page = await _integrationService.GetReadyIdsAsync(
                new GetReadyDocumentIdsInput { AfterId = cursor, MaxResultCount = 1 });
            if (page.Items.Count == 0)
            {
                break;
            }

            page.Items.Count.ShouldBe(1);
            collected.AddRange(page.Items);
            cursor = page.Items[^1];
        }

        collected.ShouldBe(new[] { seeded.Ready, seeded.SecondReady }, ignoreOrder: true);
        collected.Distinct().Count().ShouldBe(collected.Count);
    }

    /// <summary>
    /// The reason for a keyset cursor rather than an offset: a document that leaves the set between two pages must
    /// not make the walk skip one that stays. Here the first page's document is deleted before the second page is
    /// asked for; an offset of 1 would then land past the remaining document.
    /// </summary>
    [Fact]
    public async Task GetReadyIdsAsync_does_not_skip_a_document_when_an_earlier_one_leaves_the_set()
    {
        var seeded = await SeedAsync();

        var first = await _integrationService.GetReadyIdsAsync(new GetReadyDocumentIdsInput { MaxResultCount = 1 });
        first.Items.Count.ShouldBe(1);

        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(first.Items[0]));

        var second = await _integrationService.GetReadyIdsAsync(
            new GetReadyDocumentIdsInput { AfterId = first.Items[0], MaxResultCount = 1 });

        var remaining = first.Items[0] == seeded.Ready ? seeded.SecondReady : seeded.Ready;
        second.Items.ShouldBe(new[] { remaining });
    }

    private async Task<SeededIds> SeedAsync()
    {
        var seeded = new SeededIds(
            Ready: DocumentTestData.DeterministicGuid("integration:ready"),
            SecondReady: DocumentTestData.DeterministicGuid("integration:ready-2"),
            Processing: DocumentTestData.DeterministicGuid("integration:processing"),
            Container: DocumentTestData.DeterministicGuid("integration:container"),
            Untyped: DocumentTestData.DeterministicGuid("integration:untyped"),
            Deleted: DocumentTestData.DeterministicGuid("integration:deleted"),
            OtherTenant: DocumentTestData.DeterministicGuid("integration:other-tenant"));

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(TypeId, null, "host.invoice", "Invoice"), autoSave: true);
            await _fieldRepository.InsertAsync(new Field(
                FieldId, null, TypeId,
                name: "invoice_no", displayName: "Invoice No", fieldTypeName: "Text"), autoSave: true);

            var ready = NewDocument(seeded.Ready, null, TypeId, DocumentLifecycleStatus.Ready);
            ready.SetField("invoice_no", "INV-001");
            await _documentRepository.InsertAsync(ready, autoSave: true);

            await _documentRepository.InsertAsync(
                NewDocument(seeded.SecondReady, null, TypeId, DocumentLifecycleStatus.Ready), autoSave: true);
            await _documentRepository.InsertAsync(
                NewDocument(seeded.Processing, null, TypeId, DocumentLifecycleStatus.Processing), autoSave: true);

            // A container reaches Ready lifecycle and its event is suppressed. A real one has no type; this one is given
            // a type so that only the container term withholds it, and the untyped row below covers the type term.
            var container = NewDocument(seeded.Container, null, TypeId, DocumentLifecycleStatus.Ready);
            typeof(Document).GetProperty(nameof(Document.IsContainer))!.SetValue(container, true);
            await _documentRepository.InsertAsync(container, autoSave: true);

            // Not a container, Ready, no type: a row that broke the gate's invariant. Only the type term withholds it.
            await _documentRepository.InsertAsync(
                NewDocument(seeded.Untyped, null, null, DocumentLifecycleStatus.Ready), autoSave: true);

            var deleted = NewDocument(seeded.Deleted, null, TypeId, DocumentLifecycleStatus.Ready);
            deleted.IsDeleted = true;
            await _documentRepository.InsertAsync(deleted, autoSave: true);

            await _documentRepository.InsertAsync(
                NewDocument(seeded.OtherTenant, OtherTenantId, TypeId, DocumentLifecycleStatus.Ready), autoSave: true);
        });

        return seeded;
    }

    private static Document NewDocument(Guid id, Guid? tenantId, Guid? typeId, DocumentLifecycleStatus status)
    {
        var document = new Document(id, tenantId, DocumentTestData.NewFileOrigin(id));
        document.SetMarkdown(id == DocumentTestData.DeterministicGuid("integration:ready")
            ? "# Invoice 001\n\nbody"
            : "# Other\n\nbody");
        DocumentTestData.SetTitle(document, "Invoice 001");
        typeof(Document).GetProperty(nameof(Document.Language))!.SetValue(document, "ja");
        typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, UploaderId);

        if (typeId.HasValue)
        {
            DocumentTestData.MarkClassified(document, typeId.Value);
        }

        DocumentTestData.SetLifecycleStatus(document, status);
        return document;
    }

    private sealed record SeededIds(
        Guid Ready,
        Guid SecondReady,
        Guid Processing,
        Guid Container,
        Guid Untyped,
        Guid Deleted,
        Guid OtherTenant);
}

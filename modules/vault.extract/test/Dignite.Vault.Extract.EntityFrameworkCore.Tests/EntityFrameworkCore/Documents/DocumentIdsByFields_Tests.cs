using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// <see cref="IDocumentAppService.GetIdsByFieldsAsync"/> over real EF (SQLite) and the field index: the ids of the
/// Ready documents of a type whose fields meet the conditions, for Vault Index's field filters.
/// </summary>
public class DocumentIdsByFields_Tests : VaultExtractTestBase<DocumentIdsByFieldsTestModule>
{
    private const string TypeCode = "invoice.general";

    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly Dignite.Abp.FlexFields.IFlexFieldIndexManager<Document> _indexManager;
    private readonly ICurrentTenant _currentTenant;

    public DocumentIdsByFields_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _indexManager = GetRequiredService<Dignite.Abp.FlexFields.IFlexFieldIndexManager<Document>>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task Returns_the_ready_documents_whose_fields_match_in_ascending_order()
    {
        var ids = new List<Guid>();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            ids.Add(await SeedDocumentAsync(100m));
            ids.Add(await SeedDocumentAsync(100m));
            await SeedDocumentAsync(200m);
            // Matches, but is not released: not something a consumer may build on.
            await SeedDocumentAsync(100m, DocumentLifecycleStatus.PendingReview);
        });

        var result = await _appService.GetIdsByFieldsAsync(Input("100"));

        result.Ids.ShouldBe(ids.OrderBy(id => id).ToList());
        result.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_documents_in_the_callers_read_scope_are_returned()
    {
        var id = Guid.Empty;
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            id = await SeedDocumentAsync(100m);
        });
        var readScope = GetRequiredService<ReadScopeOverride>();

        readScope.Scope = DocumentAccessScope.Of(new HashSet<Guid> { Guid.NewGuid() }, ownerId: null);
        (await _appService.GetIdsByFieldsAsync(Input("100"))).Ids.ShouldBeEmpty();

        readScope.Scope = DocumentAccessScope.Of(new HashSet<Guid> { TypeId }, ownerId: null);
        (await _appService.GetIdsByFieldsAsync(Input("100"))).Ids.ShouldBe([id]);
    }

    [Fact]
    public async Task Says_when_more_matched_than_it_returned()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            for (var i = 0; i < 3; i++)
            {
                await SeedDocumentAsync(100m);
            }
        });

        var result = await _appService.GetIdsByFieldsAsync(Input("100", maxResultCount: 2));

        result.Ids.Count.ShouldBe(2);
        result.HasMore.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_field_or_type_fails_loudly()
    {
        await WithUnitOfWorkAsync(SeedSchemaAsync);

        (await Should.ThrowAsync<BusinessException>(() => _appService.GetIdsByFieldsAsync(new GetDocumentIdsByFieldsInput
        {
            DocumentTypeCode = TypeCode,
            FieldFilters = [new DocumentFieldFilter { Name = "ghost", Value = "x" }]
        }))).Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.Unknown);

        (await Should.ThrowAsync<BusinessException>(() => _appService.GetIdsByFieldsAsync(new GetDocumentIdsByFieldsInput
        {
            DocumentTypeCode = "no.such.type",
            FieldFilters = [new DocumentFieldFilter { Name = "amount", Value = "1" }]
        }))).Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.Unknown);
    }

    [Fact]
    public async Task At_least_one_condition_is_required()
    {
        await Should.ThrowAsync<AbpValidationException>(() => _appService.GetIdsByFieldsAsync(new GetDocumentIdsByFieldsInput
        {
            DocumentTypeCode = TypeCode,
            FieldFilters = []
        }));
    }

    private static GetDocumentIdsByFieldsInput Input(string amount, int maxResultCount = DocumentConsts.MaxFieldMatchedDocumentIds) => new()
    {
        DocumentTypeCode = TypeCode,
        FieldFilters = [new DocumentFieldFilter { Name = "amount", Value = amount }],
        MaxResultCount = maxResultCount
    };

    private async Task SeedSchemaAsync()
    {
        await _documentTypeRepository.InsertAsync(new DocumentType(TypeId, _currentTenant.Id, TypeCode, TypeCode), autoSave: true);
        await _fieldRepository.InsertAsync(
            new Field(
                AmountFieldId, _currentTenant.Id, TypeId,
                name: "amount", displayName: "Amount",
                fieldTypeName: Dignite.Abp.FlexFields.Number.NumberFieldType.ControlName),
            autoSave: true);
    }

    private async Task<Guid> SeedDocumentAsync(decimal amount, DocumentLifecycleStatus status = DocumentLifecycleStatus.Ready)
    {
        var id = Guid.NewGuid();
        var document = new Document(id, _currentTenant.Id, DocumentTestData.NewFileOrigin(id));
        DocumentTestData.MarkClassified(document, TypeId);
        DocumentTestData.SetLifecycleStatus(document, status);
        document.SetFlexFields(new Dictionary<string, object?> { ["amount"] = amount });
        await _documentRepository.InsertAsync(document, autoSave: true);
        await _indexManager.SynchronizeAsync(document);
        return id;
    }

    private static Guid TypeId => DocumentTestData.DeterministicGuid("type:" + TypeCode);
    private static Guid AmountFieldId => DocumentTestData.DeterministicGuid("field:amount");
}

/// <summary>
/// The EF test module, plus a stub for the document blob container the application service takes but this path never
/// touches, and a read scope a test can pin.
/// </summary>
[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentIdsByFieldsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton<ReadScopeOverride>();
        context.Services.Replace(ServiceDescriptor.Transient<DocumentAccessChecker, PinnedScopeDocumentAccessChecker>());
    }
}

/// <summary>The read scope <see cref="PinnedScopeDocumentAccessChecker"/> answers with; <c>null</c> for the real one.</summary>
public sealed class ReadScopeOverride
{
    public DocumentAccessScope? Scope { get; set; }
}

[DisableConventionalRegistration]
public class PinnedScopeDocumentAccessChecker : DocumentAccessChecker
{
    private readonly ReadScopeOverride _override;

    public PinnedScopeDocumentAccessChecker(
        Volo.Abp.Users.ICurrentUser currentUser,
        DocumentAccessMemo accessMemo,
        ReadScopeOverride scopeOverride)
        : base(currentUser, accessMemo)
    {
        _override = scopeOverride;
    }

    public override Task<DocumentAccessScope> ResolveScopeAsync(DocumentAccessRule rule)
        => _override.Scope is { } scope ? Task.FromResult(scope) : base.ResolveScopeAsync(rule);
}

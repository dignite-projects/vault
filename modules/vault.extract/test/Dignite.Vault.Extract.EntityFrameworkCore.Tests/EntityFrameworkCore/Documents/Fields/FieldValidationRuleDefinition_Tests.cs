using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Table;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents.Fields;

/// <summary>
/// A field's validation rules through <see cref="IFieldDefinitionAppService"/>, stored in the real SQLite database: what
/// is checked before they are saved, that an input stays bound to its field by id, and that a client which does not know
/// about rules cannot erase them by saving a field.
/// </summary>
public class FieldValidationRuleDefinition_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private readonly IFieldDefinitionAppService _fieldAppService;
    private readonly IDocumentTypeAppService _typeAppService;
    private readonly IFieldRepository _fieldRepository;

    public FieldValidationRuleDefinition_Tests()
    {
        _fieldAppService = GetRequiredService<IFieldDefinitionAppService>();
        _typeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
    }

    [Fact]
    public async Task Rules_are_stored_with_each_input_bound_to_its_field_and_read_back()
    {
        var (typeId, opening) = await ArrangeTypeWithOpeningAsync();

        var created = await _fieldAppService.CreateAsync(TransactionsInput(typeId, Rule(
            "第一行余额等于期初余额", "value[0].values.balance === opening", ("opening", opening.Id))));

        created.ValidationRules.Single().Inputs.Single().FieldId.ShouldBe(opening.Id);
        await WithUnitOfWorkAsync(async () =>
        {
            var stored = await _fieldRepository.GetAsync(created.Id);
            stored.ValidationRules.Single().Description.ShouldBe("第一行余额等于期初余额");
            stored.ValidationRules.Single().Expression.ShouldBe("value[0].values.balance === opening");
            stored.ValidationRules.Single().Inputs.Single().ShouldBe(new FieldValidationRuleInput("opening", opening.Id));
        });
    }

    [Fact]
    public async Task A_field_saved_before_rules_existed_reads_as_having_none()
    {
        var (typeId, opening) = await ArrangeTypeWithOpeningAsync();

        await WithUnitOfWorkAsync(async () =>
            (await _fieldRepository.GetAsync(opening.Id)).ValidationRules.ShouldBeEmpty());
        (await GetFieldAsync(typeId, opening.Id)).ValidationRules.ShouldBeEmpty();
    }

    [Fact]
    public async Task Renaming_the_field_an_input_reads_leaves_the_rule_bound_to_it()
    {
        var (typeId, opening) = await ArrangeTypeWithOpeningAsync();
        var created = await _fieldAppService.CreateAsync(TransactionsInput(typeId, Rule(
            "第一行余额等于期初余额", "value[0].values.balance === opening", ("opening", opening.Id))));

        await _fieldAppService.UpdateAsync(opening.Id, UpdateInput(opening, name: "startBalance"));

        (await GetFieldAsync(typeId, created.Id)).ValidationRules.Single().Inputs.Single().FieldId.ShouldBe(opening.Id);
    }

    [Fact]
    public async Task An_update_without_rules_keeps_them_and_an_empty_list_removes_them()
    {
        var (typeId, opening) = await ArrangeTypeWithOpeningAsync();
        var created = await _fieldAppService.CreateAsync(TransactionsInput(typeId, Rule("r", "value.length > 0")));

        var kept = await _fieldAppService.UpdateAsync(created.Id, UpdateInput(created, rules: null));
        kept.ValidationRules.Count.ShouldBe(1);

        var cleared = await _fieldAppService.UpdateAsync(created.Id, UpdateInput(created, rules: []));
        cleared.ValidationRules.ShouldBeEmpty();
        await WithUnitOfWorkAsync(async () => (await _fieldRepository.GetAsync(created.Id)).ValidationRules.ShouldBeEmpty());
    }

    [Fact]
    public async Task An_input_that_is_not_a_field_of_the_same_type_is_refused_and_nothing_is_saved()
    {
        var (typeId, _) = await ArrangeTypeWithOpeningAsync();
        var (_, foreign) = await ArrangeTypeWithOpeningAsync();

        var ex = await Should.ThrowAsync<BusinessException>(() => _fieldAppService.CreateAsync(TransactionsInput(typeId,
            Rule("r", "value.length > 0 && other > 0", ("other", foreign.Id)))));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.InvalidValidationRuleInput);
        (await _fieldAppService.GetListAsync(new GetFieldDefinitionListInput { DocumentTypeId = typeId }))
            .ShouldNotContain(f => f.Name == "transactions");
    }

    [Fact]
    public async Task An_expression_that_does_not_parse_is_refused_on_update_and_the_rules_stay_as_they_were()
    {
        var (typeId, _) = await ArrangeTypeWithOpeningAsync();
        var created = await _fieldAppService.CreateAsync(TransactionsInput(typeId, Rule("r", "value.length > 0")));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _fieldAppService.UpdateAsync(created.Id, UpdateInput(created, rules: [Rule("r", "value.length >")])));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.UnparsableValidationRule);
        await WithUnitOfWorkAsync(async () =>
            (await _fieldRepository.GetAsync(created.Id)).ValidationRules.Single().Expression.ShouldBe("value.length > 0"));
    }

    [Fact]
    public async Task A_field_another_fields_rule_reads_cannot_be_deleted_until_that_rule_changes()
    {
        var (typeId, opening) = await ArrangeTypeWithOpeningAsync();
        var created = await _fieldAppService.CreateAsync(TransactionsInput(typeId, Rule(
            "第一行余额等于期初余额", "value[0].values.balance === opening", ("opening", opening.Id))));

        var ex = await Should.ThrowAsync<BusinessException>(() => _fieldAppService.DeleteAsync(opening.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.ReadByValidationRule);
        ex.Data["ReadBy"].ShouldBe("transactions");
        (await GetFieldAsync(typeId, opening.Id)).ShouldNotBeNull();

        await _fieldAppService.UpdateAsync(created.Id, UpdateInput(created, rules: []));
        await _fieldAppService.DeleteAsync(opening.Id);
        (await _fieldAppService.GetListAsync(new GetFieldDefinitionListInput { DocumentTypeId = typeId }))
            .ShouldNotContain(f => f.Id == opening.Id);
    }

    // ─── helpers ───

    private async Task<FieldDefinitionDto> GetFieldAsync(Guid typeId, Guid fieldId)
        => (await _fieldAppService.GetListAsync(new GetFieldDefinitionListInput { DocumentTypeId = typeId })).Single(f => f.Id == fieldId);

    private async Task<(Guid TypeId, FieldDefinitionDto Opening)> ArrangeTypeWithOpeningAsync()
    {
        var type = await _typeAppService.CreateAsync(new CreateDocumentTypeDto
        {
            TypeCode = $"host.rules-{Guid.NewGuid():N}",
            DisplayName = "Bank statement"
        });
        var opening = await _fieldAppService.CreateAsync(new CreateFieldDefinitionDto
        {
            DocumentTypeId = type.Id,
            Name = "openingBalance",
            DisplayName = "Opening balance",
            FieldTypeName = NumberFieldType.ControlName
        });
        return (type.Id, opening);
    }

    private static CreateFieldDefinitionDto TransactionsInput(Guid typeId, params FieldValidationRuleDto[] rules) => new()
    {
        DocumentTypeId = typeId,
        Name = "transactions",
        DisplayName = "Transactions",
        FieldTypeName = TableFieldType.ControlName,
        Configuration = new TableConfiguration
        {
            Columns = [new() { Name = "balance", DisplayName = "Balance", FieldTypeName = NumberFieldType.ControlName }]
        }.ConfigurationDictionary,
        IsSearchable = false,
        ValidationRules = rules.ToList()
    };

    private static UpdateFieldDefinitionDto UpdateInput(FieldDefinitionDto field, string? name = null, List<FieldValidationRuleDto>? rules = null) => new()
    {
        Name = name ?? field.Name,
        DisplayName = field.DisplayName,
        Description = field.Description,
        FieldTypeName = field.FieldTypeName,
        Configuration = field.Configuration,
        DisplayOrder = field.DisplayOrder,
        IsRequired = field.IsRequired,
        IsSearchable = field.IsSearchable,
        IsUniqueKey = field.IsUniqueKey,
        ValidationRules = rules
    };

    private static FieldValidationRuleDto Rule(string description, string expression, params (string Alias, Guid FieldId)[] inputs) => new()
    {
        Description = description,
        Expression = expression,
        Inputs = inputs.Select(input => new FieldValidationRuleInputDto { Alias = input.Alias, FieldId = input.FieldId }).ToList()
    };
}

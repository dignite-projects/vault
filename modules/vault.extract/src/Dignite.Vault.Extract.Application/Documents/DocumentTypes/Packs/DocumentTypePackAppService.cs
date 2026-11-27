using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.FlexFields;
using Dignite.Vault.Extract.Documents.Duplicates;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Fields.Cleanup;
using Dignite.Vault.Extract.Documents.Fields.Validation;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>
/// Config import/export "pack" engine (#444). Serializes a <see cref="DocumentType"/> + its
/// <see cref="Field"/>s to a portable declarative pack and applies a pack back idempotently.
/// <para>
/// Everything goes through the domain entities + managers (never a raw DbContext), so every invariant holds:
/// code/name layer-uniqueness (<see cref="DocumentTypeManager"/> / <see cref="FieldDefinitionManager"/>),
/// entity validation (name pattern, lengths), and the data-safety guard that forbids changing a field's
/// type once extracted values exist.
/// </para>
/// <para>
/// Layer-aware: reads and writes only the caller's current layer (Host = <c>TenantId</c> null, tenant = its
/// GUID) via the ambient <c>IMultiTenant</c> filter + <c>CurrentTenant.Id</c>; there is no cross-layer
/// mixing. Identity is <c>TypeCode</c> / field <c>Name</c> (#207: a rename = a new type/field). Import is
/// atomic: it runs in the ambient application-service unit of work, and all pack versions are validated
/// before any write, so an unsupported version leaves nothing partially applied.
/// </para>
/// </summary>
public class DocumentTypePackAppService : VaultExtractAppService, IDocumentTypePackAppService
{
    /// <summary>Mirror of <c>FieldDefinitionAppService.ColumnNameRegex</c> — see that field's own doc for why a column <c>Name</c> needs the same prompt-injection allow-list a top-level <c>Field.Name</c> already has.</summary>
    private static readonly Regex ColumnNameRegex = new(
        FieldDefinitionConsts.NamePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldDefinitionRepository;
    private readonly IFieldTypeResolver _fieldTypeResolver;
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentTypeManager _documentTypeManager;
    private readonly FieldDefinitionManager _fieldDefinitionManager;
    private readonly FieldSchemaPromptBudgetGuard _schemaPromptBudget;

    /// <summary>See <see cref="FieldDefinitionAppService"/>'s field of the same name — same reason: a searchability flip is a complete reindex, not a per-row patch.</summary>
    private readonly IFlexFieldIndexManager<Document> _indexManager;

    private readonly IVaultExtractFieldTypeRegistry _fieldTypeExtensionRegistry;

    /// <summary>
    /// #651: a pack import is a second save path for <see cref="DocumentType.DuplicateScope"/>, so it owes the
    /// same reconciliation enqueue <c>DocumentTypeAppService.UpdateAsync</c> owes. Both now go through the one
    /// helper that carries that obligation, rather than restating it — this path was missing it entirely until
    /// someone went looking, which is the argument against restating it a third time.
    /// </summary>
    private readonly DocumentTypeUpdater _documentTypeUpdater;

    protected IBackgroundJobManager BackgroundJobManager => LazyServiceProvider.LazyGetRequiredService<IBackgroundJobManager>();

    public DocumentTypePackAppService(
        IDocumentTypeRepository documentTypeRepository,
        IFieldRepository fieldDefinitionRepository,
        IFieldTypeResolver fieldTypeResolver,
        IDocumentRepository documentRepository,
        DocumentTypeManager documentTypeManager,
        FieldDefinitionManager fieldDefinitionManager,
        FieldSchemaPromptBudgetGuard schemaPromptBudget,
        IFlexFieldIndexManager<Document> indexManager,
        IVaultExtractFieldTypeRegistry fieldTypeExtensionRegistry,
        DocumentTypeUpdater documentTypeUpdater)
    {
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _fieldTypeResolver = fieldTypeResolver;
        _documentRepository = documentRepository;
        _documentTypeManager = documentTypeManager;
        _fieldDefinitionManager = fieldDefinitionManager;
        _schemaPromptBudget = schemaPromptBudget;
        _indexManager = indexManager;
        _fieldTypeExtensionRegistry = fieldTypeExtensionRegistry;
        _documentTypeUpdater = documentTypeUpdater;
    }

    [Authorize(VaultExtractPermissions.DocumentTypes.Default)]
    public virtual async Task<DocumentTypePackDto> ExportAsync(Guid id)
    {
        var type = await _documentTypeRepository.GetAsync(id);
        // Defense in depth on top of the IMultiTenant filter: never export another layer's type.
        if (type.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(DocumentType), id);
        }

        var fields = await _fieldDefinitionRepository.GetListAsync(type.Id);
        return MapToPack(type, fields);
    }

    [Authorize(VaultExtractPermissions.DocumentTypes.Default)]
    public virtual async Task<List<DocumentTypePackDto>> ExportAllAsync()
    {
        // The ambient IMultiTenant filter narrows this to the caller's layer.
        var types = await _documentTypeRepository.GetListAsync();
        var packs = new List<DocumentTypePackDto>(types.Count);
        foreach (var type in types.OrderBy(t => t.Priority).ThenBy(t => t.TypeCode))
        {
            var fields = await _fieldDefinitionRepository.GetListAsync(type.Id);
            packs.Add(MapToPack(type, fields));
        }

        return packs;
    }

    // Import always may create types + fields, so both Create permissions gate entry. The Update permissions
    // are asserted lazily, only on the branches that actually update an existing type / field (ImportPackAsync
    // / ImportFieldsAsync), so a CreateOnly import — or a first-time import of all-new types/fields — never
    // demands Update. This is not an LLM-influenced path, so [Authorize] fires normally.
    [Authorize(VaultExtractPermissions.DocumentTypes.Create)]
    [Authorize(VaultExtractPermissions.FieldDefinitions.Create)]
    public virtual async Task<DocumentTypePackImportResultDto> ImportAsync(ImportDocumentTypePacksInput input)
    {
        // Validate every pack version up front, before touching the store, so an unsupported version can
        // never leave earlier packs partially applied (the production UoW would roll back anyway, but the
        // test harness disables the transaction — pre-validation makes the guarantee unconditional).
        foreach (var pack in input.Packs)
        {
            if (pack.Version < DocumentTypePackConsts.MinSupportedVersion ||
                pack.Version > DocumentTypePackConsts.CurrentVersion)
            {
                throw new BusinessException(VaultExtractErrorCodes.DocumentTypePack.UnsupportedVersion)
                    .WithData("TypeCode", pack.TypeCode)
                    .WithData("Version", pack.Version)
                    .WithData("Supported", DocumentTypePackConsts.CurrentVersion);
            }

            // Upconvert before anything else reads a field, so the budget validation below and the import
            // itself both see one shape. A version-1 pack reaching ImportFieldsAsync unconverted would
            // create every field as the default Text type, silently discarding its declared types.
            if (pack.Version < DocumentTypePackConsts.CurrentVersion)
            {
                foreach (var field in pack.Fields)
                {
                    DocumentTypePackV1Upconverter.Upconvert(field);
                }
            }
        }

        // Validate every projected type schema before touching the store. This is aggregate-wide (not one DTO
        // attribute per field) and simulates repeated packs in request order, preserving the method's unconditional
        // no-partial-write guarantee even in the non-transactional test harness.
        await ValidateSchemaPromptBudgetsAsync(input.Packs, input.Mode);

        // The same no-partial-write guarantee for the fields' validation rules: every rule a pack would write is checked
        // against the type's projected fields before anything is stored.
        await ValidateValidationRulesAsync(input.Packs, input.Mode);

        var result = new DocumentTypePackImportResultDto();
        var searchabilityChanged = false;
        foreach (var pack in input.Packs)
        {
            var (item, packSearchabilityChanged) = await ImportPackAsync(pack, input.Mode);
            result.Items.Add(item);
            searchabilityChanged |= packSearchabilityChanged;

            switch (item.TypeAction)
            {
                case PackItemAction.Created: result.TypesCreated++; break;
                case PackItemAction.Updated: result.TypesUpdated++; break;
                default: result.TypesSkipped++; break;
            }

            result.FieldsCreated += item.FieldsCreated;
            result.FieldsUpdated += item.FieldsUpdated;
            result.FieldsSkipped += item.FieldsSkipped;
        }

        if (searchabilityChanged)
        {
            // One rebuild for the whole import, not one per field: an import can touch many fields across
            // many types in a single call, and RebuildAsync already walks every document once regardless
            // of how many fields changed.
            await _indexManager.RebuildAsync();
        }

        return result;
    }

    protected virtual async Task ValidateSchemaPromptBudgetsAsync(
        List<DocumentTypePackDto> packs,
        PackImportMode mode)
    {
        var projectedByTypeCode = new Dictionary<string, Dictionary<string, string?>>(
            StringComparer.Ordinal);

        foreach (var pack in packs)
        {
            if (!projectedByTypeCode.TryGetValue(pack.TypeCode, out var projectedFields))
            {
                projectedFields = new Dictionary<string, string?>(StringComparer.Ordinal);
                var existingType = await _documentTypeRepository.FindByTypeCodeAsync(pack.TypeCode);
                if (existingType != null)
                {
                    var existingFields = await _fieldDefinitionRepository.GetListAsync(existingType.Id);
                    foreach (var field in existingFields)
                    {
                        projectedFields[field.Name] = field.Description;
                    }
                }

                projectedByTypeCode[pack.TypeCode] = projectedFields;
            }

            foreach (var field in pack.Fields)
            {
                if (!projectedFields.ContainsKey(field.Name) || mode == PackImportMode.CreateOrUpdate)
                {
                    projectedFields[field.Name] = field.Description;
                }
            }

            _schemaPromptBudget.EnsureCanPersist(pack.TypeCode, projectedFields.Values);
        }
    }

    /// <summary>
    /// Checks every validation rule the import would write, in request order, against the fields its type will have
    /// (the existing ones and the packs' own): each input names another of those fields, the rule's shape passes
    /// <see cref="Field.CheckValidationRules"/>, and each expression parses. A field an import leaves alone (an existing
    /// one under <see cref="PackImportMode.CreateOnly"/>) and a field whose pack carries no rules are not checked.
    /// </summary>
    protected virtual async Task ValidateValidationRulesAsync(List<DocumentTypePackDto> packs, PackImportMode mode)
    {
        var namesByTypeCode = new Dictionary<string, (HashSet<string> Existing, HashSet<string> Projected)>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            if (!namesByTypeCode.TryGetValue(pack.TypeCode, out var names))
            {
                names = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                var existingType = await _documentTypeRepository.FindByTypeCodeAsync(pack.TypeCode);
                if (existingType != null)
                {
                    foreach (var field in await _fieldDefinitionRepository.GetListAsync(existingType.Id))
                    {
                        names.Existing.Add(field.Name);
                        names.Projected.Add(field.Name);
                    }
                }

                namesByTypeCode[pack.TypeCode] = names;
            }

            foreach (var field in pack.Fields)
            {
                names.Projected.Add(field.Name);
            }
        }

        foreach (var pack in packs)
        {
            var names = namesByTypeCode[pack.TypeCode];

            // Stand-in ids, one per field name, so the shape check can tell a field reading itself or reading one field
            // twice from two different fields.
            var standIns = new Dictionary<string, Guid>(StringComparer.Ordinal);
            Guid StandIn(string name) => standIns.TryGetValue(name, out var id) ? id : standIns[name] = Guid.NewGuid();

            foreach (var field in pack.Fields)
            {
                if (field.ValidationRules == null || (names.Existing.Contains(field.Name) && mode == PackImportMode.CreateOnly))
                {
                    continue;
                }

                var rules = new List<FieldValidationRule>(field.ValidationRules.Count);
                for (var i = 0; i < field.ValidationRules.Count; i++)
                {
                    var rule = field.ValidationRules[i];
                    var inputs = new List<FieldValidationRuleInput>();
                    foreach (var input in rule?.Inputs ?? new List<DocumentTypePackValidationRuleInputDto>())
                    {
                        if (input?.FieldName == null || !names.Projected.Contains(input.FieldName))
                        {
                            throw Field.InvalidInput(field.Name, i + 1, input?.Alias);
                        }

                        inputs.Add(new FieldValidationRuleInput(input.Alias, StandIn(input.FieldName)));
                    }

                    rules.Add(rule == null ? null! : new FieldValidationRule(rule.Description, rule.Expression, inputs));
                }

                var checkedRules = Field.CheckValidationRules(field.Name, StandIn(field.Name), rules);
                for (var i = 0; i < checkedRules.Count; i++)
                {
                    RuleEvaluator.CheckExpression(field.Name, i + 1, checkedRules[i].Expression);
                }
            }
        }
    }

    /// <summary>Checks that a rule's expression parses. Resolved lazily, so the constructor a subclass calls stays as it was.</summary>
    protected FieldValidationRuleEvaluator RuleEvaluator => LazyServiceProvider.LazyGetRequiredService<FieldValidationRuleEvaluator>();

    protected virtual async Task<(DocumentTypePackItemResultDto Item, bool SearchabilityChanged)> ImportPackAsync(
        DocumentTypePackDto pack, PackImportMode mode)
    {
        var item = new DocumentTypePackItemResultDto { TypeCode = pack.TypeCode };

        // Match the type by code within the caller's layer (active rows only). Rename = new type (#207).
        var type = await _documentTypeRepository.FindByTypeCodeAsync(pack.TypeCode);

        if (type == null)
        {
            // Both modes create what is missing. CheckCodeAvailableAsync is soft-delete-aware: if a deleted
            // row occupies the code it loud-fails rather than colliding on restore.
            await _documentTypeManager.CheckCodeAvailableAsync(pack.TypeCode);
            type = new DocumentType(
                GuidGenerator.Create(),
                CurrentTenant.Id,
                pack.TypeCode,
                pack.DisplayName,
                pack.Description,
                pack.ConfidenceThreshold,
                pack.Priority,
                pack.DuplicateScope);
            StampProvenance(type, pack.Version);
            await _documentTypeRepository.InsertAsync(type, autoSave: true);
            item.TypeAction = PackItemAction.Created;
        }
        else if (mode == PackImportMode.CreateOrUpdate)
        {
            // Updating an existing type needs the Update permission — asserted here rather than as a blanket
            // method attribute, so a CreateOnly / all-new import never requires it.
            await CheckPolicyAsync(VaultExtractPermissions.DocumentTypes.Update);

            // #651: the save goes through DocumentTypeUpdater, which persists and — only on an actual scope
            // change — enqueues reconciliation. Passing pack.DuplicateScope through is also what keeps
            // export → import a round trip; without it, re-importing a pack would silently reset every
            // Uploader-scoped type back to Layer, which is the hole the now-required parameter closes.
            await _documentTypeUpdater.UpdateAsync(type, t =>
            {
                t.Update(
                    pack.TypeCode,
                    pack.DisplayName,
                    pack.Description,
                    pack.ConfidenceThreshold,
                    pack.Priority,
                    pack.DuplicateScope);
                StampProvenance(t, pack.Version);
            });

            item.TypeAction = PackItemAction.Updated;
        }
        else
        {
            // CreateOnly: leave the existing type's own properties untouched (but still add missing fields).
            item.TypeAction = PackItemAction.Skipped;
        }

        var searchabilityChanged = await ImportFieldsAsync(type.Id, pack.Fields, mode, pack.Version, item);
        return (item, searchabilityChanged);
    }

    /// <summary>Returns whether any existing field's <c>IsSearchable</c> flipped, so the caller can rebuild the index once for the whole import.</summary>
    protected virtual async Task<bool> ImportFieldsAsync(
        Guid documentTypeId,
        List<DocumentTypePackFieldDto> fields,
        PackImportMode mode,
        int version,
        DocumentTypePackItemResultDto item)
    {
        var searchabilityChanged = false;
        var uniqueKeyCleared = false;
        var written = new List<DocumentTypePackFieldDto>();

        foreach (var f in fields)
        {
            var existing = await _fieldDefinitionRepository.FindByNameAsync(documentTypeId, f.Name);

            if (existing == null)
            {
                EnsureFieldTypeRegistered(f.FieldTypeName!, f.Configuration);
                await _fieldDefinitionManager.CheckNameAvailableAsync(documentTypeId, f.Name);
                CheckSearchable(f.FieldTypeName!, f.IsSearchable);
                CheckUniqueKey(f.FieldTypeName!, f.IsUniqueKey);
                var field = new Field(
                    GuidGenerator.Create(),
                    CurrentTenant.Id,
                    documentTypeId,
                    f.Name,
                    f.DisplayName,
                    f.FieldTypeName!,
                    f.Description,
                    f.Configuration,
                    f.DisplayOrder,
                    f.IsRequired,
                    f.IsSearchable,
                    f.IsUniqueKey);
                StampProvenance(field, version);
                await _fieldDefinitionRepository.InsertAsync(field, autoSave: true);
                item.FieldsCreated++;
                written.Add(f);
                // Never needs a rebuild: a field that did not exist a moment ago cannot have values on any
                // existing document to backfill.
            }
            else if (mode == PackImportMode.CreateOrUpdate)
            {
                // Updating an existing field needs the Update permission — asserted lazily; a CreateOnly or
                // all-new import never reaches here.
                await CheckPolicyAsync(VaultExtractPermissions.FieldDefinitions.Update);
                EnsureFieldTypeRegistered(f.FieldTypeName!, f.Configuration);
                await GuardFieldMutationAsync(existing, f);
                CheckSearchable(f.FieldTypeName!, f.IsSearchable);
                CheckUniqueKey(f.FieldTypeName!, f.IsUniqueKey);
                var wasSearchable = existing.IsSearchable;
                var wasUniqueKey = existing.IsUniqueKey;
                existing.SetDisplayName(f.DisplayName);
                existing.SetDescription(f.Description);
                existing.SetFieldTypeName(f.FieldTypeName!);
                existing.SetConfiguration(f.Configuration);
                existing.SetDisplayOrder(f.DisplayOrder);
                existing.SetIsRequired(f.IsRequired);
                existing.SetIsSearchable(f.IsSearchable);
                existing.SetIsUniqueKey(f.IsUniqueKey);
                StampProvenance(existing, version);
                await _fieldDefinitionRepository.UpdateAsync(existing, autoSave: true);
                item.FieldsUpdated++;
                written.Add(f);
                searchabilityChanged |= wasSearchable != existing.IsSearchable;
                uniqueKeyCleared |= wasUniqueKey && !existing.IsUniqueKey;
            }
            else
            {
                item.FieldsSkipped++;
            }
        }

        // Rules last: an input may name a field the same pack creates after the one whose rule reads it. ValidateValidationRulesAsync
        // has already checked every one of them, so each input resolves. A pack without rules for a field (null, every
        // version-1 and version-2 pack) leaves that field's rules as they are.
        if (written.Any(f => f.ValidationRules != null))
        {
            var byName = (await _fieldDefinitionRepository.GetListAsync(documentTypeId)).ToDictionary(f => f.Name, StringComparer.Ordinal);
            foreach (var f in written.Where(f => f.ValidationRules != null))
            {
                var target = byName[f.Name];
                target.SetValidationRules(f.ValidationRules!.Select(rule => new FieldValidationRule(
                    rule.Description,
                    rule.Expression,
                    rule.Inputs.Select(input => new FieldValidationRuleInput(input.Alias, byName[input.FieldName].Id)).ToList())));
                await _fieldDefinitionRepository.UpdateAsync(target, autoSave: true);
            }
        }

        if (uniqueKeyCleared)
        {
            // Mirrors FieldDefinitionAppService: a unique key turned off by an import leaves the same stale duplicate
            // basis a deleted key does. One enqueue per type; the job rechecks the final active schema when it runs.
            await BackgroundJobManager.EnqueueAsync(
                new DuplicateBasisCleanupArgs
                {
                    DocumentTypeId = documentTypeId,
                    TenantId = CurrentTenant.Id
                });
        }

        return searchabilityChanged;
    }

    /// <summary>
    /// Mirror of the <see cref="FieldDefinitionAppService"/> data-safety guard: never break already-extracted
    /// values by changing the field type under them. A pack that would do so loud-fails (the whole import
    /// rolls back in the ambient UoW) instead of leaving values nothing can render or index.
    /// <para>
    /// One guard where v2 had two: "one value or many" is a property of the field type in v3 (Tags versus
    /// Text), so narrowing a multi-valued field <b>is</b> a field-type change and is covered here.
    /// </para>
    /// </summary>
    protected virtual async Task GuardFieldMutationAsync(Field existing, DocumentTypePackFieldDto pack)
    {
        // #625 follow-up: a composite type (Table) whose own FieldTypeName is unchanged can still have its
        // COLUMNS changed by the incoming pack — see FieldDefinitionAppService.UpdateAsync's mirror of this
        // same guard for the full rationale (renaming/removing/adding/retyping/reordering a column orphans
        // historical cell data exactly like a top-level type change would).
        var fieldTypeChanged = !string.Equals(pack.FieldTypeName, existing.FieldTypeName, StringComparison.Ordinal);
        var columnsChanged = !fieldTypeChanged &&
            CompositeColumnsChanged(existing.FieldTypeName, existing.Configuration, pack.Configuration);

        if (!fieldTypeChanged && !columnsChanged)
        {
            return;
        }

        if (await _documentRepository.AnyFlexFieldValueAsync(existing, IsIndexable(existing.FieldTypeName)))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.DataTypeChangeNotAllowed)
                .WithData("Name", existing.Name);
        }
    }

    /// <summary>Mirror of <c>FieldDefinitionAppService.CompositeColumnsChanged</c> — see that method's own doc.</summary>
    protected virtual bool CompositeColumnsChanged(
        string fieldTypeName, FieldConfigurationDictionary? oldConfiguration, FieldConfigurationDictionary? newConfiguration)
    {
        var kernelFieldType = _fieldTypeResolver.GetAll()
            .FirstOrDefault(t => string.Equals(t.Name, fieldTypeName, StringComparison.Ordinal));
        if (kernelFieldType is not ICompositeFieldType compositeFieldType)
        {
            return false;
        }

        var oldColumns = compositeFieldType
            .GetInlineFields(oldConfiguration ?? new FieldConfigurationDictionary())
            .Select(c => (c.Name, c.FieldTypeName))
            .ToList();
        var newColumns = compositeFieldType
            .GetInlineFields(newConfiguration ?? new FieldConfigurationDictionary())
            .Select(c => (c.Name, c.FieldTypeName))
            .ToList();

        return !oldColumns.SequenceEqual(newColumns);
    }

    /// <summary>Whether values of this field type reach the query index at all; see <see cref="FieldDefinitionAppService"/>.</summary>
    protected virtual bool IsIndexable(string fieldTypeName)
        => _fieldTypeResolver.GetAll()
            .FirstOrDefault(t => string.Equals(t.Name, fieldTypeName, StringComparison.Ordinal))?.IndexValueType != null;

    /// <summary>
    /// Same guard as <see cref="FieldDefinitionAppService.EnsureFieldTypeRegistered"/> — a pack is another
    /// write path into the same fields, and owes them the same fail-closed check against Vault Extract's
    /// own supported set, not just the kernel's full registry, including the #625 recursive check for a
    /// composite type's (<c>Table</c>) own columns.
    /// </summary>
    protected virtual void EnsureFieldTypeRegistered(string fieldTypeName, FieldConfigurationDictionary? configuration)
    {
        var allFieldTypes = _fieldTypeResolver.GetAll();
        var kernelFieldType = allFieldTypes
            .FirstOrDefault(t => string.Equals(t.Name, fieldTypeName, StringComparison.Ordinal));

        if (kernelFieldType == null || !_fieldTypeExtensionRegistry.IsSupported(fieldTypeName))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.UnknownFieldType)
                .WithData("FieldTypeName", fieldTypeName);
        }

        if (kernelFieldType is ICompositeFieldType)
        {
            // Same ordering as FieldDefinitionAppService's mirror: the depth cap runs FIRST, before
            // anything below recurses into the configuration itself — see CompositeFieldNesting's own doc.
            if (CompositeFieldNesting.ExceedsMaxDepth(fieldTypeName, configuration, allFieldTypes))
            {
                throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.CompositeNestingTooDeep)
                    .WithData("FieldTypeName", fieldTypeName)
                    .WithData("MaxDepth", CompositeFieldNesting.MaxDepth);
            }

            EnsureColumnsRegistered(fieldTypeName, configuration ?? new FieldConfigurationDictionary(), allFieldTypes);
        }
    }

    /// <summary>
    /// Mirror of <c>FieldDefinitionAppService.EnsureColumnsRegistered</c> — a pack is another write path
    /// into the same fields, and owes an imported composite field's own columns the same recursive registry
    /// + Name-allow-list check, not just the kernel's generic shape validation. Safe from unbounded
    /// recursion only because <see cref="EnsureFieldTypeRegistered"/> already rejected anything deeper than
    /// <see cref="CompositeFieldNesting.MaxDepth"/> before this method is ever called.
    /// </summary>
    protected virtual void EnsureColumnsRegistered(
        string fieldTypeName, FieldConfigurationDictionary configuration, IReadOnlyList<IFieldType> allFieldTypes)
    {
        var fieldType = allFieldTypes.FirstOrDefault(t => string.Equals(t.Name, fieldTypeName, StringComparison.Ordinal));
        if (fieldType is not ICompositeFieldType compositeFieldType)
        {
            return;
        }

        foreach (var column in compositeFieldType.GetInlineFields(configuration))
        {
            if (!_fieldTypeExtensionRegistry.IsSupported(column.FieldTypeName))
            {
                throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.UnknownColumnFieldType)
                    .WithData("FieldTypeName", column.FieldTypeName)
                    .WithData("ColumnName", column.Name);
            }

            if (!ColumnNameRegex.IsMatch(column.Name))
            {
                throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidColumnName)
                    .WithData("ColumnName", column.Name)
                    .WithData("Pattern", FieldDefinitionConsts.NamePattern);
            }

            EnsureColumnsRegistered(column.FieldTypeName, column.Configuration, allFieldTypes);
        }
    }

    /// <summary>Same guard as <see cref="FieldDefinitionAppService.CheckSearchable"/> — a pack is another write path into the same fields, and owes them the same fail-closed check.</summary>
    protected virtual void CheckSearchable(string fieldTypeName, bool isSearchable)
    {
        if (isSearchable && !IsIndexable(fieldTypeName))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.FieldTypeNotSearchable)
                .WithData("FieldTypeName", fieldTypeName);
        }
    }

    /// <summary>Same guard as <see cref="FieldDefinitionAppService.CheckUniqueKey"/> (#626) — a pack must not mark a Table or CKEditor field as a duplicate-detection key the field designer refuses.</summary>
    protected virtual void CheckUniqueKey(string fieldTypeName, bool isUniqueKey)
    {
        if (isUniqueKey && !IsIndexable(fieldTypeName))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.FieldTypeNotUniqueKeyable)
                .WithData("FieldTypeName", fieldTypeName);
        }
    }

    // Provenance: mark pack-sourced config in ExtraProperties (config metadata on the type/field aggregate,
    // not the Document truth source). A stable value keeps re-import idempotent (no phantom diffs).
    protected virtual void StampProvenance(IHasExtraProperties entity, int version)
    {
        entity.SetProperty(DocumentTypePackConsts.ProvenanceSourceKey, DocumentTypePackConsts.ProvenanceSourceValue);
        entity.SetProperty(DocumentTypePackConsts.ProvenanceVersionKey, version);
    }

    /// <summary>
    /// The type and its fields as a pack. A rule input is written as the name of the field it reads; a rule reading a field
    /// that is no longer among <paramref name="fields"/> (deleted) is left out, since no import could resolve it.
    /// </summary>
    protected virtual DocumentTypePackDto MapToPack(DocumentType type, List<Field> fields)
    {
        var namesById = fields.ToDictionary(f => f.Id, f => f.Name);
        return new DocumentTypePackDto
        {
            Version = DocumentTypePackConsts.CurrentVersion,
            TypeCode = type.TypeCode,
            DisplayName = type.DisplayName,
            Description = type.Description,
            ConfidenceThreshold = type.ConfidenceThreshold,
            Priority = type.Priority,
            DuplicateScope = type.DuplicateScope,
            Fields = fields
                .OrderBy(f => f.DisplayOrder)
                .ThenBy(f => f.Name)
                .Select(f => new DocumentTypePackFieldDto
                {
                    Name = f.Name,
                    DisplayName = f.DisplayName,
                    Description = f.Description,
                    FieldTypeName = f.FieldTypeName,
                    Configuration = f.Configuration,
                    DisplayOrder = f.DisplayOrder,
                    IsRequired = f.IsRequired,
                    IsSearchable = f.IsSearchable,
                    IsUniqueKey = f.IsUniqueKey,
                    ValidationRules = f.ValidationRules
                        .Where(rule => rule.Inputs.All(input => namesById.ContainsKey(input.FieldId)))
                        .Select(rule => new DocumentTypePackValidationRuleDto
                        {
                            Description = rule.Description,
                            Expression = rule.Expression,
                            Inputs = rule.Inputs
                                .Select(input => new DocumentTypePackValidationRuleInputDto
                                {
                                    Alias = input.Alias,
                                    FieldName = namesById[input.FieldId]
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };
    }
}

---
description: "Dignite Vault Extract field architecture: where a field is defined, field types and their dispatch, where values live, the extraction and query paths"
paths:
  - "modules/vault.extract/src/**/Document.cs"
  - "modules/vault.extract/src/**/DocumentDto.cs"
  - "modules/vault.extract/src/**/Fields/**/*.cs"
  - "modules/vault.extract/src/**/*Field*.cs"
  - "modules/vault.extract/src/**/DocumentTypes/**/*.cs"
  - "modules/vault.extract/src/**/Classification/**/*.cs"
---

# Field architecture

Type-bound fields are built on the `Dignite.Abp.FlexFields` kernel (sibling repo `abp-modules/flex-fields`). The kernel supplies field types, the value bag, the derived index and key migration. Vault Extract decides which types exist, how a field is scoped, and what the pipeline does with a value.

## System fields worth knowing

- `DocumentTypeId` is the internal association, by the immutable `DocumentType.Id`. The wire format (REST / MCP / ETO) carries the `DocumentTypeCode` string, resolved on the read path, so renaming a TypeCode does not cascade.
- Each bit of `ReviewReasons` is maintained by exactly one pipeline stage. `ReviewDisposition` is the operator's verdict.
- `CabinetId` is a manual filing dimension. The pipeline neither reads nor writes it.
- Classification runs within the document's own layer. The background path switches layer with `ICurrentTenant.Change` and then uses the ordinary `GetListAsync`.

## Where a field is defined

- One `Field` entity carries both layers: `TenantId IS NULL` is a Host field, otherwise a tenant field. The same name in two layers is two independent rows.
- A field is bound to one `DocumentType`, which is why `IsRequired` / `IsSearchable` / `IsUniqueKey` live on `Field`. Uniqueness is `(TenantId, DocumentTypeId, Name)`.
- **That uniqueness is an application-layer check** in `FieldDefinitionManager`, not a database index. Every write path goes through it: create, rename, restore, and the cascade restore from `DocumentTypeAppService.RestoreAsync`.
- **`Field.Name` is the key its value is stored under in every document's bag**, so a rename rewrites the bags. `FieldDefinitionAppService.UpdateAsync` does that through `DocumentFieldValueMigrator`, not the kernel's `IFlexFieldValueMigrator<Document>`. The kernel renames by name across all of a tenant's documents; two document types may each define `invoice_no`, so the unscoped walk would move the other type's values to a key no definition backs.
- **`Name` is validated against `FieldDefinitionConsts.NamePattern` in `Field.SetName`.** The value is concatenated raw into the LLM's schema message, so the pattern is a prompt-injection boundary. The kernel validates no format.
- The app service, DTO and route keep the names `IFieldDefinitionAppService` / `FieldDefinitionDto` / `/field-definitions`. Only the entity is called `Field`.

## Field types

A type is `Field.FieldTypeName` (a registration key) plus `Field.Configuration`.

| Key | What to know |
|---|---|
| `Text`, `Number`, `Boolean` | plain scalars |
| `DateTime` | one type for date, date-time and month, told apart by `DateTime.InputMode`. All normalize to midnight on write; Month also pins the day to 1. The mode→format mapping is in `DateTimeInputModeFormats.Format`, with an Angular twin `dateInputType` in `field-value-filter.model.ts`: change both together |
| `Select` | closed vocabulary; `Select.Options` becomes a JSON-schema `enum` in the extraction schema. Multi-valued when `Select.Multiple` |
| `Tags` | open vocabulary, always a list |
| `CKEditor` | long text, never indexed (`IndexValueType` is null). Write `ContentFormat = Markdown` explicitly; the type's own default is `Html` |
| `Table` | one column schema (`TableConfiguration.Columns`) for every row, never indexed. **Each row's cells sit under a `"values"` key**: `{"values": {<column>: <value>}}`, in the extraction schema, in `TryRead` and in `WriteJson` alike. A flat row looks plausible, but every cell then reads as absent in the UI with no error |

- **"Is this field multi-valued" has two branches**: `Tags` always, `Select` when configured `Multiple`. Ask `IVaultExtractFieldTypeRegistry.IsMultiValue(fieldTypeName, configuration)`; testing the type name alone mishandles every multi-Select. There is no client-side equivalent.
- **Configuration enum values are written as numbers.** The kernel reads them with `(int)(long)value`; a name silently falls back to the default.

## Field-type dispatch

Five call sites behave differently per type: `FlexFieldValueReader` (read and validate), `FlexFieldValueSchemaBuilder` (extraction schema), `FlexFieldValueJsonWriter` (egress JSON), `ExportCellRenderer`, and `FlexFieldFingerprintCalculator`. A new field type is one `IVaultExtractFieldTypeExtension`, in `Application/Documents/Fields/FieldTypeExtensions/`.

- **Derive from `VaultExtractFieldTypeExtensionBase`, not the bare interface.** ABP exposes an interface only to a class whose name ends with the interface name. Without the base class's `[ExposeServices]`, the registry receives an empty list, every `TryRead` returns `false`, and every extracted field is rejected, all without an error.
- `IVaultExtractFieldTypeRegistry.Get` throws for an unregistered name. `TryGet` is for call sites where "not registered" is legitimate, such as the fingerprint calculator.
- The five dispatch classes are static and take the registry as a parameter, so tests use a hand-built registry.
- **A composite type cannot constructor-inject the registry**, because the registry's constructor enumerates every extension. `TableFieldTypeExtension` resolves it lazily through `LazyServiceProvider`, and the property carries `[DisablePropertyInjection]`. Without that attribute, ABP's property autowiring resolves the registry eagerly and overflows the stack.

## Where a value lives

- **`Document.FlexFields`** is authoritative: a JSON column keyed by `Field.Name`, written as a whole set through `Document.SetFlexFields(...)`. A null value drops its key.
- **`DocumentFlexFieldIndex`** is derived and never authoritative. A type or searchability change is repaired by `RebuildAsync`. `NumberValue` is mapped `decimal(38,6)`, because EF's default `decimal(18,2)` would round silently.
- **Every write of the bag owes the index an `IFlexFieldIndexManager<Document>.SynchronizeAsync` in the same unit of work.** Miss one and the document stops matching its own field filters, while every read through the bag still looks right. There are four such sites: the extraction write and the no-definitions clearing path in `FieldExtractionService`, `DocumentAppService.UpdateExtractedFieldsAsync`, and `DocumentClassificationBackgroundJob.CompleteRunAsync` (the retraction paths that clear the bag).
- `IsSearchable` decides whether a field's values enter the index at all.

## Extraction

Classification schedules one field-extraction run in the same transaction as its own completion (`DocumentPipelineJobScheduler`), before the document can derive Ready. `FieldExtractionService` then:

1. reads the `Field` rows of the document's own layer;
2. builds the response schema from type and configuration;
3. makes one LLM call;
4. re-reads the definitions and discards any value whose field was deleted, renamed or retyped while the call was in flight (the guards compare both `FieldTypeName` and `Name`);
5. validates and converts each value with `FlexFieldValueReader`;
6. writes the whole set and synchronizes the index. No event is published here: egress is `DocumentReadyEto`.

`FlexFieldValueReader` is the single validation gate, shared with the operator edit. The extraction workflow passes the raw `JsonElement` through and does not validate, because a second gate could only diverge from the first.

## Reading and querying

- The egress `ExtractedFields` is assembled from the bag by `DocumentAppService.AssembleExtractedFields`. It resolves field references with the soft-delete filter off, so an archived field still resolves to a name for a document that holds its value.
- Filtering by field value goes through `DocumentFieldQueryResolver` → `IFlexFieldQueryExecutor<Document>`, against the index. An unknown field name, or a non-indexable type, throws a `BusinessException` and never returns a silent empty result.
- **Presence is tested by name**: `document.FlexFields.ContainsKey(field.Name)`. A test by id against value rows compiles and reports every required field as missing.
- The duplicate fingerprint hashes the unique-key fields' values from the bag in a canonical order.

## Changing a field's type

It is refused when the field already holds values: they would stay in the bag, but nothing would render or index them under the new type. Narrowing a multi-value field counts as a type change.

"Does any document hold this field" is answered by `IDocumentRepository.AnyFlexFieldValueAsync`: from the index when the field is indexable and searchable, otherwise by paging the type's documents and testing the key in memory. That fallback is bounded only by the type's document count.

## Leftovers from the previous model

`FieldDefinitionToFieldMapper`, the `FieldDataType` enum and some older constants are still used by `FieldDraftSuggestionAppService` and `DocumentTypePackV1Upconverter`. `DocumentFieldValidationWarning.FieldDefinitionId` keeps its column name because it is on the wire; its foreign key points at `VaultFields`.

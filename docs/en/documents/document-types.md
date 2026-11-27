```json
//[doc-seo]
{
    "Description": "Define document types and their fields in Dignite Vault: Host and tenant layers, field types, extraction prompts, duplicate detection, validation rules and type packs."
}
```

# Document Types and Fields

A **document type** is what classification sorts documents into, and the container of the **fields** extracted from documents of that type. Dignite Vault ships **no built-in types and no business fields**: an administrator defines every type and every field, and the channel extracts by that schema. A contract amount or an invoice number is a field you define, not something the module knows about.

> A business system that needs more than field values (an approval workflow, a contract record) builds it on its own side from the [egress contracts](../egress/integration-events.md). Dignite Vault stays a channel.

## Host and Tenant Layers

Types and fields live in two separate **layers**:

- The **Host layer** (`TenantId` is null) is defined by the Host administrator and applies only to the Host's own documents.
- Each **tenant layer** is defined by that tenant's administrator and applies only to that tenant's documents.

The layers never mix. A Host type is **not** visible to tenants, and a tenant's types are invisible to other tenants. A document is classified against, and extracts fields for, the types of its own layer only (decided by the document's `TenantId`). The same type code, or the same field name, may exist in several layers as independent rows, so a downstream consumer must route by the **`(TenantId, DocumentTypeCode)`** pair, never by the code alone.

## Document Types

Types are managed on **Vault Extract → Document Types** (`IDocumentTypeAppService`).

| Property | Constraints | Used for |
| --- | --- | --- |
| `TypeCode` | Required, at most 128 characters, letters, digits, `_` and `-` in dot-separated segments (for example `invoice.standard`). Unique in its layer | The machine identity of the type on every egress (`DocumentTypeCode`). Documents reference the type by id, so a code can be renamed without touching them; downstream consumers keyed on the old code must follow |
| `DisplayName` | Required, at most 128 characters, no control characters | Shown in the UI and sent to the classifier as the candidate's name |
| `Description` | Optional, at most 512 characters, no control characters | Sent to the classifier with the name. Describe what distinguishes this type from its neighbors |
| `ConfidenceThreshold` | `0` to `1`, default `0.7` | A classification at or above it is accepted automatically; below it the document goes to review |
| `Priority` | Integer | Candidate order in the classification prompt (higher first). When a layer has more types than `MaxDocumentTypesInClassificationPrompt` (default 50), the lowest-priority ones are left out |
| `DuplicateScope` | `Layer` (default) or `Uploader` | Where [duplicate detection](#duplicate-detection) looks |

While typing a display name, the editor suggests a code through `ISlugSuggestionAppService`, which asks the LLM for an English, URL-safe slug. The field editor does the same for field names. It needs `DocumentTypes.Create` or `FieldDefinitions.Create`.

### Deleting and Restoring

Deleting a type is refused (`Extract:DocumentTypeInUse`) while any document references it, recycle-bin documents included. Otherwise the type and its fields are soft-deleted. A deleted type still holds its code: a new type cannot reuse it until the deleted one is gone for good.

Restoring a type restores its fields with it (a field whose name is now taken by an active field is skipped) and re-checks the [prompt budget](#prompt-budget). Deleting or restoring a type never touches documents.

## Fields

A type's fields are managed on its **Fields** page (`IFieldDefinitionAppService`). Each field belongs to exactly one type.

| Property | Constraints | Used for |
| --- | --- | --- |
| `Name` | Required, 1 to 64 letters, digits, `_` or `-`. Unique within the type | The key the value is stored and served under (`extractedFields.<name>`), and the name in field filters. Treat it as an API contract |
| `DisplayName` | Required, at most 128 characters | Column header in the UI and in [Data Download](../egress/data-download.md) |
| `Description` | Optional | **The extraction instruction** sent to the LLM for this field: what to look for and the expected format. Checks the value must pass are [validation rules](#validation-rules), not part of the instruction |
| `FieldTypeName` | One of the [field types](#field-types); default `Text` | What the field accepts, how it is validated, rendered and queried |
| `Configuration` | Depends on the field type | Type-specific settings (options, limits, input mode, columns) |
| `DisplayOrder` | Integer | Column order in the document list and in downloads (ties broken by `Name`) |
| `IsRequired` | Default `false` | A missing value raises the non-blocking `MissingRequiredFields` review reason |
| `IsSearchable` | Default `true` | Whether the values are written to the query index, so they can be [filtered](../egress/rest-api.md#listing-and-filtering-documents). Not available on long text and table fields |
| `IsUniqueKey` | Default `false` | Part of the [duplicate detection](#duplicate-detection) key. Not available on long text and table fields |
| `ValidationRules` | Up to 10 | Checks the value must pass, evaluated in code; see [Validation Rules](#validation-rules) |

### Field Types

| Type | Value | Configuration | Filterable |
| --- | --- | --- | --- |
| `Text` | One string | `Text.Mode`, `Text.CharLimit` (enforced as the maximum length), `Text.Placeholder` | Equality |
| `Number` | One number | `Number.Decimals`, `Number.Min`, `Number.Max`, `Number.Step`, `FormatSpecifier` (display settings; extraction checks only that the value is a number) | Equality or range |
| `Boolean` | `true` / `false` | `Boolean.Default` | Equality |
| `DateTime` | A date, date-time or month, chosen by `DateTime.InputMode` (`Date`, `DateTime`, `Month`) | `DateTime.InputMode`, `DateTime.Min`, `DateTime.Max` (enforced) | Equality or range |
| `Select` | One option, or several when `Select.Multiple` is set | `Select.Options` (the closed vocabulary, sent to the LLM as an enum), `Select.Multiple`, `Select.NullText`, `Select.Size` | Equality |
| `Tags` | A list of free strings | `Tags.MaxCount` (default 100), `Tags.MaxLength` (default 256), `Tags.Placeholder` | Equality |
| `CKEditor` | Long text, stored as Markdown | `CKEditor.Mode`, `CKEditor.ContentFormat`, … | No |
| `Table` | Rows sharing one column schema; each column is itself a field definition | `Table.Columns` | No |

Field types come from the [`Dignite.Abp.FlexFields`](https://github.com/dignite-projects/abp-modules) kernel, plus Vault Extract's own `Tags` type. `GetFieldTypesAsync` returns the types this deployment supports and whether each is indexable. Values are served as `yyyy-MM-dd` (`Date`), `yyyy-MM-ddTHH:mm:ss` (`DateTime`, no offset) and `yyyy-MM` (`Month`, stored as the first day of the month).

### Changing a Field

- **Renaming** is allowed. Every document of the type has its stored value moved to the new name, but downstream consumers and saved filters that use the old name must change.
- **Changing the field type** (or a table's columns) is refused while any document holds a value for the field (`Extract:FieldDefinitionDataTypeChangeNotAllowed`): the old values would stay stored but nothing could read them under the new type.
- **Turning `IsSearchable` on or off** rebuilds the query index of the type in the background.
- **Turning `IsUniqueKey` on or off** applies to documents as they are extracted again; existing documents keep their current duplicate verdict until then.
- **Deleting** a field soft-deletes it and stops extracting it. Documents keep their stored values, which REST and MCP still return in `extractedFields` (the read path resolves deleted fields on purpose, so historical documents stay readable), but the field leaves [downloads](../egress/data-download.md) and field filters. Restoring the field (its type must be active and its name free) brings it back.

Changing a field does not re-extract existing documents. Run a [batch field re-extraction](../pipeline/reprocessing.md) when you want them to pick up the change.

### Prompt Budget

Every field's `Description` is sent with every field-extraction call of its type, so the **total** across a type's active fields is capped by `Vault:ExtractBehavior:MaxFieldSchemaPromptLength` (default 32,000 characters). A create, update, restore or import that would exceed it is refused (`Extract:FieldDefinitionSchemaPromptBudgetExceeded`). One long prompt is fine; it is the sum that is bounded.

### AI Drafting Helpers

The field editor offers two LLM helpers. Both fail open: on a timeout or a provider error the editor keeps what you typed.

| Helper | Service | What it does |
| --- | --- | --- |
| Draft a field | `IFieldDraftSuggestionAppService.DraftAsync` | Turns a plain-language request (up to 4,000 characters) into a draft: display name, name (for a new field), field type, configuration and the required flag |
| Polish a prompt | `IFieldPromptPolishAppService.PolishAsync` | Rewrites a field's extraction instruction into clear Markdown |

Both need `VaultExtract.FieldDefinitions.Create` or `.Update` and use the structured LLM client. Validation rules have drafting helpers of their own: see [Drafting and Trying Rules](#drafting-and-trying-rules).

## How Fields Are Extracted

After classification assigns a type, one **field-extraction** run sends the document's complete Markdown and the type's schema to the LLM in a single call, unless the document's tables are too long for one answer (see [Long Tables](#long-tables)). The schema is built from each field's type and configuration (for example a `Select` field's options become an enum), with each field's `Description` as its instruction.

Every returned value is validated against its field type before it is stored; an invalid value is dropped and logged. A value whose field was renamed, retyped or deleted while the call was running is discarded. The accepted values replace the document's previous values as a whole.

The Markdown is never truncated for field extraction, because a field can sit anywhere in a document. A document longer than `MaxFieldExtractionMarkdownLength` (default 200,000 characters) is not sent at all: it gets the blocking `FieldExtractionIncomplete` review reason, and an operator enters the values by hand and confirms them. See [AI Provider](../configuration/ai-provider.md#cross-cutting-llm-behavior-extractbehavior).

### Long Tables

A `Table` field's answer transcribes the table row by row, so a long table (a bank statement's transactions, say) can make one answer longer than the model reliably writes. When the rows of the tables in the Markdown run to more characters than one answer is sized for (half of `MaxFieldExtractionOutputTokens`, at 4 tokens a character, shared among the type's `Table` fields: 2,048 characters at the default), the type's `Table` fields are read in excerpts:

1. The Markdown is cut into excerpts of about that size. A cut falls only between lines, so never inside a table row, and never inside a table's header or between rows an HTML `rowspan` joins. A cut at a table's edge is preferred, so an excerpt rarely holds the end of one page's table and the start of the next. An excerpt that starts inside a table is shown that table's header. Where a cut falls between two lines of text (a table whose grid the parse flattened into lines), each excerpt is also shown the three lines across the cut, and a row belongs to the excerpt it begins in.
2. One call per excerpt, a few in parallel, returns the rows that begin in it, in order. An excerpt is read once more, in two halves where it can be cut, when its answer was cut off or unreadable, or returned fewer rows than the excerpt's recognised tables hold; the fuller reading is kept. Fewer rows after that are kept as they are, because the excerpt may hold a table the field does not ask for.
3. The rows are joined in document order. Nothing is deduplicated: the excerpts do not overlap, and a bank statement can hold two identical rows.
4. One last call extracts the other fields over the whole document, as before. It is not given the joined rows: a check over the whole table (a running balance, the first and last rows against the opening and closing balance) is a [validation rule](#validation-rules), evaluated in code over the stored rows. Given the rows, the model copied a misread balance into the closing balance, so the error agreed with itself. A type whose only fields are tables makes no last call.

The rows stay in the order the document prints them. An instruction in a `Table` field's description that asks for the rows to be reordered (reversed when the statement runs newest first, say) is applied only when the table fits in one answer; a table read in excerpts keeps the document's order. A rule such as `value.every((r, i) => i === 0 || value[i - 1].values.date <= r.values.date)` is what catches a table in the wrong order.

A document read this way costs more input tokens: every excerpt call repeats the instructions and the `Table` fields' descriptions. For a 114-row statement over four pages that came to five excerpt calls and the last one: 30,000 to 38,000 input tokens where one call reads about 11,000, and about 10,000 output tokens, as one complete answer would write; one to two and a half minutes in all. Those figures were measured while the last call still read the joined rows.

If an excerpt still has no answer, or the joined rows run past `MaxFieldExtractionMarkdownLength`, the `Table` fields keep the values the document already had and the document gets the blocking `FieldExtractionIncomplete` review reason. Each `Table` field's sources are the passages quoted from every excerpt; there is no source per row.

### Answers Cut Off at the Output Limit

The model writes every field into one answer, and a long table can still make that answer longer than `MaxFieldExtractionOutputTokens` allows: one whose rows the parse did not recognise as a table, or one the estimate above underrated. The provider then stops it mid-way. The schema asks for the short fields first and tables last, so the fields that finished before the cut are kept. When the cut fell inside a `Table` field, the run reads the type's `Table` fields again [in excerpts](#long-tables). Otherwise the fields that finished before the cut are written, and the field the cut fell inside, and any after it, keep the values the document already had. A table is never stored with only its first rows. The document gets the blocking `FieldExtractionIncomplete` review reason: an operator enters what is missing and confirms the field entry, or a later extraction (after the host raises the limit, for example) replaces the set. An answer cut off only in the passages it quotes as sources is complete.

Operators can correct any value on the document's detail page. A correction is validated the same way, replaces the whole set, and re-fires `DocumentReadyEto` for a document that stays Ready.

## Duplicate Detection

Duplicate detection is **opt-in per type**: mark the fields that identify a document (an invoice number and an issuer, for example) as **unique keys**. After extraction, Dignite Vault hashes the unique-key values into a fingerprint; another live document of the same layer and type with the same fingerprint makes this document a **suspected duplicate**, a blocking review reason. A document with a unique-key field missing has no fingerprint and is never flagged.

The type's `DuplicateScope` decides which documents are compared:

| Scope | Compares against |
| --- | --- |
| `Layer` (default) | Every document of the type in the layer, whoever uploaded it |
| `Uploader` | Only documents uploaded by the same user. Documents uploaded without a user share one group |

Changing the scope re-evaluates the type's existing documents in the background (no LLM call), which can release documents to Ready or send others to review. The type editor previews the effect first (`GetDuplicateScopePreviewAsync`: how many documents would be flagged and how many cleared).

An operator resolves a suspected duplicate by **allowing** it (`AllowDuplicateAsync`), which stays in force until the document's key values change, or by deleting it. Deleting a type's last unique-key field clears the fingerprints and flags of its documents.

> This is separate from the upload check, which refuses the exact same file uploaded twice by the same user. See [Documents and Review](documents.md#uploading).

## Validation Rules

A field can carry up to 10 **validation rules**: checks its value must pass, evaluated in code each time the document's values are written. A rule that does not hold puts the blocking `FieldValidationWarning` review reason on the document, worded with the rule's description and shown on the document's detail page (`ReviewReasonDetails`). The value itself is kept. Warnings never appear on search results, downloads or events.

| Property | Constraints | Used for |
| --- | --- | --- |
| `description` | Required, one line, at most 256 characters | What the rule requires, in your words. It is the warning the operator reads |
| `expression` | Required, one JavaScript expression, at most 4,000 characters | Decides the rule |
| `inputs` | At most 10, each an `alias` (a JavaScript identifier other than `value`) and another field of the same type | The other fields the expression reads |

The expression sees the field's own value as `value` and each input under its alias, in the shape [`ExtractedFields`](../egress/rest-api.md) carries: a number as a number, a date as an ISO string in the field's format, a multi-valued field as an array of strings, a table as an array of rows with each row's cells under `values`. It returns `true` when the rule holds, `false` when it does not, or `[false, "detail"]` to say where; the detail is added to the warning.

A bank statement's `transactions` table, with `openingBalance` and `closingBalance` fields:

| Description | Expression | Inputs |
| --- | --- | --- |
| The first row's balance is the opening balance | `value[0].values.balance === opening` | `opening` → `openingBalance` |
| The last row's balance is the closing balance | `value.at(-1).values.balance === closing` | `closing` → `closingBalance` |
| Dates never go backwards | `value.every((r, i) => i === 0 \|\| value[i - 1].values.date <= r.values.date)` | none |
| Each balance follows from the one before | `(() => { for (let i = 1; i < value.length; i++) { const p = value[i - 1].values, r = value[i].values; if (Math.abs(p.balance + (r.depositAmount ?? 0) - (r.withdrawalAmount ?? 0) - r.balance) > 0.005) return [false, `row ${i + 1}`]; } return true; })()` | none |

- **Skipped**: a rule whose value or any input is absent says nothing. A missing required value already raises `MissingRequiredFields`.
- **Could not be evaluated**: an expression that throws, returns anything else, or runs past the sandbox's limits counts as failing, and the warning says the rule could not be evaluated. A broken rule is seen rather than silently passed.
- **Sandbox**: each rule runs in a JavaScript engine of its own ([Jint](https://github.com/sebastienros/jint)), in strict mode, with at most 200,000 statements, 250 ms, 16 MB and 64 levels of calls, and nothing of the host within reach: no .NET types, network or files. Rules are written by each layer's administrators, so these limits are fixed, not configuration.
- **Numbers** are JavaScript doubles: compare a sum with a tolerance, `Math.abs(a - b) < 0.005`.
- **Inputs are bound by field id**: renaming the field an input reads keeps the rule working. Deleting a field that another field's rule reads is refused (`Extract:FieldReadByValidationRule`) until that rule changes, so no check stops checking unnoticed. The field API (`FieldDefinitionDto.validationRules`) gives an input's `fieldId`; a [type pack](#importing-and-exporting-types) gives its `fieldName`.
- **Saving** a field checks that every expression parses as one expression, not a function, and every input is another field of the same type. On `UpdateFieldDefinitionDto`, `validationRules: null` leaves the rules as they are and an empty list removes them, so a client that does not know about rules does not erase them.

When the rules run:

| The values are written by | Rules evaluated |
| --- | --- |
| Field extraction | Every rule of the type; the result replaces the document's warnings |
| An operator's edit (`POST extracted-fields`) | Only the rules that read a value the edit changed, their own field's or an input's. A value corrected to pass clears its warning; a warning on a field whose rules read nothing the edit changed stays |
| [Submitted content](submitting-content.md) | Every rule of the type |

Changing a rule does not re-check the documents that already exist: it applies at their next extraction or edit.

An operator who checks a value and accepts it as it stands dismisses its warning (`ResolveFieldValidationWarningsAsync`); the reason clears when none remain. A clean re-extraction, a type change or deleting the field also clears warnings.

The extraction model is not asked to judge any rule, and a check written into a field's description no longer produces a warning: draft rules from it with [Draft from the description](#drafting-and-trying-rules). Measured on a 114-row bank statement whose values were all extracted correctly, the model warned on 4 of 19 runs (claiming, for example, that 100000 does not equal 100000) and caught 4 of 9 real mismatches; rephrasing the instructions or asking in a separate call did not bring it below that, and a separate call warned on 14 of 19. The same checks in code warned on none of 103 runs and caught every mismatch.

### Drafting and Trying Rules

The structured LLM client drafts rules while a type is configured; when documents are processed no model is asked. A draft is a suggestion: try it on a document, then save it through the field API, which checks it again. This is how Google Document AI, Extend and Affinda let an administrator write rules in words.

| Action | `IFieldValidationRuleDraftAppService` | What it does |
| --- | --- | --- |
| Draft a rule | `POST /api/vault-extract/field-validation-rule-draft/draft` (`DraftAsync`) | Writes one rule of a saved field (`fieldId`) from a requirement in words (`description`, at most 256 characters), which stays the rule's description |
| Draft from the description | `POST /api/vault-extract/field-validation-rule-draft/draft-from-prompt` (`DraftFromPromptAsync`) | Writes a rule for each check the field's saved `Description` states, at most 10, and ignores the rest of the instruction. This is how checks written into a description become rules: draft, try, save, then remove the checks from the description |
| Try rules | `POST /api/vault-extract/field-validation-rule-draft/test` (`TestAsync`) | Runs rules not yet saved (`rules`) over one document's stored values (`documentId`) as rules of the field, and returns each rule's `outcome` and `detail` in order. Calls no model and changes nothing |

Both drafting actions return `items`: the drafts, in the shape `validationRules` takes, each input bound to its field's `fieldId`.

- **What the model sees**: the type's fields (name, type, display name and the JSON schema of the value, which is the shape a rule reads) and the requirement or the field's description. No document content. The display names, the requirement and the description are passed as data the model is told not to follow.
- **What comes back**: only a draft that passes the checks saving applies: its inputs name other fields of the type, its aliases are identifiers, its expression parses as one expression, and its description is one line. Two repairs come first: a function without parameters that the model wrote without calling it (`() => ...`), or called without the parentheses an arrow function needs (`() => { ... }()`), is called, and an input the expression never names, or the rule's own field declared as `value`, is dropped, since it would only skip the rule when its field is absent. On a timeout, a provider error, a description over the type's prompt budget or an unusable answer, `items` is empty.
- **Outcomes**: `0` passed, `1` failed (`detail` is what the rule returned, if anything), `2` skipped (the value or an input is absent), `3` could not be evaluated (`detail` says why).
- **Permissions**: all three need `VaultExtract.FieldDefinitions.Create` or `.Update`. Trying rules also needs read access to the document ([Per-Document-Type Permissions](../configuration/document-type-permissions.md)), and the document must be of the field's type (`Extract:FieldValidationRuleTestDocumentOfAnotherType`). The rules tried are checked as saving checks them, with the same error codes.
- **Saved values**: drafting reads the field's saved definition, so save a new field, or a changed description or table column, before drafting its rules.

In the field editor of the Angular UI, the **Validation rules** section does all of this. Each rule has its description, its expression and the other fields it reads (an alias and a field). **Draft with AI** adds a rule from the sentence typed beside it, and **Draft rules from prompt** adds one for each check the saved extraction prompt states; both wait until the field is saved, and the second also until the prompt has no unsaved changes. Drafted rules are marked until the field is saved. **Try** runs the rules as they stand, unsaved changes included, on one of the type's 20 newest documents, and shows each rule's outcome beside it until a rule changes. The rules are saved with the field.

## Importing and Exporting Types

A **type pack** is a JSON description of one type and its fields, without ids or tenant, so a schema can move between layers or deployments.

- **Export**: `GET /api/vault-extract/document-type-packs/{id}` for one type, `GET /api/vault-extract/document-type-packs` for every type of the layer.
- **Import**: `POST /api/vault-extract/document-type-packs/import` with 1 to 100 packs and a mode:
  - `CreateOrUpdate` (default): creates missing types and fields, overwrites existing ones (matched by type code and field name).
  - `CreateOnly`: creates missing ones and skips existing ones.

Import never deletes anything. Every pack is validated (version, field types, prompt budget, validation rules) before anything is written, and a type or field whose code or name belongs to a soft-deleted row is refused. A field's `validationRules` (pack version 3) name the fields their inputs read by `fieldName`, a field of the same pack or of the existing type; a pack without them (every version 1 and 2 pack) leaves an existing field's rules as they are, and an empty list removes them. Packs written by older versions (pack version 1, with `dataType` instead of `fieldTypeName`) are converted on import. Importing needs `VaultExtract.DocumentTypes.Create` and `VaultExtract.FieldDefinitions.Create`, plus the matching `.Update` permissions to overwrite.

```json
{
  "version": 3,
  "typeCode": "invoice.standard",
  "displayName": "Invoice",
  "description": "A supplier invoice requesting payment.",
  "confidenceThreshold": 0.7,
  "priority": 10,
  "duplicateScope": 0,
  "fields": [
    {
      "name": "invoice_no",
      "displayName": "Invoice number",
      "description": "The invoice number printed on the document.",
      "fieldTypeName": "Text",
      "configuration": {},
      "displayOrder": 0,
      "isRequired": true,
      "isSearchable": true,
      "isUniqueKey": true,
      "validationRules": []
    },
    {
      "name": "tax",
      "displayName": "Tax",
      "fieldTypeName": "Number",
      "configuration": {},
      "displayOrder": 1,
      "isRequired": false,
      "isSearchable": true,
      "isUniqueKey": false,
      "validationRules": [
        {
          "description": "Tax is 10% of the subtotal",
          "expression": "Math.abs(value - subtotal * 0.1) < 1",
          "inputs": [ { "alias": "subtotal", "fieldName": "subtotal" } ]
        }
      ]
    }
  ]
}
```

## Permissions

| Permission | Allows |
| --- | --- |
| `VaultExtract.DocumentTypes` | Viewing types, including deleted ones, and exporting packs |
| `VaultExtract.DocumentTypes.Create` / `.Update` / `.Delete` | Creating, editing, deleting and restoring types |
| `VaultExtract.DocumentTypes.ManagePermissions` | Granting per-type permissions ([Per-Document-Type Permissions](../configuration/document-type-permissions.md)) |
| `VaultExtract.FieldDefinitions` | Viewing deleted fields |
| `VaultExtract.FieldDefinitions.Create` / `.Update` / `.Delete` | Creating, editing, deleting and restoring fields; the AI drafting helpers |

The list of a layer's active types and fields is also readable with `VaultExtract.Documents`, because the document pages need it. A type's schema is not confidential.

## See Also

- [Classification](../pipeline/classification.md)
- [Documents and Review](documents.md)
- [Reprocessing](../pipeline/reprocessing.md)
- [REST API](../egress/rest-api.md)
- [Data Download](../egress/data-download.md)

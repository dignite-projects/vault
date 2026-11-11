---
name: abp-document-boundary-check
description: Check that the Document aggregate still holds only infrastructure data, and scan for business-specific fields such as contract amount, expiration date, invoice number, and similar domain-specific data. Use this after editing Document.cs, when adding a business module, or before closing a Slice.
---

# Document Aggregate Boundary Check

## Background

`CLAUDE.md` ("Field architecture") describes how `Document` is shaped today, and this skill checks a change against that shape:

- `Document` is a channel-layer infrastructure aggregate root. It holds file storage, lifecycle state, text extraction output, AI classification output, and type-bound extracted field values.
- Business facts (contract amount, expiration date, counterparty name, invoice number) live in the downstream module's own aggregate root, which persists them after receiving `DocumentClassifiedEto` / `DocumentReadyEto`.

Decision rule: if a field only makes sense in a specific business scenario such as contracts, invoices, reimbursements, licenses, HR, medical claims, or insurance, it does not belong on `Document`.

## Fields Currently On Document.cs

The following fields are infrastructure-level:

| Field Category | Field Names (Examples) |
| --- | --- |
| Multi-tenancy | `TenantId` |
| File storage | `FileOrigin` including `BlobName`, `OriginalFileName`, `ContentType`, and related file metadata |
| Cabinet / organization dimension | `CabinetId` |
| Lifecycle | `LifecycleStatus` |
| Text extraction | `Markdown` as the only text payload, `Title` as a Markdown-derived display snapshot, `Language`, `ExtractionMetadata` |
| Type-bound extracted field values | `ExtractedFieldValues` (`IReadOnlyCollection<DocumentExtractedField>`) |
| AI classification common metadata | `DocumentTypeId` as the internal immutable association, external wire-format `DocumentTypeCode`, `ClassificationConfidence` |
| Human review | `ReviewDisposition`, `ReviewReasons`, `RejectionReason` |

Notes:

- `DocumentTypeId` / `DocumentTypeCode` are generic document type identifiers, not business fields.
- `Markdown` is the only text payload. A parallel text field such as `ExtractedText`, `Summary`, or `RawText` would put a second text payload on the egress.
- `ExtractionMetadata` stores provenance only, such as provider name, native payload blob manifest, and integrity or quality signals. It is not a business-field bag.

## Business-Specific Patterns To Look For

A field name or property meaning that only applies to a specific business scenario belongs in that downstream business module's own aggregate root. Common signs:

- Contract-related: `Amount`, `Currency`, `ContractNumber`, `EffectiveDate`, `ExpirationDate`, `ExpiryDate`, `Counterparty`, `CounterpartyName`, `SignedAt`, `SignedBy`, `ContractType`
- Invoice-related: `InvoiceNumber`, `InvoiceCode`, `TaxAmount`, `TaxRate`, `Buyer`, `Seller`, `IssuedAt`, `LineItems`
- Reimbursement / finance: `ReimbursementCategory`, `Reimbursable`, `CostCenter`, `Project`
- License / HR: `HolderName`, `IDNumber`, `IssuingAuthority`, `LicenseNumber`, `ValidFrom`, `ValidUntil`
- Any field containing industry-specific terms such as `PolicyNumber`, `PatientId`, or `ClaimAmount`
- Vectorization state such as `HasEmbedding`, `EmbeddingStatus`, or similar. Retrieval belongs to the planned `vault.index` module, which keeps its own state.

## Trigger Scenarios

Run this check when any of the following is true:

1. The user edited `modules/vault.extract/src/Dignite.Vault.Extract.Domain/Documents/Document.cs`.
2. The user added a property to `modules/vault.extract/src/Dignite.Vault.Extract.Application.Contracts/Documents/DocumentDto.cs`. The DTO is the external projection of `Document`, so polluting the DTO is equivalent to polluting the aggregate.
3. The user says they want to "add field X to Document".
4. A new EF Core migration touches the `VaultDocuments` table.
5. The user runs `/abp-document-boundary-check`.

## Execution Steps

1. **Read the current Document field inventory**

   Use Grep or rg to search property declarations in:

   - `modules/vault.extract/src/Dignite.Vault.Extract.Domain/Documents/Document.cs`
   - `modules/vault.extract/src/Dignite.Vault.Extract.Application.Contracts/Documents/DocumentDto.cs`
   - `modules/vault.extract/src/Dignite.Vault.Extract.EntityFrameworkCore/EntityFrameworkCore/VaultExtractDbContextModelCreatingExtensions.cs` (the `builder.Entity<Document>` block)

2. **Classify ownership for each field**

   Compare each field against the field categories and the business-specific patterns above. A name that matches a pattern is not a finding by itself. Answer these questions first:

   - Does the field's meaning hold across all document types? Infrastructure if yes; business-specific if no.
   - Is it metadata, state, or orchestration data, or is it a business fact? Infrastructure for the former; business-specific for the latter.
   - Is it written by channel-layer pipeline code, or by a downstream business module / business-specific extractor? Infrastructure for the former; business-specific for the latter.

3. **If a field is business-specific, say where it belongs**

   Report:

   - The field name.
   - The business domain it belongs to, such as contracts, invoices, reimbursements, licenses, or HR.
   - Where it fits instead: the downstream consumer's own aggregate root, filled after `DocumentClassifiedEto` / `DocumentReadyEto`; or, if the customer wants it extracted per document type, a type-bound field configured through mechanism B.

4. **If none is found, give a short confirmation**

   List the current fields and their categories, and report the checked change range, such as the latest `git diff Document.cs`.

## What Not To Do

- This skill reports; it does not edit `Document.cs`.
- Do not misclassify existing generic metadata fields such as `ReviewDisposition`, `DocumentTypeId`, or `ExtractionMetadata` as business-specific.
- Do not apply the same rule to aggregate-internal infrastructure entities such as `DocumentExtractedField` or `DocumentPipelineRun`. This boundary check is about the `Document` aggregate root itself.

## References

- Root `CLAUDE.md` -> "Field architecture", "Markdown-first", and "OUT of scope"
- Root `CLAUDE.md` -> "ABP conventions" and "Architecture" for aggregate design and dependency direction

---
description: "Dignite Vault Extract sub-document segmentation: the DocumentSegment ledger and the derived Document, what re-parse does to them, the two-Kind assumption, and the coupled state"
paths:
  - "modules/vault.extract/src/**/Segmentation/**/*.cs"
  - "modules/vault.extract/src/**/DerivedDocumentSpawner.cs"
  - "modules/vault.extract/src/**/ContainerMarker*.cs"
  - "modules/vault.extract/src/**/Segments/**/*.cs"
  - "modules/vault.extract/src/**/DocumentSegment*.cs"
---

# Sub-document segmentation

## A sub-document has two representations

It exists first as a `DocumentSegment` (a ledger row, before spawn) and then as a derived `Document`. Both are needed:

- Detection is one expensive LLM pass. The ledger holds "detected but not yet spawned", so the job can resume without paying for the split again. A `Document` cannot hold that state, because the pipeline takes it over the moment it exists.
- Retraction reads `Kind` from the ledger row after the derived document already exists.

What follows from that:

- **Ledger rows are not deleted after spawn.** They go only together with the children they route, through `SubDocumentRetractor`. A Spawned row's `SegmentKey` is the only barrier against spawning the same child twice, and a Pending row is a constituent whose spawn has not happened yet. A cleanup that deletes rows out of band breaks both.
- **The ledger is on no egress** (REST / MCP / ETO). Downstream sees a sub-document only as a `Document` row with `OriginDocumentId` set.
- **Copies made at spawn**: `SegmentKey → OriginConstituentKey`, `SourceDocumentId → OriginDocumentId`, `SliceText → Document.Markdown` (a one-way seed), `TenantId`.
- **A sub-document has no `FileOrigin`.** The parse job seeds its Markdown from `SliceText` and never touches a blob. Its source is the parent's blob, reached through `OriginDocumentId`.
- **So the parent outlives its children.** `DeleteAsync` throws `Extract:DocumentHasSubDocuments` while a live child exists. `PermanentDeleteAsync` throws `Extract:DocumentHasSubDocumentsPermanentDelete` while any child exists, recycle-bin ones included, because a hard delete reclaims the blob and a soft-deleted child can be restored. The retraction paths soft-delete children through `IDocumentRepository` directly and do not pass these guards.

## Re-parse regenerates the sub-documents

A document's sub-documents derive from its current Markdown. A re-parse replaces the Markdown, so in the same unit of work `SubDocumentRetractor.RetractAllAsync` soft-deletes every child of either Kind (one `DocumentDeletedEto` each), deletes every ledger row sourced from the document, and `Document.ReplaceParseOutput` clears `IsSegmented`. Classification then splits the new Markdown from scratch.

- **Restore follows the ledger.** `RestoreAsync` refuses a sub-document whose ledger row no longer exists (`IsRoutedBySourceLedgerAsync`, `Extract:DocumentRestoreSuperseded`).
- **A detection pass over replaced Markdown drops its result.** `DocumentSegmentationJob` re-checks in each write unit of work that the source still carries the Markdown it sent to the LLM (`IsDetectedMarkdownCurrent`). Without that, a pass started before the re-parse would insert slices of the old text.
- **A sub-document cannot be re-parsed itself** (`Extract:DocumentReparseSubDocument`). It has no file; re-parse the parent.

## The subsystem assumes exactly two Kinds {Text, Figure}

- A **Text** row belongs to a container bundle (`IsContainerIndependent()` is false). When the source is reclassified to a concrete type, the row goes inert and its spawned child is retracted.
- A **Figure** row is an embedded standalone document (`IsContainerIndependent()` is true). It spawns whether the parent is a bundle or a concrete document, and a container→concrete reclassify keeps it. Its transcription also stays inline in the parent's Markdown.

Three places are binary: `DocumentSegmentKind.IsContainerIndependent()` (an exhaustive switch that throws on an unhandled kind), the `Strip` vs `ExtractBodies` child-seed split, and the Text-only retraction filter in `ContainerMarkerClearedEventHandler`. A third kind breaks all three.

## Coupled state

`DocumentSegment` (`Status`, `Kind`) and `Document` (`IsContainer`, `IsSegmented`) are two coupled state machines:

- Every container↔concrete transition goes through `Document.SetContainerFlag`, which clears `IsSegmented`.
- `IsSegmented` is the resume gate. It is set atomically with the segment rows on success; completion is not inferred from the rows.
- A slice that is not a document (cover / index / transmittal) is skipped, not persisted.
- A live routed child exists exactly when its ledger row exists, for as long as the source lives.

A change to these transitions is covered by the round-trip tests (container→type→container, asserting the segment rows and the sub-documents' terminal states).

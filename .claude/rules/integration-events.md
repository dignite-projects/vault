---
description: "Dignite Vault Extract egress event contracts: multi-stage event table, lifecycle events, Ready gate, at-least-once + EventTime idempotency delivery semantics"
paths:
  - "modules/vault.extract/src/**/*Eto.cs"
  - "modules/vault.extract/src/**/*EventHandler*.cs"
  - "modules/vault.extract/src/**/*IntegrationEvent*.cs"
---

# Egress events

Payloads are thin (ID + key metadata); a consumer pulls the details through REST or MCP. The wire names are published in `docs/en/egress/integration-events.md` and pinned by `EtoContract_Tests.Every_Eto_Wire_Name_Is_Pinned`: changing one breaks every subscriber.

## Pipeline events

| Event | Fires when | Gated by Ready |
|---|---|---|
| `DocumentUploadedEto` | upload completed | No |
| `DocumentTextExtractedEto` | text extraction completed; fires again after a re-parse, because the Markdown was replaced | No |
| `DocumentClassifiedEto` | classification completed | No |
| `DocumentReadyEto` | the pipeline is complete and no blocking review reason is set; fires again when field extraction re-runs on a Ready document, or an operator edits its fields or corrects its Markdown | **Yes** |

A derived sub-document (`OriginDocumentId` set) has no blob of its own, so its `DocumentUploadedEto` carries `FileName = null`, `FileSize = 0`, `ContentType = null`. A consumer that sums `FileSize` for storage or quota therefore does not count the parent's bytes again.

## Lifecycle events

Not gated by Ready.

| Event | Fires when | What a consumer does |
|---|---|---|
| `DocumentDeletedEto` | a document is soft-deleted; also for each sub-document withdrawn because its parent was re-parsed or a container was reclassified to a concrete type | archive derived data (a withdrawn sub-document cannot be restored) |
| `DocumentRestoredEto` | restored from the recycle bin | un-archive |
| `DocumentPermanentlyDeletedEto` | permanently deleted, with its file and archive blob | delete derived data |
| `DocumentReclassifiedToContainerEto` | a concrete-typed document was re-recognized as a container; its type and type-bound fields are cleared. Fires only on a real transition, not for a fresh upload first classified as a container | retract the record derived from the former type |

## Ready gate

- Only `DocumentReadyEto` is gated. A document that fails the gate is still stored, its earlier events still fire, and it enters the operator review queue.
- The gate is "no blocking review reason is set". `ReviewReasonPolicy.Blocking` is the single place that declares which reasons block, and `DeriveLifecycle` refuses Ready while any of them is present. Today: `UnresolvedClassification`, `DuplicateSuspected`, `FieldExtractionIncomplete`, `FieldValidationWarning`. `MissingRequiredFields` and `SegmentationIncomplete` do not block.
- **A blocking reason needs an operator-reachable way to clear it that re-derives the gate** (`ReDeriveLifecycleAsync`). Without one, the document is stranded short of Ready. The remedies today: `UnresolvedClassification` → assign a type or reject; `DuplicateSuspected` → allow or delete; `FieldExtractionIncomplete` → enter the field values by hand.

## Delivery

- Events go through ABP's transactional outbox: the change and the event are committed in the same UoW, so delivery is at-least-once and an event is never lost.
- The channel keeps no event state table, does no in-flight replacement, and waits for no ack. Deduplication and ordering are the consumer's job.
- Every ETO carries `EventTime` (`Clock.Now` at publish time). A consumer keeps a high-water mark per `(DocumentId, EventType)`: an incoming event whose `EventTime` is not later than the mark is a redelivery or a stale event and is discarded; otherwise it is applied and becomes the new mark. An ABP consumer can instead enable the inbox (`ConfigureEventInbox()`) and get exactly-once by `MessageId`.

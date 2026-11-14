---
description: "Text-extraction stack (Dignite Vault Parse, modules/vault.parse) and how Extract consumes it: contracts, providers, native payload archiving, re-parse"
paths:
  - "modules/vault.parse/src/**/*.cs"
  - "modules/vault.extract/src/**/*Parse*/**/*.cs"
  - "modules/vault.extract/src/**/*Extractor*.cs"
  - "modules/vault.extract/src/**/*Markdown*.cs"
---

# Text extraction (Vault Parse)

The stack is the module `modules/vault.parse/` (packages `Dignite.Vault.Parse*`, module prefix `VaultParse`). It references nothing else in this repo. Extract consumes it only through `Dignite.Vault.Parse.Abstractions`, and the host chooses the providers. Its config sections (`VisionLlmOcr`, `PdfExtractor`, `OpenXmlExtractor`, `PaddleOcr`, `AzureDocumentIntelligence`) sit at the configuration root.

## Projects

- **`Dignite.Vault.Parse.Abstractions`**: `ITextExtractor`, `TextExtractionContext`, `TextExtractionResult`, `NativePayload`, `ImageOcrMarkup`.
- **`Dignite.Vault.Parse`**: the orchestrator. `DefaultTextExtractor` dispatches by file extension: images go to OCR, everything else to the Markdown provider chosen by `CanHandle(ext)` + `Priority`. A PDF with no usable text layer falls back to whole-page OCR. It also declares `IMarkdownTextProvider`.
- **`Dignite.Vault.Parse.Ocr`**: the OCR contract (`IOcrProvider` / `OcrOptions` / `OcrResult`). An OCR provider references only this project.

**OCR providers are mutually exclusive: the host enables exactly one.** Switching means changing both the host's `[DependsOn]` and its `ProjectReference`, and the keyed Vision `IChatClient` registration when VisionLlm is involved.

- `Ocr.VisionLlm` (the host default): a vision LLM transcribes images and image-only PDFs, through the keyed Vision `IChatClient`. PDFs are rasterized page by page with PDFium + SkiaSharp.
- `Ocr.PaddleOcr`: a local sidecar (PP-StructureV3).
- `Ocr.AzureDocumentIntelligence`: the cloud option.

**Markdown providers coexist and are picked per file.**

- `Parse.Pdf` (PdfPig) owns `.pdf`. It extracts the text layer and the embedded raster images, transcribes each image through the host's `IOcrProvider`, and inlines the transcription at the image's reading position, wrapped in `ImageOcrMarkup`. A PDF with no text layer returns empty, so the orchestrator's OCR fallback handles it and nothing is OCR'd twice. Vector-only graphics are not seen. A dropped image, a truncated figure OCR or a cap hit sets the completeness signal to false.
- `Parse.OpenXml` owns `.docx` and `.pptx`.
- `Parse.ElBrunoMarkItDown` is the catch-all (`CanHandle` always true, lowest priority), so leaving out a specialized provider degrades that extension instead of failing it. **It transcodes `.csv` / `.tsv` / `.txt` to UTF-8 first** (`TextEncodingNormalizer`): the underlying library only sniffs a BOM, so a no-BOM CP932 CSV, which is what Excel on Japanese Windows exports, would decode entirely to U+FFFD. **That decoding must never throw**: a failed parse run is retried by the background job and would never succeed.

## Native payload and provenance

A provider's native output (bounding boxes, table cells, confidence) cannot be re-derived later, because a re-run on a newer provider version no longer lines up with the Markdown already stored. So it is captured with the Markdown at extraction time.

- `TextExtractionResult.NativePayload` carries it (`Content` + `ContentType` + `SchemaName`). An OCR provider passes it through flat fields on `OcrResult`, which `DefaultTextExtractor` maps, because the Ocr project does not reference Abstractions. A provider with no spatial model leaves it null.
- The parse job archives it to the blob `extraction-native/{documentId}`; a re-extraction overwrites it. It never enters the database or the Markdown string.
- **Archiving fails open.** No payload, a payload over `DocumentConsts.MaxNativePayloadArchiveBytes`, or a blob write failure logs a warning and leaves the manifest null. Text extraction still succeeds.
- `Document.ExtractionMetadata` (a JSON column) holds `ProviderName`, the `NativePayloadManifest`, and the completeness signal `IsComplete` + `IncompleteReason`. Old JSON without those fields reads as complete.
- The egress exposes none of the provenance. It does expose completeness: `DocumentDto.ExtractionIsComplete` + `ExtractionIncompleteReason`. Completeness does not gate Ready; the consumer decides what to do with an incomplete document.
- A permanent delete removes the archive blob along with the document.

## Markdown

Every provider returns Markdown: `ITextExtractor` / `IMarkdownTextProvider` / `IOcrProvider` have one text output, so classification, field extraction and downstream chunkers consume a single format. A provider whose engine returns plain text wraps it into flat paragraphs itself. For unstructured content that Markdown carries no extra signal; it is only the common container. Internal LLM prompts say that the input is Markdown.

`Microsoft.Extensions.DocumentExtraction` (experimental in dotnet/extensions 11.0.0) has the same positioning as Parse's contracts: an engine-neutral extraction client that returns a structured element tree with page and region references. The plan is to rebuild Parse on it once it leaves experimental status, with no adapter layer between the two. Three things Parse relies on that it lacks (a failed-page signal, a durable native result, a language hint) were raised upstream in dotnet/extensions#7587.

## Re-parse

`Document.Markdown` is written once by the first parse; `SetMarkdown` throws `MarkdownIsImmutable` after that. It then changes in two ways: an operator correction (`CorrectMarkdown`, Markdown only) and a re-parse (`ReplaceParseOutput`).

- **A re-parse** (`DocumentAppService.ReparseAsync`) re-runs text extraction from the stored file and then classification, as for a fresh upload. It replaces Markdown, Title, Language and ExtractionMetadata as one unit, withdraws every sub-document split from the old Markdown (see `sub-document-segmentation.md`), and re-runs classification even over a confirmed type. It is refused on a sub-document.
- Its Complete phase is one transaction and queues classification before completing its own run, so the document never derives Ready in between.
- **The mode travels in the job args** (`DocumentParseJobArgs.IsReparse`), set by whoever queues the run. The job never infers it from the document's state, so a redelivered first-parse job never turns into a re-parse: it ends when its run has ended or a later attempt superseded it, and otherwise fails on `MarkdownIsImmutable`.
- A re-parse is announced: it re-publishes `DocumentTextExtractedEto`, each withdrawn sub-document fires `DocumentDeletedEto`, and classification fires its own events. Downstream rebuilds from the later `EventTime`.

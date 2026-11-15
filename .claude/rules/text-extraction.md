---
description: "Text-extraction stack (Dignite Vault Parse, modules/vault.parse) and how Extract consumes it: the router and its engines on Microsoft.Extensions.DocumentExtraction, the document tree, re-parse"
paths:
  - "modules/vault.parse/src/**/*.cs"
  - "modules/vault.extract/src/**/*Parse*/**/*.cs"
  - "modules/vault.extract/src/**/*Extractor*.cs"
  - "modules/vault.extract/src/**/*Markdown*.cs"
---

# Text extraction (Vault Parse)

The stack is the module `modules/vault.parse/` (packages `Dignite.Vault.Parse*`, module prefix `VaultParse`). It references nothing else in this repo. Extract consumes it only through `Dignite.Vault.Parse.Abstractions`, and the host chooses the engines. Its config sections (`VisionLlmOcr`, `PdfExtractor`, `OpenXmlExtractor`, `PaddleOcr`, `AzureDocumentIntelligence`) sit at the configuration root.

## Projects

Parse is built on `Microsoft.Extensions.DocumentExtraction`: every engine is a `DocumentExtractionClient` and returns the document tree of `Microsoft.Extensions.Documents` (`ExtractionDocument`: sections, text elements with roles and heading levels, tables with spans, images with OCR text, source pages and polygons). Both packages are experimental in dotnet/extensions 11 and used on purpose: `EXTEXP0019` / `MEDE0001` are suppressed in the `Directory.Build.props` of `modules/vault.parse/` and `modules/vault.extract/`.

- **`Dignite.Vault.Parse.Abstractions`**: `ParseExtractionClient` (an engine: `ProviderName`, `Priority`, `CanExtract(extension, mediaType)`) and `ParseOcrClient` (an OCR engine: claims images and PDFs at `ParseExtractionPriorities.Ocr`); `ParseExtractionOptions` (the file name and language hints carried in `DocumentExtractionOptions.AdditionalProperties`) and the metadata extensions (`SetProvider`, `SetLanguage`, `MarkIncomplete`, `IsComplete`); Vault's conventions over the tree (`ParseDocumentConventions.cs`: element roles, content properties, metadata keys); `ExtractionDocumentMarkdown` (renders the stored Markdown from a tree); `ImageOcrMarkup`.
- **`Dignite.Vault.Parse`**: `ParseRoutingClient`, the router, registered as `DocumentExtractionClient` (and never as an engine). It picks the highest-priority engine whose `CanExtract` claims the file, by the lower-case extension of the file name and the media type, gives it a seekable copy of the stream, and sends a PDF whose engine found no letter or digit to the OCR engine. It also holds `MarkdownDocumentReader` (reads Markdown-native engine output into a tree; an indented block is kept as written, because in engine output it is the continuation of a footnote, not code) and the helpers the engines share.

Every engine is found through `[ExposeServices(typeof(ParseExtractionClient), ...)]`, an OCR engine also as `ParseOcrClient`. None of them matches ABP's naming convention, so a dropped attribute silently removes the engine; each engine has a registration test.

**OCR engines are mutually exclusive: the host enables exactly one.** The PDF and OpenXML engines inject the single `ParseOcrClient` for figures. Switching means changing both the host's `[DependsOn]` and its `ProjectReference`, and the keyed Vision `IChatClient` registration when VisionLlm is involved.

- `Ocr.VisionLlm` (the host default): a vision LLM transcribes images and image-only PDFs, through the keyed Vision `IChatClient`, one section per page. PDFs are rasterized page by page with PDFium + SkiaSharp.
- `Ocr.PaddleOcr`: a local sidecar (PP-StructureV3).
- `Ocr.AzureDocumentIntelligence`: the cloud option.

**Digital engines coexist and are picked per file.**

- `Parse.Pdf` (PdfPig) owns `.pdf`, one section per page. It extracts the text layer and the embedded raster images, transcribes each image through the OCR engine, and inlines the transcription at the image's reading position, wrapped in `ImageOcrMarkup`. A PDF with no text layer returns an empty document, so the router's OCR fallback handles it and nothing is OCR'd twice. Vector-only graphics are not seen. A dropped image, a truncated figure OCR or a cap hit marks the document incomplete.
- `Parse.OpenXml` owns `.docx` (`DocxExtractor`) and `.pptx` (`PptxExtractor`, one section per slide).
- `Parse.ElBrunoMarkItDown` is the catch-all (`CanExtract` always true, `ParseExtractionPriorities.Fallback`), so leaving out a specialized engine degrades that extension instead of failing it. **It transcodes `.csv` / `.tsv` / `.txt` to UTF-8 first** (`TextEncodingNormalizer`): the underlying library only sniffs a BOM, so a no-BOM CP932 CSV, which is what Excel on Japanese Windows exports, would decode entirely to U+FFFD. **That decoding must never throw**: a failed parse run is retried by the background job and would never succeed.

## How Extract consumes it

`DocumentParseBackgroundJob` injects `DocumentExtractionClient` and calls `ExtractAsync(blob, documentId, contentType, ParseExtractionOptions.Create(originalFileName))`. It stores `ExtractionDocumentMarkdown.Render(tree)` as the Markdown, the tree's language, and `DocumentParseMetadata` (engine name and completeness). The tree itself is not persisted, and neither is an engine's native answer (`ExtractionDocument.RawRepresentation`: Azure's `AnalyzeResult`, the PaddleOCR sidecar's JSON).

- `Document.ExtractionMetadata` (a JSON column) holds `ProviderName` and the completeness signal `IsComplete` + `IncompleteReason`. Old JSON without completeness reads as complete; old JSON with a `NativePayloadManifest` reads with the manifest ignored.
- The egress exposes none of the provenance. It does expose completeness: `DocumentDto.ExtractionIsComplete` + `ExtractionIncompleteReason`. Completeness does not gate Ready; the consumer decides what to do with an incomplete document.
- Parses before the move to `Microsoft.Extensions.DocumentExtraction` archived the native answer to the blob `extraction-native/{documentId}`; a permanent delete still removes that key.

## Markdown

Every engine's tree renders to Markdown, so classification, field extraction and downstream chunkers consume a single format. An engine whose source has no structure fills the tree with flat paragraphs. For unstructured content that Markdown carries no extra signal; it is only the common container. Internal LLM prompts say that the input is Markdown.

A Markdown-native answer read into the tree and rendered back is the same Markdown up to blank lines and the indentation of nested list items (each is indented to its parent's content), and scores the same on the evaluation corpus. Three things Parse relies on that the shared contract lacks (a failed-page signal, a durable native result, a language hint) were raised upstream in dotnet/extensions#7587; until it answers, Vault carries the first as a metadata key and the last as an option key.

## Re-parse

`Document.Markdown` is written once by the first parse; `SetMarkdown` throws `MarkdownIsImmutable` after that. It then changes in two ways: an operator correction (`CorrectMarkdown`, Markdown only) and a re-parse (`ReplaceParseOutput`).

- **A re-parse** (`DocumentAppService.ReparseAsync`) re-runs text extraction from the stored file and then classification, as for a fresh upload. It replaces Markdown, Title, Language and ExtractionMetadata as one unit, withdraws every sub-document split from the old Markdown (see `sub-document-segmentation.md`), and re-runs classification even over a confirmed type. It is refused on a sub-document.
- Its Complete phase is one transaction and queues classification before completing its own run, so the document never derives Ready in between.
- **The mode travels in the job args** (`DocumentParseJobArgs.IsReparse`), set by whoever queues the run. The job never infers it from the document's state, so a redelivered first-parse job never turns into a re-parse: it ends when its run has ended or a later attempt superseded it, and otherwise fails on `MarkdownIsImmutable`.
- A re-parse is announced: it re-publishes `DocumentTextExtractedEto`, each withdrawn sub-document fires `DocumentDeletedEto`, and classification fires its own events. Downstream rebuilds from the later `EventTime`.

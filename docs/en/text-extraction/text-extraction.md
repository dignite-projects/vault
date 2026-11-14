```json
//[doc-seo]
{
    "Description": "How Dignite Vault Extract turns uploads into Markdown: the Markdown-first contract, the Parse router and its engines, OCR engine choice, extractor options and custom engines."
}
```

# Text Extraction

Every document uploaded to Dignite Vault Extract passes through a text-extraction stage that converts the raw bytes into **Markdown**. The Markdown then drives the channel's internal pipeline — classification, sub-document segmentation, field extraction and title generation — and is the only text payload Dignite Vault Extract exposes to downstream consumers (RAG platforms, business systems, MCP clients) through REST and MCP. The accepted file types and the upload size limit are listed in [Documents and Review](../documents/documents.md#uploading).

## Markdown-First Contract

Dignite Vault Extract is an AI-native platform. Markdown is the **single text payload** of the pipeline. But what Markdown contributes depends on whether the source document has structure — be honest about both cases:

**With structure — real signal.** For contracts, reports, CSV/TSV, XLSX, DOCX/PPTX with headings and layout, or layout-aware OCR output (PP-StructureV3, Azure DI `prebuilt-layout`): headings, tables and lists are not formatting decoration — they are semantic signals that downstream RAG chunkers (header-path injection) and Dignite Vault Extract's own LLM prompts (system prompt: "input is Markdown") rely on. Use them in full.

**Without structure — container, not signal.** For OCR loose paragraphs, plain `.txt`, PP-OCRv4 line dumps, single-line notes: the Markdown wrapper is a **container name**, not a signal upgrade — `string.Join("\n\n", paragraphs)` and the plain text it wraps are byte-for-byte indistinguishable. We still route this through the Markdown contract so internal pipelines (classification / field extraction / title generation) and downstream consumers (RAG / business systems) stay on one shape. The wrapper buys uniformity, not LLM comprehension.

Contract obligations regardless of structure:

- Parse is built on [`Microsoft.Extensions.DocumentExtraction`](https://github.com/dotnet/extensions): every engine — built-in or third-party — is a `DocumentExtractionClient` that returns a document tree (`Microsoft.Extensions.Documents.ExtractionDocument`: sections, text with roles and heading levels, tables with spans, images with their OCR text, source pages). `Document.Markdown` is rendered from that tree by `ExtractionDocumentMarkdown`.
- An engine whose source has no structure still fills the tree with flat paragraphs rather than exposing a parallel raw-text channel; an engine that answers in Markdown (a vision model, MarkItDown, PP-StructureV3) is read into the tree by `MarkdownDocumentReader`. Reading that Markdown and rendering it again gives the same Markdown, so this costs nothing on the [evaluation corpus](parse-evaluation.md).
- `Document.Markdown` is the **only** text field on the `Document` aggregate. Consumers that need plain text strip on demand via `Dignite.Vault.Extract.Documents.MarkdownStripper.Strip(...)`; nothing is persisted in stripped form.

**Markdown-first is an engineering default, not a creed.** Out-of-band signals (coordinates, confidence, page metadata, form key-value structure, stamp positions) are **orthogonal** to Markdown and belong in the tree: its source pages and element source references, its element roles, and the engine's own answer in `ExtractionDocument.RawRepresentation`. **Forbidden**: stuffing such signals back into the Markdown string. Vault's conventions over the tree (element roles, content properties, document metadata keys) are in [`ParseDocumentConventions`](https://github.com/dignite-projects/vault/blob/main/modules/vault.parse/src/Dignite.Vault.Parse.Abstractions/ParseDocumentConventions.cs). Extract stores only the Markdown rendered from the tree today.

Source contract: [`ParseExtractionClient`](https://github.com/dignite-projects/vault/blob/main/modules/vault.parse/src/Dignite.Vault.Parse.Abstractions/ParseExtractionClient.cs) (an engine), [`ParseRoutingClient`](https://github.com/dignite-projects/vault/blob/main/modules/vault.parse/src/Dignite.Vault.Parse/ParseRoutingClient.cs) (the router Extract injects as `DocumentExtractionClient`).

## Two Extraction Paths

```
Upload → DocumentParseBackgroundJob → DocumentExtractionClient (ParseRoutingClient)
              │
              ├─→ digital text layer? (PDF / DOCX / PPTX / XLSX / TXT / CSV / TSV …)
              │     └─→ the engine that claims the file, by extension (or media type) and priority:
              │           • .pdf → PdfExtractor (PdfPig: text layer + embedded-image OCR, inlined)
              │           • .pptx / .docx → PptxExtractor / DocxExtractor (structure + charts/tables + embedded-image OCR, inlined)
              │           • .xlsx → ElBruno MarkItDown Excel plugin
              │           • .csv / .tsv / .txt and everything else → ElBruno MarkItDown (catch-all)
              │
              └─→ image / scan, or a PDF whose engine found no text?
                    └─→ the OCR engine (Vision-LLM / PaddleOCR / Azure Document Intelligence)

Both paths return the same shape: ExtractionDocument { Sections, SourcePages, Metadata (provider, language, completeness) }
                                  → ExtractionDocumentMarkdown.Render → Document.Markdown
```

The router picks the highest-priority engine that claims the file; the OCR engine claims images and PDFs at a priority below every digital engine, so it reads an image, and a PDF only when the PDF engine is absent or found no text. Hosts wire one or more digital engines plus exactly one OCR engine via `[DependsOn(...)]`; switching engines is a host-level swap with no Application or Domain changes.

## Digital Extraction — Engines (Picked per File)

Digital engines **coexist** and are picked **per file** — unlike the OCR engine, which is host-selected and mutually exclusive. Each engine declares the files it reads via `CanExtract(extension, mediaType)` plus a `Priority`; the router picks the highest-priority engine that reads the file, with ElBruno as the catch-all fallback. **Omitting a specialized engine module makes its extension fall back to ElBruno**, preserving prior behaviour.

| Engine | Owns | Notes |
|---|---|---|
| **PdfExtractor** (`Dignite.Vault.Parse.Pdf`, PdfPig) | `.pdf` | Extracts the digital text layer **and** embedded raster images. Each image is transcribed through the host-selected OCR engine and inlined at its reading position (#301); each page with content is a section of the document, referring to its page, so embedded figures are no longer silently dropped. Vector-only graphics are an accepted blind spot (`GetImages()` does not see them). |
| **PptxExtractor / DocxExtractor** (`Dignite.Vault.Parse.OpenXml`, OpenXML SDK) | `.pptx`, `.docx` | Own the whole PowerPoint / Word pass: rebuild structure (PPTX slide text, plus speaker notes when `IncludeSpeakerNotes` is on, one section per slide, #307; DOCX headings / tables / lists / inline formatting / hyperlinks / text boxes, #308) **and** transcribe embedded raster images through the host-selected OCR engine, render `ChartPart` backing data as Markdown tables, inlining everything at reading position. EMF/WMF vector images are an accepted blind spot. |
| **ElBruno MarkItDown** (`Dignite.Vault.Parse.ElBrunoMarkItDown`) | catch-all (XLSX via its Excel plugin; HTML / TXT / CSV / TSV / RTF / EPUB; also `.docx` when the OpenXml module is absent, and `.pdf` when the Pdf module is absent) | Default fallback; enabled by the host module. XLSX conversion emits each non-empty worksheet as Markdown tables. |

**XLSX resource boundary (#471).** An XLSX is a compressed ZIP package, so the 20 MiB upload limit alone does not bound the memory or CPU required to open it. Before ClosedXML materializes the workbook, the ElBruno engine drains every archive entry through a shared expanded-byte budget and rejects packages exceeding the entry, worksheet, or cell caps. The resulting Markdown has a separate character cap. A limit violation fails the parse run explicitly; it is never persisted as a successful empty extraction.

**Embedded images in digital PDFs (#301).** PdfExtractor reuses the host-selected OCR engine for figure transcription — no separate vision client is wired at the digital-engine layer, and the semantics are transcription only (no chart/describe modes). Image-heavy PDFs are bounded by `PdfExtractorOptions` (`MaxImagesPerPdf`, `MinImagePixels` — tiny decorative images are skipped). When images are dropped (cap reached / undecodable codec such as JBIG2/JPX) or a figure's OCR is truncated, the result is marked incomplete via the #268 completeness signal.

**Searchable / "sandwich" PDFs (#309).** A scan-to-searchable PDF stores, per page, a full-page scan raster plus an (often invisible) OCR text layer over it. PdfExtractor detects this and **skips the full-page raster** instead of re-OCRing it, which would duplicate the already-extracted text and burn a redundant vision call per page. The skip is intentional and non-lossy — the text layer is kept and it does **not** trip the #268 completeness signal (a deliberate skip is not a dropped figure). It is high-precision and errs toward keeping: it fires only when the image's **placement bbox** covers most of the page **and** the text layer reads as a whole-page transcription (vertically spread across the image region, not a thin edge caption band); a predominantly invisible `Tr 3` layer is the canonical confirmation. When unsure it keeps and transcribes the image, so a real full-page figure (figure + caption / infographic / photo) is never dropped. Tunable via `PdfExtractorOptions` (`SkipFullPageScanBackground`, on by default; `FullPageScanCoverageThreshold`, `FullPageScanMinTextLines`, `FullPageScanMinTextVerticalCoverage`, `FullPageScanMinInvisibleTextRatio`). Tiled multi-image scan pages and heavily margined/cropped scans are out of scope for now and still double-OCR.

**Scanned / no-text-layer PDF.** If a PDF has no digital text layer, PdfExtractor returns an empty document — it does **not** OCR its images in that case — and the router hands the file to the OCR engine. There is no double OCR.

**OpenXML PPTX / DOCX (#307 / #308).** The OpenXML engines own the full PowerPoint / Word pass so the image↔text position linkage is preserved: they rebuild the document structure themselves and reuse the host-selected OCR engine for embedded-image transcription (no separate vision client, transcription only), renders charts and tables directly from the OpenXML (no OCR, no vector blind spot), and uses native alt-text (`docPr/@descr`) as the figure caption. A figure (image or chart) inside a table cell is extracted as its own block after the table, since a Markdown cell cannot host a transcription. A nested list item is indented to its parent's content (the width of `- ` or `1. `), so a child under an ordered item still nests in CommonMark. Block-level content controls (`w:sdt`) and custom-XML wrappers are recursed into (in body paragraphs and table cells alike), and legacy VML raster images (`w:pict`/`v:imagedata`) are transcribed alongside DrawingML ones, so neither is silently dropped; an unhandled block — or content-control nesting beyond a safety depth cap — that still carries text trips the completeness signal. Known limitation: VML images are not decorative-size-filtered (a VML shape's size lives in its style attribute, not `wp:extent`), so a tiny VML icon may be transcribed where its DrawingML equivalent would be skipped. DOCX collapses markup-compatibility (`mc:AlternateContent`) on open so a modern text box / picture is not read twice, and applies the accepted view of tracked changes (insertions kept, deletions dropped). Image caps live in `OpenXmlExtractorOptions`; dropped / undecodable images, truncated figure OCR, image-cap hits, unrenderable charts, and per-block parse failures trip the #268 completeness signal. Unlike PDF there is no whole-page OCR fallback, so once the module owns an extension an unopenable file reports empty + incomplete. **Required for `.pptx`** (ElBruno has no PresentationML converter); `.docx` degrades gracefully to ElBruno when the module is absent.

## OCR — Choosing an Engine

Dignite Vault Extract ships three OCR engines. Enable **one** in the host module's `[DependsOn]` (in this repository, `src/Dignite.Vault.Extract.Host/VaultExtractHostModule.cs`, together with the matching `<ProjectReference>`) based on the deployment scenario — the digital engines inject a single `ParseOcrClient` for figures, so the OCR engines are mutually exclusive. The shipped host enables **Vision-LLM**.

| | [Vision-LLM](ocr-vision-llm.md) (shipped default) | [PaddleOCR](ocr-paddleocr.md) | [Azure Document Intelligence](ocr-azure-document-intelligence.md) |
|---|---|---|---|
| Where data goes | Your LLM provider (stays local if the model does) | Local sidecar — never leaves the network | Cloud (Azure region) |
| Setup | Host LLM endpoint + a vision model id | `docker compose up paddleocr` | Azure subscription + AI resource |
| Markdown output | Native (the LLM transcribes to Markdown) | Native (PP-StructureV3 / VL); flat (PP-OCRv4) | Native |
| Cold start | Instant | ~30–60 s first run (model download ~600 MB) | Instant |
| Per-page cost | Paid per token (image + output) | Free | F0 free tier / S0 ~$1.50 / 1000 pages |
| Best at | **Phone photos / thermal receipts** where layout OCR fails (#259); image-only PDFs | Structured scans, CJK documents | Structured scans, forms |

> Cloud LLM OCR (Gemini / Mistral) and Google Document AI were evaluated and rejected — see issue #79 for the rationale (Japanese-language quality, region access, dependency footprint, free-tier shape). The [vision-LLM provider](ocr-vision-llm.md) (#259) is a later, vendor-agnostic `IChatClient`-based path for the photo/receipt scenario specifically.

Each provider's setup lives on its own page: **[Vision-LLM](ocr-vision-llm.md)** · **[PaddleOCR](ocr-paddleocr.md)** · **[Azure Document Intelligence](ocr-azure-document-intelligence.md)**.

### Provider-Agnostic OCR Pipeline Behavior

These apply regardless of which provider is wired:

- Dignite Vault Extract does not auto-switch OCR profiles per document. The OCR provider runs once with the host-configured model; there is no second OCR pass with a guessed specialized mode, and OCR average confidence is no longer a quality gate (#196).
- OCR completion always advances the document to classification. OCR average confidence was removed (#196): it did not reliably predict real quality (skewed pages, blurry scans and layout issues are not reflected in an average score). Quality problems surface later as review reasons (an unresolved classification, a suspected duplicate, missing or invalid field values), which the operator resolves, or fixes by re-parsing the document. See [Documents and Review](../documents/documents.md).
- **Extraction completeness (#268).** An engine may report that it captured only part of a document (e.g. a vision-LLM output truncated at the token cap, or a dropped PDF page). This is carried in the document's metadata (`ParseDocumentMetadata.IncompleteReason`, absent when complete), recorded in `Document.ExtractionMetadata`, and surfaced to downstream consumers on the REST `DocumentDto` as `ExtractionIsComplete` + `ExtractionIncompleteReason`. It is a **quality signal** (distinct from internal extraction provenance, which is not exposed); the channel does not gate Ready on it — consumers decide whether to accept, downgrade, or route to review. Engines that don't report it default to complete.

## Extractor Options

The digital engines bound their work with options bound from configuration.

**`PdfExtractor`** (`PdfExtractorOptions`, package `Dignite.Vault.Parse.Pdf`):

| Key | Default | Description |
| --- | --- | --- |
| `MaxImagesPerPdf` | `50` | Most embedded images transcribed per PDF; beyond it the result is marked incomplete |
| `MinImagePixels` | `4096` (64 × 64) | Smaller images are treated as decorative and skipped |
| `SkipFullPageScanBackground` | `true` | Skip the full-page scan raster of a searchable ("sandwich") PDF instead of OCR-ing it again |
| `FullPageScanCoverageThreshold` | `0.9` | Share of the page an image must cover to count as a full-page scan |
| `FullPageScanMinTextLines` | `15` | Text lines the page must carry to count as a whole-page transcription |
| `FullPageScanMinTextVerticalCoverage` | `0.5` | Share of the image's height the text must span |
| `FullPageScanMinInvisibleTextRatio` | `0.6` | Share of invisible (`Tr 3`) text that confirms a scan layer on its own |
| `ReconstructTables` | `true` | Rebuild detected grids as Markdown tables |

**`PptxExtractor` / `DocxExtractor`** (`OpenXmlExtractorOptions`, package `Dignite.Vault.Parse.OpenXml`):

| Key | Default | Description |
| --- | --- | --- |
| `MaxImagesPerFile` | `50` | Most embedded images transcribed per file; beyond it the result is marked incomplete |
| `MinImagePixels` | `4096` (64 × 64) | Images displayed smaller than this are skipped as decorative |
| `MaxImageBytesPerImage` | `16777216` (16 MiB) | Larger images are not decoded (decompression-bomb guard) |
| `IncludeSpeakerNotes` | `false` | Include PowerPoint speaker notes in the Markdown |

## Adding a Custom OCR / Digital Engine

Derive from `ParseOcrClient` (for image/scan input; it claims images and PDFs at `ParseExtractionPriorities.Ocr`) or `ParseExtractionClient` (for files with a digital text layer), and expose it with `[ExposeServices(typeof(ParseExtractionClient), typeof(YourEngine))]` (an OCR engine also exposes `typeof(ParseOcrClient)`). A digital engine declares the files it reads via `CanExtract(extension, mediaType)` + `Priority` (`ParseExtractionPriorities.Default` for a specialized engine; the catch-all fallback sits at `ParseExtractionPriorities.Fallback`). `ExtractAsync` returns an `ExtractionDocument`; name the engine with `SetProvider`, and record missing content with `MarkIncomplete`. The file name and the language hints arrive in the options (`GetFileName`, `GetLanguageHints`).

The engine lives in its own module project (`Dignite.Vault.Parse.Ocr.<Vendor>` or `Dignite.Vault.Parse.<Vendor>`), references `Dignite.Vault.Parse` (for `MarkdownDocumentReader` and the shared helpers), and is enabled by the host through `[DependsOn(...)]`. Digital engines coexist (picked per file), so adding one does not displace the others.

**Structure is the engine's job, not the router's.** There is no parallel raw-text channel. An engine that answers in Markdown reads it with `MarkdownDocumentReader`; if the underlying OCR engine returns plain text only (e.g. PaddleOCR PP-OCRv4), the engine itself wraps it into paragraphs. Returning an empty document when the engine produced text is a contract violation. Custom OCR engines expose their model choice through engine/host configuration, not through Dignite Vault Extract core profile codes.

## See Also

- OCR engines: [Vision-LLM](ocr-vision-llm.md) · [PaddleOCR](ocr-paddleocr.md) · [Azure Document Intelligence](ocr-azure-document-intelligence.md)
- [Classification pipeline](../pipeline/classification.md) — how the LLM consumes the Markdown
- [AI provider](../configuration/ai-provider.md) — provider wiring for the keyed chat clients used by classification / field extraction / title generation
- [Deployment checklist](../deployment/deployment-checklist.md) — verifying OCR after a sidecar upgrade

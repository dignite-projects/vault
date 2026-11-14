```json
//[doc-seo]
{
    "Description": "How Vault Parse's extraction quality is measured: a generated corpus with ground truth, the metrics, the regression baseline that CI enforces, and running the OCR documents against a vision model."
}
```

# Parse Evaluation

Text extraction is measured on a corpus whose ground truth is known, so a change to Parse (a provider, an option, a new library) shows what it gains and what it loses before it ships. The suite is `modules/vault.parse/test/Dignite.Vault.Parse.Evaluation.Tests`. It parses every corpus document through the providers the host wires (PdfPig, OpenXML, ElBruno MarkItDown, vision-LLM OCR) and scores the Markdown against the truth.

## The Corpus

`Corpus/` holds each document beside its truth, `<id>.json`: the elements in reading order (headings with their levels, paragraphs, list items, tables with merged cells), the running header printed on every page, and whether page numbers were printed. Neither of the last two may reach the Markdown.

| Document | Format | What it exercises |
|---|---|---|
| `invoice` | PDF | A ruled table with row and column spans |
| `contract` | PDF, 2 pages | Japanese headings and a bulleted list; a running header and page numbers |
| `report` | PDF | Two-column English text |
| `pricelist` | PDF, 2 pages | A table continued across a page break without its header row |
| `contract-scan` | Image-only PDF | `contract` rasterized at 150 dpi, rotated, with noise and blur (OCR) |
| `receipt` | PNG | A photographed thermal receipt (OCR) |
| `memo` | DOCX | Heading styles, a bulleted list, a table with spans |
| `deck` | PPTX | Three slides with titles, bullets and a table |
| `ledger` | XLSX | Two sheets, each a table |
| `customers` | CSV | Shift_JIS (cp932) encoding |

The truth is written as data in `etc/eval/parse_corpus.py`, which renders the files from it (PyMuPDF, Pillow, openpyxl). The output is byte-stable, so a regenerated corpus changes only where the script changed:

```bash
python etc/eval/parse_corpus.py modules/vault.parse/test/Dignite.Vault.Parse.Evaluation.Tests/Corpus
```

## Metrics

Each metric is in [0, 1] and compares text in one form: Markdown and HTML markup removed, NFKC (full-width characters become their ASCII forms), lower case, and no whitespace, which a parser inserts at every line wrap. A metric is left out where the document has nothing to measure.

| Metric | Measures | Lowered by |
|---|---|---|
| Text similarity | One minus the character edit distance between all of the truth text and all of the output, over the longer length | Lost, garbled, duplicated or leaked text |
| Block recall | Truth headings, paragraphs and list items found in the output, each within 10% character edits | A block lost or garbled |
| Reading order | Of the blocks found, the share in truth order | Columns or pages read in the wrong order |
| Table cell recall | Truth table cells found in a row of an output table | Tables flattened into paragraphs |
| Table cell precision | Output table cells that are truth table cells; 1 when neither has a table | Columns of text read as a table, a paragraph pulled into one |
| Cell header recall | Truth data cells found under their own column header (the first row of every truth table is its header) | A table continued after a page break without its header, shifted columns, a merged cell placed in the wrong column |
| Heading recall | Truth headings the output marks as headings; a provider's label before one (`Sheet: 売上`) is not counted as its text | Headings written as paragraphs |
| Heading level agreement | Of consecutive found headings, the share whose levels move the same way as the truth's | Flattened or inverted heading levels |
| Noise free | One minus the share of running headers and page numbers that reached the output | Page furniture in the Markdown |

The scorer is an `IEvaluator` of [Microsoft.Extensions.AI.Evaluation](https://learn.microsoft.com/dotnet/ai/evaluation/libraries), `ParseQualityEvaluator`, and every run is stored with its disk-based reporting. To see a run's Markdown next to its scores:

```bash
dotnet tool install --global Microsoft.Extensions.AI.Evaluation.Console
```

```bash
dotnet aieval report --path <results directory> --output parse-evaluation.html
```

## Running It

```bash
dotnet test modules/vault.parse/test/Dignite.Vault.Parse.Evaluation.Tests/Dignite.Vault.Parse.Evaluation.Tests.csproj
```

The digital documents parse the same way every time, so each must hold the scores recorded in `Reports/baseline.json` within 0.01; CI runs the suite on every pull request. A change that moves a score rewrites the baseline from its own run and commits it with the change:

| Variable | Effect |
|---|---|
| `VAULT_PARSE_EVAL_WRITE_BASELINE=1` | Replaces the digital documents' entries in `Reports/baseline.json` in the source tree with this run's scores |
| `VAULT_PARSE_EVAL_REPORT` | A file path: writes a Markdown table of every document this run parsed |
| `VAULT_PARSE_EVAL_RESULTS` | The results directory for `dotnet aieval report`; the temp directory's `vault-parse-eval` by default |
| `VAULT_PARSE_EVAL_VISION_ENDPOINT`, `_API_KEY`, `_MODEL` | An OpenAI-compatible vision model; the OCR documents run only with all three |

The OCR documents are scored and reported, never gated: their output depends on the model and changes between calls. Each OCR page is one model call.

Provider options can be changed for one run through environment variables, which is how a candidate setting is compared with the baseline. Turning off the PDF table reconstruction, for example, fails `invoice`, `pricelist` and `report`:

```bash
PdfExtractor__ReconstructTables=false dotnet test modules/vault.parse/test/Dignite.Vault.Parse.Evaluation.Tests/Dignite.Vault.Parse.Evaluation.Tests.csproj
```

Dated runs, with the OCR documents and what they found, are kept in `Reports/`.

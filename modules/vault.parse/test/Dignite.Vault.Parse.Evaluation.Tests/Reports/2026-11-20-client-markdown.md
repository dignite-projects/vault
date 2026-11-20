# Client Markdown evaluation, 2026-11-20

How well an AI client converts the corpus itself, for [submitting processed content](../../../../../docs/en/documents/submitting-content.md),
scored with `VAULT_PARSE_EVAL_CLIENT_MARKDOWN` the way Vault reads a submission. The client was a fresh Claude Sonnet
agent, given only the corpus files (no ground truth) and the conversion rules of `vault_extract_submit_document`'s
description; it read PDFs and the image through its own vision and the Office files and the CSV with Python. The server
column is `baseline.json` for the digital documents and the 2026-11-14 run (Qwen3-VL-32B) for the OCR ones.

| Document | Metric that differs | Server | Client, run 1 | Client, run 2 |
|---|---|---:|---:|---:|
| contract-scan | Noise free | 0.250 | 1.000 * | 1.000 * |
| contract-scan | Text similarity / block recall | 0.988 / 0.969 | 1.000 * | 1.000 * |
| ledger | Text similarity | 0.964 | 0.988 | 1.000 |
| ledger | Block recall / heading recall | 1.000 / 1.000 | 0.500 / 0.000 | 1.000 / 1.000 |
| pricelist | Cell header recall | 1.000 | 0.432 | 1.000 |
| pricelist | Text similarity, table cells | 0.992, 0.989 | 1.000, 1.000 | 1.000, 1.000 |
| deck | Heading level agreement | 1.000 | 1.000 | 0.500 |

Every other metric of every document is 1.000 for the client in both runs, and so is every page count: each paged
document marked all of its pages with `<!-- PageBreak -->`. `report` scores 0.994 text similarity on both sides.

\* The client said it copied its `contract` transcription into `contract-scan` after comparing the pages, so its scan
score is not an independent OCR measurement. Transcribed independently from the scan's page images alone (Claude Opus,
the same rules, without having seen the digital twin), `contract-scan` scored 1.000 on every metric, noise free included.

## What changed between the runs

- Run 1's prompt added a line of the evaluator's own about spreadsheets that discouraged sheet names as headings; the
  corpus truth has them (the server's engine writes them too). Run 2 used the tool's rules alone, with two rules added
  after run 1: a spreadsheet's sheet names as headings, and a table running onto the next page kept as one table with
  the page break after it. Run 1 had split `pricelist`'s table at the page break, losing the header under the second
  half: the same defect the PDF engine had until it joined continued tables.
- Run 2 made only the first slide title a top-level heading. The rules then said "the document title as the top-level
  heading", which a deck reads two ways; they now say each slide's title is a top-level heading. That change is not yet
  re-measured.

## What it found

- On scans the client is better than the server's vision-LLM OCR: it leaves running headers and page numbers out, which
  the server's OCR transcribed with the body.
- On digital documents the server's engines are already at or near 1.000, so the client can only match them, and does
  once the rules say how a sheet, a deck and a page-crossing table are written.
- In run 1 the client read every PDF visually, because the text layer it printed from Python came out garbled on the
  Windows console. Run 2 was told to write such text to a file first and read the digital PDFs' text layers. Visual
  transcription of a long digital PDF costs the client vision tokens the server's own parse does not.

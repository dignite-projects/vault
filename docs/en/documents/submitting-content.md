```json
//[doc-seo]
{
    "Description": "Submit a document an AI client or a business system processed itself: its Markdown, type and field values, which skip the server's OCR, classification and field extraction."
}
```

# Submitting Processed Content

Uploading a file hands all of the processing to the server: text extraction (with OCR), classification and field extraction, each a model call the server pays for. A caller that can do that work itself, such as an AI client with a capable model on the user's side, **submits** the result instead. The server validates and stores what it receives and completes the pipeline stages with it; any stage the caller leaves out runs on the server as for an upload. The document then has the same lifecycle, review reasons and [integration events](../egress/integration-events.md) as an uploaded one, so downstream systems need no change.

This is a second way in, next to [uploading](documents.md#uploading); it does not replace it.

## What a Submission Carries

| Part | Required | When supplied | When left out |
| --- | --- | --- | --- |
| **Markdown** | Yes | Becomes the document's text. No OCR, no title model call | — |
| **Document type** (`documentTypeCode`) | No | Classification completes as for an upload that declares its type. No classification model call | The server classifies the text |
| **Field values** (`fields`) | No, and only with a type | Stored after the same validation an operator's field edit gets. No field-extraction model call | The server extracts the fields |
| **Field sources** (`fieldSources`) | No | Per field, up to three passages copied from the Markdown; each one found exactly once in the stored text becomes the value's source | Values have no source |
| Title, language, cabinet | No | As given (a cabinet needs the Cabinets permission, as on upload) | Title: the first heading |

Supplying `fields`, even as an empty object, means "these are the values": the server extracts none. Leaving it out lets the server extract them from the submitted text.

Nothing is stored when any part is refused: an unknown field, a value that does not read as its field's type, field values without a type, Markdown with no text, or Markdown longer than `DocumentConsts.MaxSubmittedMarkdownLength` (1,000,000 characters by default).

## Writing the Markdown

The Markdown is read the way Vault reads any [Markdown file](../text-extraction/text-extraction.md), and the result is the stored text, the same format the server's own engines produce:

- Transcribe the whole document, verbatim, in reading order. Do not summarize, translate or correct.
- Headings as `#` to `######`, lists as lists, tables as pipe tables, or as HTML `<table>` with `rowspan` / `colspan` when cells are merged.
- Text read from a picture, a stamp or a chart goes in a `<figure>` block, the way Azure Document Intelligence writes one.
- **Pages**: write `<!-- PageBreak -->` on a line of its own between two pages. The pages then reach the document's layout: the [layout endpoint](../egress/rest-api.md#the-layout) and Vault Index's page-aware chunks and search hits see them. `<!-- PageHeader="…" -->`, `<!-- PageFooter="…" -->` and `<!-- PageNumber="…" -->` lines are read as page furniture and kept out of the text. Without page breaks the document has no pages.

A client's output can be scored on the Parse evaluation corpus before it is relied on: see [Scoring a Client's Markdown](../text-extraction/parse-evaluation.md#scoring-a-clients-markdown).

## What a Submitted Document Lacks

- **No original file.** It cannot be downloaded or previewed, and [re-parsing](documents.md#correcting-a-document) is refused because there is nothing to re-parse.
- **The text is the caller's.** The server checks its shape, not its fidelity to a file. `DocumentDto.textOrigin` is `Submitted` for such a document (`Parsed` otherwise), so a consumer can decide how far to trust it.
- **Positions are page-level at best.** Field sources point into the text and, through the pages the Markdown marked, to a page; never to a region of a page image.
- **Duplicate detection** works on field values as for any document (the type's unique-key fields). There is no file hash to compare, so the same document submitted twice is caught only that way, and a client that retries after a timeout should first check whether its earlier call created the document.

## Permissions

A submission needs the [upload right](../configuration/document-type-permissions.md) for its type, exactly as an upload into that type, **and** `VaultExtract.Documents.Submit`, a module-wide permission for bringing one's own content. A tenant can therefore let a user upload without letting them submit processed content.

## Through MCP

`vault_extract_submit_document` submits inline: the Markdown, type and field values are tool arguments. Its description carries the conversion rules above, so the client's model follows them. To extract fields, the client first calls `vault_extract_list_document_types` with `includeExtractionSchema=true`: each field then carries `valueSchema`, the JSON schema its value must satisfy (the one the server's own extraction uses: a Select's options, a Table's columns, a date's format), and `extractionPrompt`, what to extract.

Because nothing but text is stored, **the user agrees first**:

- A client that supports elicitation over Multi Round-Trip Requests (the `2026-07-28` protocol revision) is asked to show a confirmation; the model's own say-so does not count.
- Any other client gets `confirmationRequired: true` and the list of what is given up, and the tool submits nothing until it is called again with `userConfirmed: true`. The server cannot see the conversation, so this relies on the model asking the user; what it guarantees is that the consequences reach the model.

## Through REST

```bash
curl -X POST https://your-host/api/vault-extract/documents/submit \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
        "markdown": "# Invoice\n\nNo. INV-7\n\n<!-- PageBreak -->\n\nTotal 1,200",
        "documentTypeCode": "invoice",
        "fields": { "invoice_no": "INV-7", "total": 1200 },
        "fieldSources": { "invoice_no": ["No. INV-7"] }
      }'
```

The response is the new document (`DocumentDto`). When every stage was supplied and nothing holds it for review, it is already `Ready` and `DocumentReadyEto` has been published.

## See Also

- [Documents and Review](documents.md)
- [MCP Server](../egress/mcp-server.md)
- [Document Type Permissions](../configuration/document-type-permissions.md)

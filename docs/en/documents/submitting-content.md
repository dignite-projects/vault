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
| **Markdown** (`markdown`, or an uploaded `markdownFileId`) | Yes | Becomes the document's text. No OCR, no title model call | — |
| **Original file** (an uploaded `originalFileId`) | No | Kept as the document's file, as an upload keeps it: downloadable, previewable, re-parsable | The document has its text only |
| **Document type** (`documentTypeCode`) | No | Classification completes as for an upload that declares its type. No classification model call | The server classifies the text |
| **Field values** (`fields`) | No, and only with a type | Stored after the same validation an operator's field edit gets. No field-extraction model call | The server extracts the fields |
| **Field sources** (`fieldSources`) | No | Per field, up to three passages copied from the Markdown; each one found exactly once in the stored text becomes the value's source | Values have no source |
| Title, language, cabinet | No | As given (a cabinet needs the Cabinets permission, as on upload) | Title: the first heading |

Supplying `fields`, even as an empty object, means "these are the values": the server extracts none. Leaving it out lets the server extract them from the submitted text.

Nothing is stored when any part is refused: an unknown field, a value that does not read as its field's type, field values without a type, Markdown with no text, given both inline and as a file, or longer than `DocumentConsts.MaxSubmittedMarkdownLength` (1,000,000 characters by default), a Markdown file that is not UTF-8, an uploaded file that is not the caller's pending upload, or an original file an upload would refuse (its type, its size, or a copy of a document the caller already has).

## Uploading the Files

The Markdown and the original file do not have to travel through the request, or through a model: each is uploaded on its own with a one-time link, and the submission names it by id.

1. Ask for a link per file: `POST /api/vault-extract/documents/submission-uploads` with `{"kind": "Markdown"}` or `{"kind": "Original"}`, or the MCP tool `vault_extract_create_submission_upload`. The answer gives the file's id, the upload URL, its expiry (ten minutes) and the largest size it takes.
2. Send the file to the URL: `POST`, multipart, form field `file`, with no sign-in (the link is the credential). The MCP tool also returns the `curl` command, for a shell on the machine the file is on.
3. Submit with `markdownFileId` and `originalFileId`.

A link takes one file, for the user it was issued to; a Markdown link takes text only, an original link the types an upload accepts, decided from the bytes rather than from what the client declares. The files wait in Blob Management's `extract-submissions` container: only the user who uploaded them can submit them, a submission deletes them once it has taken them, and a file never submitted is deleted after a day. Callers that act without a user (a client signed in with its own credentials) count as one uploader of the tenant, here and when a submission is recognised as one they made before, as they do in an upload's duplicate check. For a folder, take one document at a time, two links and a submission each.

The host provides this through [Dignite Blob Management](https://github.com/dignite-projects/abp-modules): it installs the module, its HTTP API (which serves the upload endpoint) and its tables, and declares the `extract-submissions` container. Upload tickets are signed with ASP.NET Core Data Protection, whose keys the host keeps in its database, so a link issued by one instance is accepted by another ([Data Protection Keys](../deployment/deployment.md#data-protection-keys)).

## Writing the Markdown

The Markdown is read the way Vault reads any [Markdown file](../text-extraction/text-extraction.md), and the result is the stored text, the same format the server's own engines produce:

- Transcribe the whole document, verbatim, in reading order. Do not summarize, translate or correct.
- Headings as `#` to `######`, lists as lists, tables as pipe tables, or as HTML `<table>` with `rowspan` / `colspan` when cells are merged.
- A spreadsheet: each sheet's name as a heading, then its table. A slide deck: each slide in order, its title as a top-level heading, with a page break between slides.
- Text read from a picture, a stamp or a chart goes in a `<figure>` block, the way Azure Document Intelligence writes one.
- **Pages**: write `<!-- PageBreak -->` on a line of its own between two pages. The pages then reach the document's layout: the [layout endpoint](../egress/rest-api.md#the-layout) and Vault Index's page-aware chunks and search hits see them. `<!-- PageHeader="…" -->`, `<!-- PageFooter="…" -->` and `<!-- PageNumber="…" -->` lines are read as page furniture and kept out of the text. Without page breaks the document has no pages. A table that runs onto the next page stays one table, with the page break after it: split in two, its second half loses its header, and every cell below the break is read under no column. A page break inside a `<figure>` takes effect after it: the figure stays whole, on the page it starts on.

A client's output can be scored on the Parse evaluation corpus before it is relied on: see [Scoring a Client's Markdown](../text-extraction/parse-evaluation.md#scoring-a-clients-markdown).

## What a Submitted Document Lacks

- **No original file**, unless one was uploaded with it. Without one it cannot be downloaded or previewed, and [re-parsing](documents.md#correcting-a-document) is refused because there is nothing to re-parse. With one, a re-parse replaces the submitted text with the server's own.
- **The text is the caller's.** The server checks its shape, not its fidelity to a file. `DocumentDto.textOrigin` is `Submitted` for such a document (`Parsed` otherwise), so a consumer can decide how far to trust it.
- **Positions are page-level at best.** Field sources point into the text and, through the pages the Markdown marked, to a page; never to a region of a page image.
- **Duplicate detection** works on field values as for any document (the type's unique-key fields). A submission is also checked against the caller's own earlier ones, as an upload is against the caller's uploads: by its original file's hash when it brings one, otherwise by the hash of its text as stored, so a line-ending or blank-line difference does not make a retry new. A repeat is refused with `Extract:DocumentSubmittedTextDuplicate` (or `…InRecycleBin`) and the existing document's id, which a client that retried after a timeout can go on with.

## Permissions

A submission needs the [upload right](../configuration/document-type-permissions.md) for its type, exactly as an upload into that type, **and** `VaultExtract.Documents.Submit`, a module-wide permission for bringing one's own content. A tenant can therefore let a user upload without letting them submit processed content. Asking for an upload link needs `VaultExtract.Documents.Submit` too; the type is judged when the submission names it.

## Through MCP

`vault_extract_submit_document` takes the Markdown inline or as `markdownFileId`, and the original as `originalFileId`; its description carries the conversion rules above, so the client's model follows them. A client with a shell (Claude Code, Codex and other agents) stages both files with `vault_extract_create_submission_upload` and runs the `curl` command it returns, so even a long document costs the model nothing to send. A chat client without one submits the Markdown inline, and stores no original file. To extract fields, the client first calls `vault_extract_list_document_types` with `includeExtractionSchema=true`: each field then carries `valueSchema`, the JSON schema its value must satisfy (the one the server's own extraction uses: a Select's options, a Table's columns, a date's format), and `extractionPrompt`, what to extract.

When nothing but text is stored, **the user agrees first** (a submission that brings its original file is not asked):

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

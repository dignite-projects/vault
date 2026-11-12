```json
//[doc-seo]
{
    "Description": "Search and read the text of Dignite Vault documents through Vault Index: hybrid keyword and vector search, document outlines and pointer reads over REST and MCP, within each caller's read scope."
}
```

# Index Search

Vault Index turns every document Extract releases into chunks with vectors and a full-text index, and serves them back as **evidence**: the passages that match a question, with their source, never a generated answer. It complements Extract's own [MCP search](mcp-server.md#search), which finds documents by type, metadata and field values but not by wording or meaning.

> Vault Index is under development and ships only inside the host (its packages are not published yet). The contract on this page may still change before its first release.

There are three operations, used in this order: **search** finds passages, **outline** shows a document's sections, **read** returns the text behind a pointer either of them returned. They are the same application service (`IIndexSearchAppService`) behind REST and MCP.

## Who Sees What

Every call needs:

- the `VaultIndex.Enable` and `VaultExtract.Enable` features for the caller's tenant (both on by default): the read scope and the type codes come from Extract's application services;
- the `VaultIndex.Search` permission;
- Extract's `VaultExtract.Documents` entry permission. Index asks Extract for the caller's read scope on every call, and Extract refuses a caller without it.

A result contains only documents the caller could read in Extract: with `VaultExtract.Documents.ReadAll`, every document of the tenant; otherwise the documents of the types granted to the caller and the documents the caller uploaded. See [Per-Document-Type Permissions](../configuration/document-type-permissions.md). The scope is resolved on each call, so a revoked grant applies to the next call. Something outside it is answered as **not found**, never as forbidden.

Index holds only what Extract released (`DocumentReadyEto`). A document in Extract's recycle bin is out of every result until it is restored. A document being reclassified is out until its next release, so the old type's readers lose it at once. A document sent back to review after release stays at its last released version.

## Search

| | |
| --- | --- |
| REST | `GET /api/vault-index/search?query=…&documentTypeCode=…&maxResultCount=…` |
| MCP | `vault_index_search` (`query`, `documentTypeCode`, `maxResults`) |

`query` is natural language or keywords, up to 1,000 characters. `documentTypeCode` narrows to one type (a type the caller cannot see finds nothing). `maxResultCount` is 1 to 20, default 8.

Two legs run over the caller's scope and are fused by **reciprocal rank fusion** (k = 60): a **keyword** leg (SQL Server full-text search with the Japanese word breaker) and a **vector** leg (exact cosine distance with the host's embedding model). Fusion reads ranks, not scores, so each hit reports where each leg ranked it:

```json
{
  "hits": [
    {
      "pointer": "chunk:3a24dfd9-6201-…",
      "documentId": "c8ffc40a-0034-f808-288f-3a24dfd96114",
      "documentTitle": "建物賃貸借契約書",
      "documentTypeCode": "lease",
      "headingPath": "# 建物賃貸借契約書 ## 第2条(賃料)",
      "text": "# 建物賃貸借契約書 ## 第2条(賃料)\n賃料は月額 185,000 円とし、…",
      "textTruncated": false,
      "textTotalLength": 92,
      "score": 0.0325,
      "keywordRank": 1,
      "vectorRank": 2
    }
  ]
}
```

- The vector leg always returns the nearest chunks, so a search returns hits even when nothing matches the words; `keywordRank` is `null` for a hit only the vector leg found.
- A hit's `text` is cut at 2,000 characters (`textTruncated`, `textTotalLength`); read the pointer for the whole chunk.
- The keyword leg lags writes by about five seconds; the vector leg sees a document as soon as it is indexed.
- Without Full-Text Search on the database the keyword leg returns nothing and the host logs a warning. See [Connection Strings](../deployment/deployment.md#connection-strings).
- Search is refused (`VaultIndex:EmbeddingModelChanged`) when the host's embedding model is not the one the index was built with: two models' vectors cannot be compared.

## Outline

| | |
| --- | --- |
| REST | `GET /api/vault-index/documents/{documentId}/outline` |
| MCP | `vault_index_outline` (`documentId`) |

The document's heading tree in document order, at most 500 nodes (`truncated`, `totalNodeCount`). Each node has a `pointer` (`node:{documentId}/{path}`, the 1-based position in the tree, such as `1.2`), its `title`, `depth`, Markdown `level` and `chunkCount`. A document without headings has no nodes; read its chunks from search hits.

## Read

| | |
| --- | --- |
| REST | `GET /api/vault-index/read?pointer=…` |
| MCP | `vault_index_read` (`pointer`) |

Accepts only the pointers search and outline return: `chunk:{id}` for one chunk, `node:{documentId}/{path}` for a section with its subsections. Anything else is refused with `VaultIndex:InvalidPointer`; a pointer outside the caller's scope is not found. The text is Index's stored copy, cut at 40,000 characters (`textTruncated`, `textTotalLength`). A chunk read also returns `previousPointer` and `nextPointer` to read on.

A pointer stays valid until the document is indexed again (its text or chunking changes), when search and outline return new ones. Pointers are not character ranges of the source Markdown, so citations name a document and a heading path, not a page.

## MCP Specifics

The three tools are on the host's shared `/mcp` endpoint, next to Extract's `vault_extract_*` tools, with the same authentication; see [MCP Server](mcp-server.md). Their descriptions are compile-time constants. Titles, heading paths and node titles come back wrapped in `<field>` tags and chunk text in `<document>` tags: document content is untrusted data, never instructions. The tools use the ambient tenant only; they take no `tenantId`.

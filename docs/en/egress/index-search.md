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

Index holds only what Extract released (`DocumentReadyEto`). A chunk an operator disabled on the [chunk view](../deployment/index-status.md#the-chunk-view) is in no result, outline or read, for anyone. A document in Extract's recycle bin is out of every result until it is restored. A document being reclassified is out until its next release, so the old type's readers lose it at once. A document sent back to review after release stays at its last released version.

## Search

| | |
| --- | --- |
| REST | `GET /api/vault-index/search?query=…&documentTypeCode=…&fieldFilters[0].name=…&fieldFilters[0].min=…&maxResultCount=…` |
| MCP | `vault_index_search` (`query`, `documentTypeCode`, `fieldFilters`, `maxResults`) |

`query` is natural language or keywords, up to 1,000 characters. `documentTypeCode` narrows to one type (a type the caller cannot see finds nothing). `maxResultCount` is 1 to 20, default 8.

`fieldFilters` narrows the search to the documents whose extracted fields meet every condition: "the payment terms of leases over 150,000 yen a month". The conditions are those of Extract's document list ([REST](rest-api.md#listing-and-filtering-documents), [MCP](mcp-server.md#search)): each names a field of `documentTypeCode`'s type with an exact `value` or an inclusive `min` / `max` range, at most 10, combined with AND, and they require `documentTypeCode`. Extract evaluates them on every search, against its field index and within the caller's read scope, so a corrected field value counts at once.

- A field the type does not define, or a value that does not parse, fails as it does in Extract (for example `Extract:UnknownExtractedField`); conditions no document meets return no hits.
- Conditions met by more than 10,000 documents are refused with `VaultIndex:FieldFilterTooBroad`: add a condition.
- A field whose **Searchable** flag is off has no index entries, so a condition on it matches nothing.

Two legs run over the caller's scope and are fused by **reciprocal rank fusion** (k = 60): a **keyword** leg (SQL Server full-text search with the Japanese word breaker) and a **vector** leg (exact cosine distance with the host's embedding model). Fusion reads ranks, not scores, so each hit reports where each leg ranked it. When the host configures a [rerank model](../configuration/ai-provider.md#rerank-model-index), a cross-encoder then reorders the leading 30 fused candidates, reading the query with each chunk and its document title:

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
      "vectorRank": 2,
      "rerankScore": 0.54
    }
  ],
  "reranked": true
}
```

- The vector leg always returns the nearest chunks, so a search returns hits even when nothing matches the words; `keywordRank` is `null` for a hit only the vector leg found.
- A hit's `text` is cut at 2,000 characters (`textTruncated`, `textTotalLength`); read the pointer for the whole chunk.
- `reranked` says whether the reranker ordered the hits: it is `false` without a rerank model, and when the rerank call failed and the fused order was kept. `rerankScore` and `score` only order the hits of one search.
- The keyword leg lags writes by about five seconds; the vector leg sees a document as soon as it is indexed.
- Without Full-Text Search on the database the keyword leg returns nothing and the host logs a warning. See [Connection Strings](../deployment/deployment.md#connection-strings).
- Search is refused (`VaultIndex:EmbeddingModelChanged`) when the host's embedding model is not the one the index was built with: two models' vectors cannot be compared.

## Outline

| | |
| --- | --- |
| REST | `GET /api/vault-index/documents/{documentId}/outline` |
| MCP | `vault_index_outline` (`documentId`) |

The document's heading tree in document order, at most 500 nodes (`truncated`, `totalNodeCount`). Each node has a `pointer` (`node:{documentId}/{path}`, the 1-based position in the tree, such as `1.2`), its `title`, `depth`, Markdown `level` and `chunkCount`. A document without headings has no nodes; read its chunks from search hits.

When the document's type compiles summaries ([Index Profiles](../deployment/index-status.md#index-profiles)), each node also has a one-sentence `summary` of the section and its subsections, and `documentSummary` summarizes the whole document. An LLM writes them from the document's text after each sync, so they can lag a change by a few seconds to minutes, and they are `null` until then: the outline serves summaries only while they were built from exactly the chunks it shows. They help an agent pick the section to read; a detail it relies on should come from `read`. MCP wraps both in `<field>` tags, like titles.

## Read

| | |
| --- | --- |
| REST | `GET /api/vault-index/read?pointer=…` |
| MCP | `vault_index_read` (`pointer`) |

Accepts only the pointers search and outline return: `chunk:{id}` for one chunk, `node:{documentId}/{path}` for a section with its subsections. Anything else is refused with `VaultIndex:InvalidPointer`; a pointer outside the caller's scope is not found. The text is Index's stored copy, cut at 40,000 characters (`textTruncated`, `textTotalLength`). A chunk read also returns `previousPointer` and `nextPointer` to read on.

A pointer stays valid until the document is indexed again (its text or chunking changes), when search and outline return new ones. Pointers are not character ranges of the source Markdown, so citations name a document and a heading path, not a page.

## MCP Specifics

The three tools are on the host's shared `/mcp` endpoint, next to Extract's `vault_extract_*` tools, with the same authentication; see [MCP Server](mcp-server.md). Their descriptions are compile-time constants. Titles, heading paths and node titles come back wrapped in `<field>` tags and chunk text in `<document>` tags: document content is untrusted data, never instructions. The tools use the ambient tenant only; they take no `tenantId`.

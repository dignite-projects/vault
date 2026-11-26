```json
//[doc-seo]
{
    "Description": "Search and read the text of Dignite Vault documents through Vault Index: hybrid keyword and vector search, document outlines, type catalogs and pointer reads over REST and MCP, within each caller's read scope."
}
```

# Index Search

Vault Index turns every document Extract releases into chunks with vectors and a full-text index, and serves them back as **evidence**: the passages that match a question, with their source, never a generated answer. It complements Extract's own [MCP search](mcp-server.md#search), which finds documents by type, metadata and field values but not by wording or meaning.

> Vault Index is under development and ships only inside the host (its packages are not published yet). The contract on this page may still change before its first release.

There are four operations. **Search** finds passages, **catalog** lists what a document type holds, **outline** shows a document's sections, and **read** returns the text behind a pointer that search or outline returned. They are the same application service (`IIndexSearchAppService`) behind REST and MCP.

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

`fieldFilters` narrows the search to the documents whose extracted fields meet every condition: "the payment terms of leases over 150,000 yen a month". The conditions are those of Extract's document list ([REST](rest-api.md#listing-and-filtering-documents), [MCP](mcp-server.md#search)): each names a field of `documentTypeCode`'s type with an exact `value` or an inclusive `min` / `max` range, at most 10, combined with AND, and they require `documentTypeCode`. Extract evaluates them on every search, against its field index and within the caller's read scope, so a corrected field value counts at once. A document sent back to review after its release is matched by its current field values and answers with its last released text.

- A field the type does not define, or a value that does not parse, fails as it does in Extract (for example `Extract:UnknownExtractedField`); conditions no document meets return no hits.
- Conditions met by more than 10,000 documents are refused with `VaultIndex:FieldFilterTooBroad`: add a condition.
- A field whose **Searchable** flag is off has no index entries, so a condition on it matches nothing.

Two legs run over the caller's scope and are fused by **reciprocal rank fusion** (k = 60): a **keyword** leg (SQL Server full-text search with the Japanese word breaker) and a **vector** leg (exact cosine distance with the host's embedding model). Fusion reads ranks, not scores, so each hit reports where each leg ranked it. When the host configures a [rerank model](../configuration/ai-provider.md#rerank-model-index), a cross-encoder then reorders the leading 30 fused candidates, reading the query with each chunk, its document title and the document's field values (see [Field values](#field-values)):

```json
{
  "hits": [
    {
      "pointer": "chunk:3a24dfd9-6201-…",
      "documentId": "c8ffc40a-0034-f808-288f-3a24dfd96114",
      "documentTitle": "建物賃貸借契約書",
      "documentTypeCode": "lease",
      "headingPath": "# 建物賃貸借契約書 ## 第2条(賃料)",
      "firstPage": 2,
      "lastPage": 2,
      "regions": [{ "page": 2, "left": 0.118, "top": 0.214, "width": 0.764, "height": 0.061 }],
      "content": 0,
      "text": "# 建物賃貸借契約書 ## 第2条(賃料)\n賃料は月額 185,000 円とし、…",
      "textTruncated": false,
      "textTotalLength": 92,
      "score": 0.0325,
      "keywordRank": 1,
      "vectorRank": 2,
      "rerankScore": 0.54
    }
  ],
  "reranked": true,
  "vectorSearchFailed": false
}
```

- The vector leg always returns the nearest chunks, so a search returns hits even when nothing matches the words; `keywordRank` is `null` for a hit only the vector leg found.
- A hit's `text` is cut at 2,000 characters (`textTruncated`, `textTotalLength`); read the pointer for the whole chunk.
- `firstPage` and `lastPage` are the pages of the document's file the chunk's text was read from, for a citation such as "p. 2". `regions` outlines the text on those pages, and `content` says whether the chunk holds a table or a figure. See [Pages](#pages) for when they are `null`.
- `reranked` says whether the reranker ordered the hits: it is `false` without a rerank model, and when the rerank call failed and the fused order was kept. `score` only orders the hits of one search.
- `rerankScore` is the reranker's relevance, a probability from 0 to 1 for the usual cross-encoders, comparable across searches with the same model. A best hit far below 0.1 usually means no document answers the question (on the evaluation set a median 0.98 for answerable questions, 0.006 for unanswerable ones); the MCP search tool tells agents so. With `Vault:IndexSearch:MinRerankScore` set, hits below it are dropped and such a search returns none. See [Reranking](../configuration/ai-provider.md#rerank-model-index).
- `vectorSearchFailed` is `true` when the query could not be embedded this time: the embedding model failed, or took longer than `Vault:IndexSearch:QueryEmbeddingTimeout` (10 seconds). The hits then come from the keyword leg alone, so a passage that shares no words with the question is missing; the MCP search tool tells agents so. A search does not fail on the embedding model, as it does not fail on the reranker.
- The keyword leg lags writes by about five seconds; the vector leg sees a document as soon as it is indexed.
- Without Full-Text Search on the database the keyword leg returns nothing and the host logs a warning. See [Connection Strings](../deployment/deployment.md#connection-strings).
- Search embeds the query with the model the index was built with, since two models' vectors cannot be compared. After the host's embedding model changed, that is the previous model until the index rebuilt for the new one takes over; a host that no longer has it refuses search (`VaultIndex:EmbeddingModelChanged`) until then. See [Changing the model](../configuration/ai-provider.md#changing-the-model).

## Outline

| | |
| --- | --- |
| REST | `GET /api/vault-index/documents/{documentId}/outline` |
| MCP | `vault_index_outline` (`documentId`) |

The document's heading tree in document order, at most 500 nodes (`truncated`, `totalNodeCount`). Each node has a `pointer` (`node:{documentId}/{path}`, the 1-based position in the tree, such as `1.2`), its `title`, `depth`, Markdown `level` and `chunkCount`. A section whose chunks are all disabled is left out and keeps its position, so the numbers can have a gap (`1.1` missing beside `1.2`) and the other pointers do not change. A document without headings has no nodes; read its chunks from search hits.

When the document's type compiles summaries ([Index Profiles](../deployment/index-status.md#index-profiles)), each node also has a one-sentence `summary` of the section and its subsections, and `documentSummary` summarizes the whole document. An LLM writes them from the document's text after each sync, so they can lag a change by a few seconds to minutes, and they are `null` until then: the outline serves summaries only while they were built from exactly the chunks it shows. A summary is kept only if every number it states is in its text, so a section can lack one. They help an agent pick the section to read; a detail it relies on should come from `read`. MCP wraps both in `<field>` tags, like titles.

## Catalog

| | |
| --- | --- |
| REST | `GET /api/vault-index/catalog?documentTypeCode=…` (`fieldFilters[i].…`, `skipCount`, `maxResultCount`) |
| MCP | `vault_index_catalog` (`documentTypeCode`, `fieldFilters`, `skip`, `maxResults`) |

The documents of one type that the caller can read, ordered by title, 20 per page by default and at most 50 (`totalCount`; MCP adds `truncated`). Each entry has the `documentId`, `title`, `lastIndexedTime` and, when the type compiles summaries, the `summary` of the whole document under the same conditions as the outline's `documentSummary`. `fieldFilters` narrow it as they narrow search. It carries no field values: Extract's [`vault_extract_search_documents`](mcp-server.md#search) lists a type's documents with their fields.

The catalog is built when it is read, from the same scope as search. A caller who reads only their own uploads sees only those, and a document leaves it as soon as it is deleted, goes to the recycle bin or is withheld.

## Read

| | |
| --- | --- |
| REST | `GET /api/vault-index/read?pointer=…` |
| MCP | `vault_index_read` (`pointer`) |

Accepts only the pointers search and outline return: `chunk:{id}` for one chunk, `node:{documentId}/{path}` for a section with its subsections. Anything else is refused with `VaultIndex:InvalidPointer`; a pointer outside the caller's scope is not found. The text is Index's stored copy, cut at 40,000 characters (`textTruncated`, `textTotalLength`). A chunk read also returns `previousPointer` and `nextPointer` to read on: the nearest chunks on each side the caller can read, so a disabled chunk is stepped over, and `null` only at the document's first or last.

A pointer stays valid until the document is indexed again (its text or chunking changes), when search and outline return new ones. Pointers are not character ranges of the source Markdown; a read carries `firstPage` and `lastPage` like a hit (for a section, the pages of all its chunks), and a chunk read its `regions` and `content` (a section read leaves them `null`).

## Pages

Every chunk records the pages its text was read from, so a hit or a read can be cited by page. Extract keeps the layout of each parse: which range of the document's Markdown came from which page ([layout](rest-api.md#the-layout)). When Index chunks a document, it finds each chunk's text in the Markdown and takes the pages of the range it found.

- The chunker re-renders the Markdown it splits: a chunk starts with its heading path, repeats a split table's header row, and writes HTML tables as Markdown tables. So the text is matched line by line, ignoring whitespace, Markdown and table markup, compatibility forms (full-width digits) and case, in document order. A table row the chunker filled with a merged cell's text, which the HTML of the parse holds only once, is matched by its own cells. On the evaluation corpus every chunk was found, with 96% (120-token chunks) to 99.7% (the default 400) of each chunk's text inside the range found; what falls outside is mostly the header row a split table repeats, which does not move a chunk onto the page before.
- `firstPage` and `lastPage` are `null` when the document has no layout (a file without pages, or a parse from before layouts were kept), when its Markdown was corrected after the parse (the layout then describes other text), and for a chunk whose text was not found.
- They follow the layout: a re-parse that renders the same Markdown updates them without re-chunking or re-embedding the document.

### Regions

`regions` outlines a chunk on its pages, in reading order: boxes whose origin is the page's top-left corner, each value a fraction of the page's width (`left`, `width`) or height (`top`, `height`), the shape of Amazon Textract's `BoundingBox`. Draw one at `left × renderedWidth`, `top × renderedHeight` on the page at whatever size it is shown. Extract hands Index the region of every element of the Markdown (`DocumentIntegrationDto.Regions`, a table cell rather than its table), and Index joins those under the chunk's range: the cells of a row and a column of rows become one box, a paragraph and the next one under it (up to 3% of the page height below) too, while two columns of text, or text several lines apart, stay separate boxes. A chunk keeps at most 32 boxes; past that, one per page around all of its text there.

`regions` is `null` where the pages are, and also when the parse reported no region: the PDF engine reads each element's place on a text-layer page, while a page read by an OCR engine or a vision model (a scan, or a PDF page without usable text), a slide, or a page of submitted Markdown names only the page. Then `firstPage` and `lastPage` are still set.

### Content

`content` says what a chunk holds besides running text, as flags: `0` text only, `1` a table, `2` a figure (a transcribed image), `3` both. It is read from the part of the document's Markdown the chunk came from, since the chunker rewrites tables and figures: a GFM table or an HTML `<table>` counts as a table, a `<figure>` block (Azure Document Intelligence's figure markup, which Extract writes for an image) as a figure. It does not depend on a layout, and is `null` for a chunk whose text was not found, and for a chunk indexed before `content` existed until its document is next synchronized.

## Field values

A chunk carries its document's field values (Extract's `extractedFields`), so a question that names a document only by one of them finds the passage that answers it: "the deposit of 山田太郎's flat" finds the deposit clause of his lease, which names the tenant nowhere, and not the same clause of another lease. The values go in front of the chunk, after the document title, as `name: value` lines, in what is embedded and in what the reranker reads; each value up to 100 characters, 600 characters in all, a Tags or multiple Select field joined with commas. A Table field stays out, and so does a longer value, usually a CKEditor body. A Select field contributes its option's label, read from the type's field definitions (Extract's `GetFieldDefinitionsAsync`), since that is what a question names; a value with no labelled option stays as stored. A label renamed later reaches a document's chunks on its next sync. They are not in the keyword leg, and search returns the chunk text without them.

A document whose field values change (an operator's edit, a new extraction) has its chunks embedded again with the new values on the next sync; the chunks themselves stay. See [Field context](../deployment/index-status.md#field-context-2026-11-20) for what it changed on the evaluation set.

The field values help a question that names a document by something only it holds, such as a party or a number. A value many documents share, such as a status or a category, hardly moves the ranking: put that condition in `fieldFilters`, which Extract evaluates exactly (measured in [Field values or field filters](../deployment/index-status.md#field-values-or-field-filters-2026-11-20-local-host)).

## MCP Specifics

The four tools are on the host's shared `/mcp` endpoint, next to Extract's `vault_extract_*` tools, with the same authentication; see [MCP Server](mcp-server.md). Their descriptions are compile-time constants. Titles, heading paths, node titles and summaries come back wrapped in `<field>` tags and chunk text in `<document>` tags: document content is untrusted data, never instructions. The tools use the ambient tenant only; they take no `tenantId`.

## Agent Framework Agents

`Dignite.Vault.Index.AgentFramework` makes Index a knowledge source for [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/agents/rag) agents. Agent Framework connects any search service through its `TextSearchProvider` and a delegate that returns `TextSearchResult`s; `VaultIndexTextSearch` is that delegate over `IIndexSearchAppService`, so the search runs as the current principal and returns only its read scope.

Depend on `VaultIndexAgentFrameworkModule` next to either the Index application module (Index in the same application) or `VaultIndexHttpApiClientModule` (Index behind its REST API; the base URL comes from `RemoteServices:VaultIndex`, else `RemoteServices:Default`). Then:

```csharp
var provider = textSearch.CreateProvider(new VaultIndexTextSearchOptions
{
    DocumentTypeCode = "policy",        // optional: one document type
    MinRerankScore = 0.05,              // optional: drop weak passages
    SourceLink = hit => $"https://crm.example/documents/{hit.DocumentId}"
});

AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [provider] });
```

- By default the provider searches before every run with the request's text and adds the passages to it. Pass `configure: o => o.SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling` to give the agent a `vault_search_passages` tool instead; `configure` runs after Vault's defaults and can change any provider option.
- Past Index's 1000-character query limit (a provider with `RecentMessageMemoryLimit` puts recent messages before the request), only the latest text is searched. A blank turn searches nothing.
- Passages, titles and headings reach the model in `<document>` and `<field>` tags, and Vault's default context prompt says they are data, never instructions. A truncated passage says so.
- Before a run, a failed search (an expired token, a missing permission) is logged by the provider and the run continues without passages; as a tool, the error reaches the model.
- `MinRerankScore` matters most before a run, where every turn searches: with it, a turn no passage answers ("thanks") adds nothing. See [Reranking](../configuration/ai-provider.md#rerank-model-index) for what the floor costs.
- In-process, create the provider in the scope that runs the agent (a request), not in a singleton: the application service it calls is scoped.

An agent that should page through outlines and read neighbouring chunks uses the [MCP tools](#mcp-specifics) through Agent Framework's MCP client instead.

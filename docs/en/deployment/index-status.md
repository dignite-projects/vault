```json
//[doc-seo]
{
    "Description": "Operate Vault Index: the index status page, the chunk view, the retrieval test and the index profiles, their REST API, rebuilding a document, disabling a chunk or changing how a type is chunked, and measuring retrieval quality with the evaluation set."
}
```

# Index Status and Evaluation

Vault Index keeps a derived copy of every released document (chunks, vectors and a full-text index). This page covers how an operator sees what is in it, fixes a document that did not index, finds out why a passage is not found, chooses how each document type is chunked, and measures how well it retrieves.

> Vault Index is under development and ships only inside the host. Its Angular library, `@dignite/ng.vault-index`, is built with the host SPA and not published to npm yet.

## The Status Page

The host SPA shows it under **Vault Index → Index Status** (`/vault-index/status`). It needs the `VaultIndex.Status` permission, as do the chunk view and the retrieval test below; the **Rebuild** button needs `VaultIndex.Status.Rebuild`, and disabling a chunk needs `VaultIndex.Status.ManageChunks`. `--migrate-database` grants all three to the seeded `admin` role; grant them to other roles under Identity → Roles.

The summary at the top shows:

- documents by state: indexed, failed, pending or indexing, and those out of retrieval (in Extract's recycle bin, or withheld after a reclassification until their next release);
- chunks, and a warning when the tenant has more than 50,000 vectors. Vector search is exact, and the storage proof of concept sized it for about that many rows after the read-scope filter;
- the embedding model the index was built with and the one the host is configured with. With no model configured nothing is indexed; with a different one, search is refused until the index is rebuilt;
- the keyword index: missing (Full-Text Search not installed, so search uses vectors only), populating after a backfill, or how many changes it has not absorbed yet. It follows writes by about five seconds.

The document list shows each indexed document with its type, state, chunk count, last indexing time and, for a failed one, the last error. It shows only documents within the operator's Extract read scope, because it shows titles. **Runs** expands a document's last 20 index runs: trigger, outcome, attempt, chunks, embedding tokens, the LLM tokens enrichment took, and the error, skip reason or note (such as chunks enrichment could not reach).

**Rebuild** queues the document to be indexed again from its current Extract text, even when nothing changed. Use it after fixing what made a document fail. The rebuild goes through the same queue, claim and retry cap as an event, so it does not race an index job already running.

Errors that end a document at once carry a `VaultIndex:` code:

| Code | Meaning |
| --- | --- |
| `VaultIndex:EmptyDocument` | The document has no text |
| `VaultIndex:DocumentTooLarge` | Longer than `Vault:IndexIngestion:MaxMarkdownLength` |
| `VaultIndex:TooManyChunks` | More chunks than `Vault:IndexIngestion:MaxChunksPerDocument` |
| `VaultIndex:EmbeddingModelChanged` | The configured model is not the one the index was built with |
| `VaultIndex:EmbeddingDimensionMismatch` | The model returns vectors of another length than the storage holds |

Anything else is transient (a model timeout, a database error) and is retried up to `Vault:IndexIngestion:MaxFailedAttempts` times before the document is marked failed.

## The Chunk View

**Chunks** on a document's row opens `/vault-index/documents/{documentId}/chunks`: the chunks the document was cut into, in order, each with its heading path, its text, its pointer, and its keywords and questions when it was enriched. Like the document list, it shows only documents within the operator's read scope, including those in the recycle bin or withheld.

**Disable** takes a chunk out of retrieval: search, outline and read no longer return it, for every caller. Use it for a chunk that is noise, such as a repeated disclaimer or a garbled table, and that keeps crowding out the passages people look for. The text itself cannot be edited here: correct the document's Markdown in Extract, so that the index can always be rebuilt from Extract.

A disabled chunk stays disabled across a rebuild as long as its text is unchanged; the mark follows the text, not the chunk's id. When a correction or a new chunking changes the text, the new chunk starts enabled. **Enable** puts a chunk back.

## The Retrieval Test

**Vault Index → Retrieval test** (`/vault-index/search-test`) runs a query as search runs it, within the operator's read scope, and shows every candidate either leg found, not only the top hits:

- its place in the final order, which is the order search returns;
- its place after fusion, with the fused score;
- its rank in the keyword leg with the full-text score, and in the vector leg with the cosine distance (smaller is nearer);
- the rerank score, for the candidates the reranker read.

The table can be ordered by any of these, which answers most "why is this passage not found" questions: whether a leg found it at all, and where fusion or the reranker put it. The leg weights, the candidates per leg, whether to rerank and how many candidates to rerank can be changed for one run; empty boxes take the host's values (`Vault:IndexSearch`), and nothing is saved. A run that reranks calls the rerank model, like a search.

## Index Profiles

**Vault Index → Index profiles** (`/vault-index/profiles`, permission `VaultIndex.Profiles`) sets how each document type is cut into chunks. The first row is the layer default, used by every type without its own profile (and by the built-in settings, heading chunking at 400 tokens, until it is saved). Profiles follow Extract's two layers: the Host's for Host documents, each tenant's for its own.

| Strategy | What it does | For |
| --- | --- | --- |
| By headings (default) | Chunks follow the heading tree and each carries its heading path. A section larger than the chunk size is split by paragraph. A table stays whole with the text around it; a table larger than the chunk size is split by rows, each piece repeating the header row | Contracts, rules, manuals, invoices, reports |
| By tokens | A fixed token count with overlap, ignoring headings. Chunks carry no heading path, so a hit cites only the document | Text without headings |

Tables written as HTML, as Azure Document Intelligence writes every table (merged cells, captions), are read as tables too: the chunker rewrites them as Markdown tables first, repeating a merged cell's text into every cell it spans and putting the caption above. Figures (`<figure>`) become their text.

**Enrichment** has an LLM add to each chunk a few keywords, which go into the keyword index, and the questions it answers, which go into the keyword index and into what is embedded. Neither appears in what search returns. It is off by default: it costs a model call per 10 chunks, and with a rerank model it changed nothing on the evaluation set (below). It needs an [enrichment model](../configuration/ai-provider.md#enrichment-model-index); without one the switches are disabled, and a profile saved with enrichment rebuilds nothing until a model is added.

Editing a row shows, before anything is saved, what the change would rebuild: the indexed documents whose chunks it changes, their chunks today, and the embedding tokens their last successful indexing took, which is about what re-embedding them costs; with enrichment on, also the model calls it would make. Saving queues the rebuild in the background at low priority: the affected documents are re-chunked and re-embedded one by one, through the same queue and retry cap as an event, and a document whose settings did not change is skipped. **Use default** removes a type's own profile, with the same estimate first.

## REST API

The page uses these endpoints, which other tools can call with the same permissions:

| Endpoint | Permission | Returns |
| --- | --- | --- |
| `GET /api/vault-index/status` | `VaultIndex.Status` | The summary above |
| `GET /api/vault-index/status/documents?status=&filter=&sorting=&skipCount=&maxResultCount=` | `VaultIndex.Status` | Indexed documents within the caller's read scope. `status` is `0` pending, `1` indexing, `2` indexed, `3` failed; `filter` matches the title; `sorting` is `title`, `status` or `lastIndexedTime` with an optional `desc` |
| `GET /api/vault-index/status/documents/{documentId}/runs` | `VaultIndex.Status` | The document's last 20 runs, newest first |
| `POST /api/vault-index/status/documents/{documentId}/rebuild` | `VaultIndex.Status.Rebuild` | Queues the rebuild; nothing to return |
| `GET /api/vault-index/chunks?documentId=&skipCount=&maxResultCount=` | `VaultIndex.Status` | The document's chunks in order, disabled ones included, up to 200 per page, with the disabled count |
| `POST /api/vault-index/chunks/{id}/disable` | `VaultIndex.Status.ManageChunks` | Takes the chunk out of retrieval; nothing to return |
| `POST /api/vault-index/chunks/{id}/enable` | `VaultIndex.Status.ManageChunks` | Puts it back; nothing to return |
| `GET /api/vault-index/profiles` | `VaultIndex.Profiles` | The layer default and every visible document type, with the settings that apply and the indexed document count |
| `GET /api/vault-index/profiles/rebuild-estimate?documentTypeId=&chunkingStrategy=&maxTokensPerChunk=&overlapTokens=&enrichment=` | `VaultIndex.Profiles` | What saving those settings would rebuild: documents, chunks, last embedding tokens, enrichment calls. `chunkingStrategy` is `0` by headings, `1` by tokens; `maxTokensPerChunk` is 100 to 4,000; the overlap is smaller than the chunk; `enrichment` is a flag set, `1` keywords and `2` questions |
| `PUT /api/vault-index/profiles` | `VaultIndex.Profiles` | Saves the layer default (no `documentTypeId`) or a type's profile and queues the rebuild |
| `DELETE /api/vault-index/profiles/{documentTypeId}` | `VaultIndex.Profiles` | Removes the type's own profile and queues the rebuild |
| `POST /api/vault-index/search-test` | `VaultIndex.Status` | The retrieval test. The body is a search's (`query`, `documentTypeCode`, `fieldFilters`) plus optional `keywordWeight`, `vectorWeight` (0 to 10), `candidatesPerLeg` (1 to 100), `rerank` and `rerankCandidateCount` (1 to 100). It returns the settings used, each leg's candidate count and time, and every candidate in final order |

A document or chunk outside the caller's read scope is not found.

## Measuring Retrieval

Retrieval quality is measured with an evaluation set: a corpus of documents and queries whose right answer is known. The first set is in `modules/vault.index/test/Dignite.Vault.Index.EntityFrameworkCore.SqlServer.Tests/Evaluation/`. It holds 18 synthetic Japanese business documents (`Corpus/`) and 94 cases (`evaluation-set.json`):

- **39 field cases**, generated from each document's field values through per-type templates, such as "INV-2026-1112" or "株式会社みどり印刷の請求書". They ask for a document by a value a reviewed field would hold.
- **55 written questions**, each naming the document and the heading of the section that answers it: 44 in Japanese, 5 in Chinese and 6 in English.

The evaluation indexes the corpus through the real ingestion job and embedding model on a real SQL Server, then ranks every case several ways: each leg alone, fusion with keyword weights 1, 0.5 and 0.25, the same rankings reranked when a rerank model is configured, and `service`, the search service as the host would configure it. It reports **Hit@k**, the share of cases whose answer is in the top k, and **MRR@10**, the mean reciprocal rank of the first right answer in the top 10. **Document level** counts any chunk of the right document. **Passage level** also needs the right heading, and covers the written questions.

Run it against a SQL Server 2025 with Full-Text Search, and any OpenAI-compatible embedding endpoint whose vectors match `VAULT_INDEX_EVAL_DIMENSIONS` (default 1024):

```bash
export VAULT_INDEX_TEST_SQLSERVER="Server=localhost,14333;User ID=sa;Password=...;TrustServerCertificate=true"
export VAULT_INDEX_EVAL_EMBEDDING_ENDPOINT="https://api.siliconflow.cn/v1"
export VAULT_INDEX_EVAL_EMBEDDING_API_KEY="..."
export VAULT_INDEX_EVAL_EMBEDDING_MODEL="BAAI/bge-m3"
export VAULT_INDEX_EVAL_RERANK_MODEL="BAAI/bge-reranker-v2-m3"   # optional; _RERANK_ENDPOINT / _RERANK_API_KEY default to the embedding's
export VAULT_INDEX_EVAL_REPORT="$PWD/index-evaluation.md"
dotnet test modules/vault.index/test/Dignite.Vault.Index.EntityFrameworkCore.SqlServer.Tests --filter "FullyQualifiedName~IndexEvaluation"
```

The test creates and fills its own database, `VaultIndexEvaluation`, and writes the report. A second test in the same run compares chunking strategies and writes `VAULT_INDEX_EVAL_STRATEGY_REPORT` (or `index-strategies.md`). It measures; it does not fail on low numbers. Without those variables it is skipped, and so are the store's other SQL Server tests, which need only `VAULT_INDEX_TEST_SQLSERVER`.

### Baseline (2026-11-12)

With `BAAI/bge-m3` (1024 dimensions), heading chunking at 400 tokens and RRF k = 60, on 101 chunks. The full report is `baseline-2026-11-12.md` next to the set.

| | Ranking | Hit@1 | Hit@5 | MRR@10 |
| --- | --- | --- | --- | --- |
| Document level, 94 cases | fused | 0.95 | 0.98 | 0.964 |
| | keyword | 0.84 | 0.93 | 0.878 |
| | vector | 0.95 | 1.00 | 0.969 |
| Passage level, 55 questions | fused | 0.76 | 0.95 | 0.838 |
| | keyword | 0.58 | 0.73 | 0.651 |
| | vector | 0.91 | 1.00 | 0.949 |

What it shows so far:

- On written questions the vector leg alone finds the right section more often than the fused search. Equal-weight fusion lets the keyword leg pull up chunks that share a word with the question but answer something else.
- On field cases (identifiers, names, amounts) the keyword leg is the strongest (MRR 0.983 against 0.938 for vectors).
- Across languages the keyword leg finds almost nothing (MRR 0.409 on the Chinese and English questions), as expected for a Japanese full-text index; the vector leg finds every one.

### Reranking (2026-11-12)

The same set with `BAAI/bge-reranker-v2-m3` reranking the leading 30 candidates (`rerank-2026-11-12.md` next to the set):

| MRR@10 | fused 1:1 | fused 0.25:1 | reranked |
| --- | --- | --- | --- |
| Passage level, 55 questions | 0.832 | 0.873 | 0.974 |
| Document level, 94 cases | 0.959 | 0.948 | 0.995 |
| Field cases, 39 | 0.957 | 0.938 | 1.000 |
| Chinese and English questions (passage) | 0.677 | 0.741 | 0.932 |

Reranking lifts every slice, and reranking the fused or the vector ranking gives the same result here. Down-weighting the keyword leg trades field cases for questions, and at a weight of 0.5 or less it ranks keyword-only finds below every vector candidate, a loss this 101-chunk corpus is too small to show. So the weights stay equal, and a rerank model is the recommended way to rank paraphrased questions. With it, the keyword leg's gap on Chinese and English questions no longer shows in the results. A query translation step for that leg is not needed for now.

### Chunking strategies (2026-11-12)

Version 2 of the set adds three documents whose tables carry the answers: a price list with a Markdown table, and a sales report and an expense claim with HTML tables as Azure Document Intelligence writes them (21 documents, 108 cases, 12 of them answered by a table). The corpus was indexed once per strategy, and each case ranked by the fused legs and by search with `BAAI/bge-reranker-v2-m3` (`strategies-2026-11-12.md` next to the set):

| MRR@10, search with rerank | Passage, 69 questions | Passage, 12 table questions | Document, 108 cases | Chunks |
| --- | --- | --- | --- | --- |
| By headings, 400 tokens (default) | 0.958 | 0.875 | 0.981 | 112 |
| The same, HTML tables dropped (before) | 0.871 | 0.375 | 0.936 | 110 |
| By headings, 200 tokens | 0.972 | 0.958 | 0.991 | 115 |
| By headings, 800 tokens | 0.958 | 0.875 | 0.981 | 112 |
| By tokens, 400 with 40 overlap | 0 | 0 | 0.995 | 25 |

- Reading HTML tables as tables is the largest gain: before it, the table questions found their section half the time at best.
- 200, 400 and 800 tokens differ by one or two questions, because most sections of this corpus are shorter than 200 tokens. The default stays at 400 until a corpus with long sections says otherwise.
- Chunking by tokens finds the right document as well, but its chunks carry no heading path, so no hit can cite a section.

### Enrichment (2026-11-12)

The same set, heading chunking at 400 tokens, enriched by `Qwen/Qwen3-30B-A3B-Instruct-2507` (`enrichment-2026-11-12.md` next to the set):

| MRR@10 | None | Keywords | Questions | Both |
| --- | --- | --- | --- | --- |
| Passage, 69 questions, with rerank | 0.958 | 0.958 | 0.958 | 0.958 |
| Passage, 69 questions, fused legs only | 0.836 | 0.845 | 0.868 | 0.866 |
| Chinese and English questions, fused legs only | 0.718 | 0.759 | 0.818 | 0.807 |
| Document, 108 cases, fused legs only | 0.946 | 0.944 | 0.955 | 0.957 |

With a rerank model, enrichment changes nothing on this set: the reranker reads the chunk text and already puts the answer first. Without one, the questions lift the fused ranking, most for questions asked in another language than the document's. Both kinds together took 22 model calls for the 21 documents, about 670 input and 1,020 output tokens per document. So enrichment stays off by default; it is worth a try on a deployment without a rerank model.

The set is small and synthetic: grow it with real queries before relying on small differences.

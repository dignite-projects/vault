```json
//[doc-seo]
{
    "Description": "Operate Vault Index: the index status page, the chunk view and the retrieval test, their REST API, rebuilding a document or disabling a chunk, and measuring retrieval quality with the evaluation set."
}
```

# Index Status and Evaluation

Vault Index keeps a derived copy of every released document (chunks, vectors and a full-text index). This page covers how an operator sees what is in it, fixes a document that did not index, finds out why a passage is not found, and measures how well it retrieves.

> Vault Index is under development and ships only inside the host. Its Angular library, `@dignite/ng.vault-index`, is built with the host SPA and not published to npm yet.

## The Status Page

The host SPA shows it under **Vault Index → Index Status** (`/vault-index/status`). It needs the `VaultIndex.Status` permission, as do the chunk view and the retrieval test below; the **Rebuild** button needs `VaultIndex.Status.Rebuild`, and disabling a chunk needs `VaultIndex.Status.ManageChunks`. `--migrate-database` grants all three to the seeded `admin` role; grant them to other roles under Identity → Roles.

The summary at the top shows:

- documents by state: indexed, failed, pending or indexing, and those out of retrieval (in Extract's recycle bin, or withheld after a reclassification until their next release);
- chunks, and a warning when the tenant has more than 50,000 vectors. Vector search is exact, and the storage proof of concept sized it for about that many rows after the read-scope filter;
- the embedding model the index was built with and the one the host is configured with. With no model configured nothing is indexed; with a different one, search is refused until the index is rebuilt;
- the keyword index: missing (Full-Text Search not installed, so search uses vectors only), populating after a backfill, or how many changes it has not absorbed yet. It follows writes by about five seconds.

The document list shows each indexed document with its type, state, chunk count, last indexing time and, for a failed one, the last error. It shows only documents within the operator's Extract read scope, because it shows titles. **Runs** expands a document's last 20 index runs: trigger, outcome, attempt, chunks, embedding tokens, and the error or skip reason.

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

**Chunks** on a document's row opens `/vault-index/documents/{documentId}/chunks`: the chunks the document was cut into, in order, each with its heading path, its text and its pointer. Like the document list, it shows only documents within the operator's read scope, including those in the recycle bin or withheld.

**Disable** takes a chunk out of retrieval: search, outline and read no longer return it, for every caller. Use it for a chunk that is noise, such as a repeated disclaimer or a garbled table, and that keeps crowding out the passages people look for. The text itself cannot be edited here: correct the document's Markdown in Extract, so that the index can always be rebuilt from Extract.

A disabled chunk stays disabled across a rebuild as long as its text is unchanged; the mark follows the text, not the chunk's id. When a correction or a new chunking changes the text, the new chunk starts enabled. **Enable** puts a chunk back.

## The Retrieval Test

**Vault Index → Retrieval test** (`/vault-index/search-test`) runs a query as search runs it, within the operator's read scope, and shows every candidate either leg found, not only the top hits:

- its place in the final order, which is the order search returns;
- its place after fusion, with the fused score;
- its rank in the keyword leg with the full-text score, and in the vector leg with the cosine distance (smaller is nearer);
- the rerank score, for the candidates the reranker read.

The table can be ordered by any of these, which answers most "why is this passage not found" questions: whether a leg found it at all, and where fusion or the reranker put it. The leg weights, the candidates per leg, whether to rerank and how many candidates to rerank can be changed for one run; empty boxes take the host's values (`Vault:IndexSearch`), and nothing is saved. A run that reranks calls the rerank model, like a search.

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

The test creates and fills its own database, `VaultIndexEvaluation`, and writes the report. It measures; it does not fail on low numbers. Without those variables it is skipped, and so are the store's other SQL Server tests, which need only `VAULT_INDEX_TEST_SQLSERVER`.

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

The set is small and synthetic: grow it with real queries before relying on small differences.

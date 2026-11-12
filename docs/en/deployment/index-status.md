```json
//[doc-seo]
{
    "Description": "Operate Vault Index: the index status page and its REST API, rebuilding a document, and measuring retrieval quality with the evaluation set."
}
```

# Index Status and Evaluation

Vault Index keeps a derived copy of every released document (chunks, vectors and a full-text index). This page covers how an operator sees what is in it, fixes a document that did not index, and measures how well it retrieves.

> Vault Index is under development and ships only inside the host. Its Angular library, `@dignite/ng.vault-index`, is built with the host SPA and not published to npm yet.

## The Status Page

The host SPA shows it under **Vault Index → Index Status** (`/vault-index/status`). It needs the `VaultIndex.Status` permission; the **Rebuild** button needs `VaultIndex.Status.Rebuild`. `--migrate-database` grants both to the seeded `admin` role; grant them to other roles under Identity → Roles.

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

## REST API

The page uses these endpoints, which other tools can call with the same permissions:

| Endpoint | Permission | Returns |
| --- | --- | --- |
| `GET /api/vault-index/status` | `VaultIndex.Status` | The summary above |
| `GET /api/vault-index/status/documents?status=&filter=&sorting=&skipCount=&maxResultCount=` | `VaultIndex.Status` | Indexed documents within the caller's read scope. `status` is `0` pending, `1` indexing, `2` indexed, `3` failed; `filter` matches the title; `sorting` is `title`, `status` or `lastIndexedTime` with an optional `desc` |
| `GET /api/vault-index/status/documents/{documentId}/runs` | `VaultIndex.Status` | The document's last 20 runs, newest first |
| `POST /api/vault-index/status/documents/{documentId}/rebuild` | `VaultIndex.Status.Rebuild` | Queues the rebuild; nothing to return |

A document outside the caller's read scope is not found.

## Measuring Retrieval

Retrieval quality is measured with an evaluation set: a corpus of documents and queries whose right answer is known. The first set is in `modules/vault.index/test/Dignite.Vault.Index.EntityFrameworkCore.SqlServer.Tests/Evaluation/`. It holds 18 synthetic Japanese business documents (`Corpus/`) and 94 cases (`evaluation-set.json`):

- **39 field cases**, generated from each document's field values through per-type templates, such as "INV-2026-1112" or "株式会社みどり印刷の請求書". They ask for a document by a value a reviewed field would hold.
- **55 written questions**, each naming the document and the heading of the section that answers it: 44 in Japanese, 5 in Chinese and 6 in English.

The evaluation indexes the corpus through the real ingestion job and embedding model on a real SQL Server, then ranks every case three ways: the fused search the egress returns, the keyword leg alone and the vector leg alone. It reports **Hit@k**, the share of cases whose answer is in the top k, and **MRR@10**, the mean reciprocal rank of the first right answer in the top 10. **Document level** counts any chunk of the right document. **Passage level** also needs the right heading, and covers the written questions.

Run it against a SQL Server 2025 with Full-Text Search, and any OpenAI-compatible embedding endpoint whose vectors match `VAULT_INDEX_EVAL_DIMENSIONS` (default 1024):

```bash
export VAULT_INDEX_TEST_SQLSERVER="Server=localhost,14333;User ID=sa;Password=...;TrustServerCertificate=true"
export VAULT_INDEX_EVAL_EMBEDDING_ENDPOINT="https://api.siliconflow.cn/v1"
export VAULT_INDEX_EVAL_EMBEDDING_API_KEY="..."
export VAULT_INDEX_EVAL_EMBEDDING_MODEL="BAAI/bge-m3"
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

How to weight the legs is a decision for the next release, and it will be measured on this set. The set itself is small and synthetic: grow it with real queries before relying on small differences.

# Vault Index storage PoC

Throwaway branch, never merged. Step 2 of the Vault Index implementation order: answers the storage questions of the
design doc (question 4, question 6, and whether `Microsoft.Extensions.DataIngestion` can carry the write side) with
numbers from a real SQL Server 2025. The conclusions are written back into the design doc, section 16.

## Setup

- `docker/Dockerfile`: the official `mcr.microsoft.com/mssql/server:2025-latest` image (SQL Server 17.0.5045.3,
  Ubuntu 24.04) plus `mssql-server-fts` pinned to the same build. Developer edition.
- `IndexStoragePoc/`: a console app on EF Core 10.0.11 (the repository's line) and DataIngestion
  10.11.0-preview.1. Commands: `seed <main> <other>`, `vector`, `ftrank`, `ftlag`, `ftjp`, `fields`, `ingest`.

```bash
docker build -t vault-index-poc-sql:2025-fts docker
docker run -d --name vault-index-poc-sql -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e MSSQL_SA_PASSWORD=<pw> -e MSSQL_MEMORY_LIMIT_MB=6144 -p 14333:1433 vault-index-poc-sql:2025-fts
POC_SA=<pw> dotnet run -c Release --project IndexStoragePoc -- seed 200000 50000
```

Corpus: two tenants (200,000 + 50,000 chunks), 20 document types per tenant with Zipf-skewed sizes, 50 owners,
5 to 15 chunks per document, 1024-dimension vectors clustered by type, Japanese text built from contract, invoice and
manual phrases with one invoice number per document. Machine: Docker Desktop, 12 CPUs, SQL Server capped at 6 GB.

## Results

Numbers are warm-cache medians (p50) and p95 over 20 queries unless stated. Raw output is in `results/`.

### 1. Approximate vector index on SQL Server 2025: not usable for Index

Probed on 17.0.5045.3 with `PREVIEW_FEATURES = ON`:

| Check | Result |
| --- | --- |
| `CREATE VECTOR INDEX` on a table with a `uniqueidentifier` primary key | Refused: needs a clustered primary key on a single 4-byte `int` column |
| Index version (`sys.vector_indexes.build_parameters`) | No `Version` field: the earlier format, not version 3 |
| `INSERT` after the index exists | Msg 42231: the table is read-only while it has a vector index |
| `ALLOW_STALE_VECTOR_INDEX` | Not recognised on this build |
| `SELECT TOP (n) WITH APPROXIMATE` | Syntax error; only the deprecated `TOP_N` form works |
| `VECTOR_SEARCH(..., TOP_N = 3)` + `WHERE K > 150` (50 of 200 rows match) | 0 rows: the filter is applied after the 3 nearest are taken |
| `vector(n, float16)` (half the storage) | Only with `PREVIEW_FEATURES`; "not a recognized vector base type" otherwise |

Microsoft's documentation says the current index version (DML, iterative filtering, no int-key rule) is available on
Azure SQL Database, Fabric and Managed Instance (always-up-to-date), and is preview on SQL Server 2025. On the box
product, Index's default store is therefore exact search with `VECTOR_DISTANCE`, nothing else.

### 2. Exact vector search under the read-scope filter (EF Core 10)

`EF.Functions.VectorDistance("cosine", ...)` top 10, with the tenant query filter and the read-scope predicate (type
list, or type list OR owner) in the same query. EF Core 10 emits one `WHERE TenantId = @tenant AND (DocumentTypeId IN
(...) OR OwnerId = @owner) ORDER BY VECTOR_DISTANCE(...)`.

p50 / p95 per layout. Layout A is what ABP creates (Guid primary key, clustered). B adds a covering index keyed
(TenantId, DocumentTypeId, OwnerId) that includes the vector, so a type-filtered scan reads one contiguous range with no
key lookups. C clusters the table itself on (TenantId, DocumentTypeId, Id) with a nonclustered Guid primary key.

| Candidate rows after filter | A: Guid PK clustered | B: + covering index incl. vector | C: clustered on (TenantId, DocumentTypeId, Id) |
| --- | --- | --- | --- |
| 4,026 (1 small type) | 22.6 / 28.6 ms | 8.8 / 11.8 ms | 12.3 / 12.9 ms |
| 28,024 (5 small types OR owner) | 38.2 / 44.8 ms | 43.6 / 46.4 ms | 46.4 / 50.5 ms |
| 48,825 (1 large type) | 174.8 / 177.8 ms | 88.9 / 105.5 ms | 135.1 / 159.9 ms |
| 118,646 (5 types) | 266.6 / 278.8 ms | 217.1 / 222.7 ms | 272.0 / 278.7 ms |
| 199,989 (whole tenant, ReadAll) | 261.1 / 277.7 ms | 214.9 / 230.6 ms | 179.2 / 188.8 ms |
| 8 concurrent, 1 large type | 26.9 q/s, p50 291 ms | 39.1 q/s, p50 194 ms | 34.1 q/s, p50 225 ms |
| 8 concurrent, whole tenant | 7.5 q/s, p50 1,074 ms | 9.1 q/s, p50 883 ms | 8.1 q/s, p50 1,019 ms |
| first query after `DROPCLEANBUFFERS`, whole tenant | 1,865 ms | 1,488 ms | 1,559 ms |
| keyword `"契約"` (129k matches), whole tenant | 19.7 ms | (A's table) | 151.2 ms |
| keyword `"契約"`, type #0 | 69.8 ms | (A's table) | 345.0 ms |

C helps the vector leg but slows the keyword leg several-fold: full-text returns chunk ids, and each one is now a
nonclustered-key lookup followed by a clustered lookup. B keeps the keyword leg as in A. The layout this points to is
two tables: the chunk text clustered by its Guid key with the full-text index (A's keyword numbers), and the vectors in
their own table clustered by (TenantId, generation, DocumentTypeId, ChunkId) (B's vector numbers, without storing each
vector twice). Separate vector rows are also what lets two embedding generations coexist during a model change.

Storage: 200,000 + 50,000 chunks of 1024 dimensions take 2.0 GB (vectors are 4 KB a row); B's covering index adds
another 1.97 GB. Bulk load: 250,000 chunks in 56 s through `SqlBulkCopy` with `SqlVector<float>` (Microsoft.Data.SqlClient
6.1.6; `DataTable.Clear()` throws on that column, so a new table per batch).

### 3. Keyword leg: ranked full-text on EF Core 10

EF Core 10 has no `CONTAINSTABLE` / `FREETEXTTABLE` translation (EF Core 11 adds it but targets net11.0 only). This
shape works on EF Core 10 and keeps the tenant query filter in EF's hands:

```csharp
var q = from h in db.Database.SqlQuery<KeywordHit>($"SELECT [KEY] AS ChunkId, [RANK] AS Rank FROM CONTAINSTABLE(dbo.VaultIndexChunks, Text, {condition})")
        join c in db.Chunks on h.ChunkId equals c.Id      // the DbSet brings the tenant filter
        where scope(c)
        orderby h.Rank descending
        select new { c.Id, h.Rank };
```

The generated SQL wraps the raw query as a derived table and joins it to `SELECT ... FROM VaultIndexChunks WHERE
TenantId = @ef_filter__CurrentTenantId`.

| Condition | Engine matches (all tenants) | Scoped to type #0 | Whole tenant |
| --- | --- | --- | --- |
| one invoice number | 1 | 3.3 ms | 3.2 ms |
| `"遅延損害金"` | 52,143 | 192.5 ms | 24.9 ms |
| `"契約"` | 128,807 | 69.8 ms | 19.7 ms |
| `"保証金" AND "返還"` | 51,846 | 75.8 ms | 20.1 ms |

The full-text engine ranks over every tenant's rows and the join filters afterwards, so cost follows the matches in
the whole table, and rank statistics are table-wide.

### 4. Keyword leg lag after a write

`CHANGE_TRACKING AUTO`: a committed chunk is found by `CONTAINS` after **p50 4.6 s / p95 4.7 s**; a 50-chunk document
4.5 s / 4.6 s; a 20-chunk replacement (delete and insert in one transaction) is consistent after 4.7 s / 4.8 s. The
vector leg sees the rows at commit. Initial population of 250,000 chunks: 155 to 175 s (about 1,500 chunks/s).

### 5. Japanese full-text (word breaker 1041)

23 queries against 20 sentences; the query is expected to find exactly the listed sentences.
`CONTAINS` 17/23, `FREETEXT` 16/23.

Works: compounds split into words (`賃貸借契約書` → 賃貸借 | 契約 | 書, `電子帳簿保存法` → 電子 | 帳簿 | 保存 | 法), long-vowel
variants (`サーバー` and `サーバ` both indexed), half-width katakana (`ｻｰﾊﾞｰ`), full-width alphanumerics (`ＡＢＣ` = `abc`),
case, an alphanumeric registration number, and Chinese text under the Japanese breaker (`合同`).

Misses, all in `CONTAINS`:

| Query | Expected | Found | Why |
| --- | --- | --- | --- |
| 支払 | 支払う, 支払期限 | 支払期限 only | the verb is indexed as one token `支払う` |
| 123 | `123,456円` (half and full width) | none | number, separator and unit are one token |
| ください / 下さい | both | each finds only itself | orthographic variants are not unified |
| 引渡日 / 引き渡し | both | each finds only its own spelling | okurigana variants are not unified |

### 6. Field filters (question 6)

Vector top 10 within type #0 plus a field condition. A: Index keeps field values in its own table and joins. B:
Extract resolves the condition to document ids first; Index passes them as one JSON parameter (`EF.Parameter(ids)`).
Both returned the same top 10 in every case.

| Condition | Documents | Chunks | A: join | B: id list |
| --- | --- | --- | --- | --- |
| counterparty, rare | 3 | 33 | 2.5 ms | 1.7 ms |
| counterparty | 30 | 278 | 4.1 ms | 3.2 ms |
| counterparty | 197 | 1,887 | 12.6 ms | 12.1 ms (8 KB of ids) |
| counterparty, most common | 497 | 4,995 | 30.7 ms | 30.3 ms (19 KB) |
| amount > 5,000,000 | 2,471 | 24,743 | 43.6 ms | 114.0 ms (94 KB) |

B's time excludes Extract's own query for the ids.

### 7. DataIngestion as the write side

DataIngestion 10.11.0-preview.1 (the build that depends on Microsoft.Extensions.AI 10.11.0, the repository's version;
11.0.0-preview.1 pulls Microsoft.Extensions.AI 11.0.0 into a net10.0 host).

- Building blocks on a Markdown string work: `MarkdownReader.ReadAsync(stream, id, "text/markdown")` then
  `HeaderChunker` (o200k tokenizer, 120 tokens) split the sample contract into 5 chunks, each with its heading path as
  `Context` (`# 賃貸借契約書 ## 第2条（賃料） ### 2.1 遅延損害金`) and that path prefixed to the content; the table stayed
  in one chunk.
- `IngestionPipeline<T>.ProcessAsync` accepts only `DirectoryInfo` or `IEnumerable<FileInfo>`, in 10.11 and in
  11.0.0-preview.1. Index receives Markdown from Extract in memory, so the pipeline would need a temp file per document.
- With a custom `IngestionChunkWriter<string>`: the writer is called once per document (2 calls for 2 documents), so
  replace-in-one-transaction works; an `AsyncLocal` tenant flows into it; `chunk.Document.Identifier` is the file
  path, the only document identity it carries. A failed document (empty file) never reaches the writer; its
  `IngestionResult` carries `Succeeded = false` and the exception, nothing per stage (no timings, no token usage).

## What this changes in the design

1. On SQL Server 2025 the default store is exact search only. Sizing rule from section 2: keep the candidate set
   after the read-scope filter near or under 50,000 chunks for about 0.1 s; a ReadAll query over a 200,000-chunk
   tenant costs about 0.2 s alone and saturates at about 8 to 9 queries/s on 12 cores. Approximate search is an Azure
   SQL capability (current index version) or an external engine through a VectorData package, not a box-product one.
2. Vectors go in their own table, clustered by (TenantId, generation, DocumentTypeId, ChunkId); chunk text stays in a
   Guid-keyed table with the full-text index. The vector dimension is part of the column type, so an embedding model
   with a different dimension needs a migration, not just a new generation row.
3. The keyword leg works on EF Core 10 through `SqlQuery` + a LINQ join to the filtered `DbSet`; the full-text index
   is created by `migrationBuilder.Sql`. It lags writes by about 4.6 s, and its ranking is table-wide.
4. Japanese: nouns, compounds, katakana and width variants are found; okurigana and orthographic variants, verb
   inflection and number-with-unit tokens are not. The vector leg covers meaning; a thesaurus (`FORMSOF(THESAURUS, ...)`
   with `tsjpn.xml`) is the P1 candidate for spelling variants.
5. Field filters: resolve them in Extract to document ids (B). Same speed as a local copy up to hundreds of
   documents, slower only for broad conditions (+70 ms at 2,471 ids), and no copied field values, so no rename gap.
6. DataIngestion: use `MarkdownReader`, the chunkers and the enrichers directly; do not use `IngestionPipeline`,
   whose input is files only. Index's job owns the transaction, the run record and the retry counter. Pin
   10.11.0-preview.1, which matches the repository's Microsoft.Extensions.AI 10.11.0.
7. Deployment: Index's database must be SQL Server 2025 with Full-Text Search installed (the official container image
   lacks it; `docker/Dockerfile` adds it) or Azure SQL. The shared development server is SQL Server 2022 and needs an
   upgrade before Index can run there.

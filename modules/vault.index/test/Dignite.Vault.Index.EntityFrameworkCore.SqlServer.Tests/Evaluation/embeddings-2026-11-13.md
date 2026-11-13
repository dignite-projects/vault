# Vault Index embedding models

Four runs of `Measure_retrieval_on_the_evaluation_set` on evaluation set version 4, one per embedding model, each at 1024 dimensions (the shipped `vector(1024)` column) with the rerank model `BAAI/bge-reranker-v2-m3`, on 2026-11-13. Each run's report follows in full.

## BAAI/bge-m3 (native 1024)

- Evaluation set version 4: 23 documents, 143 chunks, 127 cases (43 field, 72 written; 9 Chinese, 11 English)
- Embedding model BAAI/bge-m3 (1024 dimensions); chunking Heading:400:0; RRF k = 60, 50 candidates per leg
- Keyword leg: SQL Server full-text FREETEXTTABLE, Japanese word breaker (LCID 1041)
- Rerank model BAAI/bge-reranker-v2-m3, top 30 of each ranking; mean rerank call 582 ms (one call per case over the union of candidates)
- `service` is IIndexSearchAppService as configured: keyword weight 1, vector weight 1, reranker BAAI/bge-reranker-v2-m3
- Run 2026-11-13 06:40 UTC

### Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 127 | 0.80 | 0.89 | 0.90 | 0.91 | 0.844 |
| vector | 127 | 0.91 | 0.98 | 0.98 | 0.99 | 0.945 |
| fused 1:1 | 127 | 0.91 | 0.97 | 0.98 | 0.98 | 0.941 |
| fused 0.5:1 | 127 | 0.90 | 0.98 | 0.98 | 0.99 | 0.938 |
| fused 0.25:1 | 127 | 0.89 | 0.97 | 0.98 | 0.99 | 0.933 |
| rerank vector | 127 | 0.97 | 0.99 | 0.99 | 0.99 | 0.980 |
| rerank fused 1:1 | 127 | 0.97 | 0.99 | 0.99 | 0.99 | 0.980 |
| rerank fused 0.5:1 | 127 | 0.97 | 0.99 | 0.99 | 0.99 | 0.980 |
| service | 127 | 0.97 | 0.99 | 0.99 | 0.99 | 0.980 |

### Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 84 | 0.61 | 0.74 | 0.76 | 0.79 | 0.673 |
| vector | 84 | 0.88 | 0.96 | 0.96 | 0.98 | 0.924 |
| fused 1:1 | 84 | 0.77 | 0.88 | 0.94 | 0.96 | 0.838 |
| fused 0.5:1 | 84 | 0.80 | 0.92 | 0.94 | 0.96 | 0.861 |
| fused 0.25:1 | 84 | 0.81 | 0.93 | 0.95 | 0.98 | 0.874 |
| rerank vector | 84 | 0.94 | 0.98 | 0.99 | 0.99 | 0.959 |
| rerank fused 1:1 | 84 | 0.94 | 0.98 | 0.99 | 0.99 | 0.959 |
| rerank fused 0.5:1 | 84 | 0.94 | 0.98 | 0.99 | 0.99 | 0.959 |
| service | 84 | 0.94 | 0.98 | 0.99 | 0.99 | 0.959 |

### Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.984 |
| vector | 43 | 0.88 | 0.98 | 1.00 | 1.00 | 0.928 |
| fused 1:1 | 43 | 0.95 | 1.00 | 1.00 | 1.00 | 0.973 |
| fused 0.5:1 | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.950 |
| fused 0.25:1 | 43 | 0.88 | 0.98 | 1.00 | 1.00 | 0.932 |
| rerank vector | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 0.5:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

### Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 64 | 0.72 | 0.88 | 0.91 | 0.92 | 0.798 |
| vector | 64 | 0.89 | 0.98 | 0.98 | 0.98 | 0.938 |
| fused 1:1 | 64 | 0.81 | 0.92 | 0.97 | 0.97 | 0.872 |
| fused 0.5:1 | 64 | 0.83 | 0.95 | 0.97 | 0.97 | 0.889 |
| fused 0.25:1 | 64 | 0.84 | 0.97 | 0.98 | 0.98 | 0.905 |
| rerank vector | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 1:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 0.5:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| service | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |

### Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.274 |
| vector | 20 | 0.85 | 0.90 | 0.90 | 0.95 | 0.880 |
| fused 1:1 | 20 | 0.65 | 0.75 | 0.85 | 0.95 | 0.727 |
| fused 0.5:1 | 20 | 0.70 | 0.80 | 0.85 | 0.95 | 0.773 |
| fused 0.25:1 | 20 | 0.70 | 0.80 | 0.85 | 0.95 | 0.776 |
| rerank vector | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| rerank fused 1:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| rerank fused 0.5:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |

### Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| vector | 12 | 0.75 | 0.83 | 0.83 | 0.83 | 0.792 |
| fused 1:1 | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| fused 0.5:1 | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| fused 0.25:1 | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| rerank vector | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| rerank fused 1:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| rerank fused 0.5:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |

### Misses of `service` (answer not first)

| Query | Expected | keyword | vector | fused 1:1 | fused 0.5:1 | fused 0.25:1 | rerank vector | rerank fused 1:1 | rerank fused 0.5:1 | service |
|---|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 5 | 2 | 3 | 3 | 2 | 3 | 3 | 3 | 3 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | - | 10 | 10 | 10 | 10 | 4 | 4 | 4 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | - | - | - | - | - | - | - | - |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 1 | 1 | 1 | 1 | 2 | 2 | 2 | 2 |
| 損害賠償の上限はいくら | outsourcing-basic-terms / 第10条 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |

## Qwen/Qwen3-Embedding-0.6B (native 1024)

- Evaluation set version 4: 23 documents, 143 chunks, 127 cases (43 field, 72 written; 9 Chinese, 11 English)
- Embedding model Qwen/Qwen3-Embedding-0.6B (1024 dimensions); chunking Heading:400:0; RRF k = 60, 50 candidates per leg
- Keyword leg: SQL Server full-text FREETEXTTABLE, Japanese word breaker (LCID 1041)
- Rerank model BAAI/bge-reranker-v2-m3, top 30 of each ranking; mean rerank call 590 ms (one call per case over the union of candidates)
- `service` is IIndexSearchAppService as configured: keyword weight 1, vector weight 1, reranker BAAI/bge-reranker-v2-m3
- Run 2026-11-13 06:44 UTC

### Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 127 | 0.80 | 0.90 | 0.90 | 0.91 | 0.843 |
| vector | 127 | 0.89 | 0.98 | 0.99 | 1.00 | 0.936 |
| fused 1:1 | 127 | 0.91 | 0.96 | 0.98 | 1.00 | 0.943 |
| fused 0.5:1 | 127 | 0.89 | 0.97 | 0.98 | 1.00 | 0.933 |
| fused 0.25:1 | 127 | 0.88 | 0.98 | 0.98 | 1.00 | 0.931 |
| rerank vector | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| rerank fused 1:1 | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| rerank fused 0.5:1 | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| service | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |

### Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 84 | 0.61 | 0.75 | 0.77 | 0.80 | 0.679 |
| vector | 84 | 0.81 | 0.96 | 0.99 | 1.00 | 0.886 |
| fused 1:1 | 84 | 0.75 | 0.87 | 0.95 | 0.99 | 0.832 |
| fused 0.5:1 | 84 | 0.79 | 0.90 | 0.96 | 0.99 | 0.857 |
| fused 0.25:1 | 84 | 0.79 | 0.93 | 0.96 | 0.99 | 0.863 |
| rerank vector | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| rerank fused 1:1 | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| rerank fused 0.5:1 | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| service | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |

### Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.984 |
| vector | 43 | 0.88 | 1.00 | 1.00 | 1.00 | 0.934 |
| fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fused 0.5:1 | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.950 |
| fused 0.25:1 | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.946 |
| rerank vector | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 0.5:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

### Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 64 | 0.72 | 0.89 | 0.92 | 0.94 | 0.805 |
| vector | 64 | 0.83 | 0.98 | 1.00 | 1.00 | 0.904 |
| fused 1:1 | 64 | 0.80 | 0.91 | 0.97 | 0.98 | 0.866 |
| fused 0.5:1 | 64 | 0.81 | 0.94 | 0.98 | 0.98 | 0.883 |
| fused 0.25:1 | 64 | 0.81 | 0.97 | 0.98 | 0.98 | 0.889 |
| rerank vector | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 1:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 0.5:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| service | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |

### Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.275 |
| vector | 20 | 0.75 | 0.90 | 0.95 | 1.00 | 0.826 |
| fused 1:1 | 20 | 0.60 | 0.75 | 0.90 | 1.00 | 0.721 |
| fused 0.5:1 | 20 | 0.70 | 0.80 | 0.90 | 1.00 | 0.775 |
| fused 0.25:1 | 20 | 0.70 | 0.80 | 0.90 | 1.00 | 0.777 |
| rerank vector | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| rerank fused 1:1 | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| rerank fused 0.5:1 | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| service | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |

### Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| vector | 12 | 0.67 | 0.83 | 0.92 | 1.00 | 0.762 |
| fused 1:1 | 12 | 0.67 | 0.92 | 0.92 | 1.00 | 0.801 |
| fused 0.5:1 | 12 | 0.67 | 0.92 | 0.92 | 1.00 | 0.801 |
| fused 0.25:1 | 12 | 0.67 | 0.92 | 0.92 | 1.00 | 0.801 |
| rerank vector | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| rerank fused 1:1 | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| rerank fused 0.5:1 | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| service | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |

### Misses of `service` (answer not first)

| Query | Expected | keyword | vector | fused 1:1 | fused 0.5:1 | fused 0.25:1 | rerank vector | rerank fused 1:1 | rerank fused 0.5:1 | service |
|---|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 5 | 3 | 4 | 4 | 3 | 3 | 3 | 3 | 3 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | - | 4 | 4 | 4 | 4 | 4 | 4 | 4 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | 9 | 9 | 9 | 9 | 2 | 2 | 2 | 2 |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |
| 損害賠償の上限はいくら | outsourcing-basic-terms / 第10条 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |

## Qwen/Qwen3-Embedding-4B (dimensions 1024)

- Evaluation set version 4: 23 documents, 143 chunks, 127 cases (43 field, 72 written; 9 Chinese, 11 English)
- Embedding model Qwen/Qwen3-Embedding-4B (1024 dimensions); chunking Heading:400:0; RRF k = 60, 50 candidates per leg
- Keyword leg: SQL Server full-text FREETEXTTABLE, Japanese word breaker (LCID 1041)
- Rerank model BAAI/bge-reranker-v2-m3, top 30 of each ranking; mean rerank call 584 ms (one call per case over the union of candidates)
- `service` is IIndexSearchAppService as configured: keyword weight 1, vector weight 1, reranker BAAI/bge-reranker-v2-m3
- Run 2026-11-13 06:47 UTC

### Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 127 | 0.81 | 0.91 | 0.91 | 0.91 | 0.853 |
| vector | 127 | 0.91 | 0.98 | 1.00 | 1.00 | 0.947 |
| fused 1:1 | 127 | 0.91 | 0.98 | 0.98 | 1.00 | 0.948 |
| fused 0.5:1 | 127 | 0.91 | 0.98 | 0.99 | 1.00 | 0.944 |
| fused 0.25:1 | 127 | 0.91 | 0.98 | 0.99 | 1.00 | 0.943 |
| rerank vector | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| rerank fused 1:1 | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| rerank fused 0.5:1 | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| service | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |

### Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 84 | 0.62 | 0.74 | 0.77 | 0.80 | 0.682 |
| vector | 84 | 0.87 | 0.98 | 1.00 | 1.00 | 0.928 |
| fused 1:1 | 84 | 0.79 | 0.90 | 0.95 | 0.99 | 0.854 |
| fused 0.5:1 | 84 | 0.81 | 0.93 | 0.96 | 0.99 | 0.876 |
| fused 0.25:1 | 84 | 0.83 | 0.94 | 0.98 | 0.99 | 0.894 |
| rerank vector | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| rerank fused 1:1 | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| rerank fused 0.5:1 | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| service | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |

### Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.984 |
| vector | 43 | 0.91 | 0.98 | 1.00 | 1.00 | 0.944 |
| fused 1:1 | 43 | 0.95 | 1.00 | 1.00 | 1.00 | 0.977 |
| fused 0.5:1 | 43 | 0.95 | 1.00 | 1.00 | 1.00 | 0.973 |
| fused 0.25:1 | 43 | 0.95 | 0.98 | 1.00 | 1.00 | 0.967 |
| rerank vector | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 0.5:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

### Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 64 | 0.73 | 0.88 | 0.92 | 0.94 | 0.810 |
| vector | 64 | 0.88 | 0.98 | 1.00 | 1.00 | 0.934 |
| fused 1:1 | 64 | 0.80 | 0.92 | 0.97 | 0.98 | 0.867 |
| fused 0.5:1 | 64 | 0.81 | 0.95 | 0.97 | 0.98 | 0.883 |
| fused 0.25:1 | 64 | 0.84 | 0.97 | 0.98 | 0.98 | 0.908 |
| rerank vector | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 1:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 0.5:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| service | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |

### Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.274 |
| vector | 20 | 0.85 | 0.95 | 1.00 | 1.00 | 0.910 |
| fused 1:1 | 20 | 0.75 | 0.85 | 0.90 | 1.00 | 0.814 |
| fused 0.5:1 | 20 | 0.80 | 0.85 | 0.95 | 1.00 | 0.851 |
| fused 0.25:1 | 20 | 0.80 | 0.85 | 0.95 | 1.00 | 0.851 |
| rerank vector | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| rerank fused 1:1 | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| rerank fused 0.5:1 | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| service | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |

### Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| vector | 12 | 0.75 | 1.00 | 1.00 | 1.00 | 0.875 |
| fused 1:1 | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| fused 0.5:1 | 12 | 0.75 | 1.00 | 1.00 | 1.00 | 0.875 |
| fused 0.25:1 | 12 | 0.75 | 1.00 | 1.00 | 1.00 | 0.875 |
| rerank vector | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| rerank fused 1:1 | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| rerank fused 0.5:1 | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| service | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |

### Misses of `service` (answer not first)

| Query | Expected | keyword | vector | fused 1:1 | fused 0.5:1 | fused 0.25:1 | rerank vector | rerank fused 1:1 | rerank fused 0.5:1 | service |
|---|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 4 | 2 | 3 | 2 | 2 | 3 | 3 | 3 | 3 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | - | 1 | 1 | 1 | 1 | 4 | 4 | 4 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | 1 | 1 | 1 | 1 | 2 | 2 | 2 | 2 |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |
| 損害賠償の上限はいくら | outsourcing-basic-terms / 第10条 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |

## Qwen/Qwen3-Embedding-8B (dimensions 1024)

- Evaluation set version 4: 23 documents, 143 chunks, 127 cases (43 field, 72 written; 9 Chinese, 11 English)
- Embedding model Qwen/Qwen3-Embedding-8B (1024 dimensions); chunking Heading:400:0; RRF k = 60, 50 candidates per leg
- Keyword leg: SQL Server full-text FREETEXTTABLE, Japanese word breaker (LCID 1041)
- Rerank model BAAI/bge-reranker-v2-m3, top 30 of each ranking; mean rerank call 584 ms (one call per case over the union of candidates)
- `service` is IIndexSearchAppService as configured: keyword weight 1, vector weight 1, reranker BAAI/bge-reranker-v2-m3
- Run 2026-11-13 06:53 UTC

### Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 127 | 0.81 | 0.90 | 0.90 | 0.91 | 0.852 |
| vector | 127 | 0.89 | 0.98 | 0.99 | 1.00 | 0.937 |
| fused 1:1 | 127 | 0.89 | 0.98 | 0.99 | 1.00 | 0.940 |
| fused 0.5:1 | 127 | 0.88 | 0.99 | 0.99 | 1.00 | 0.938 |
| fused 0.25:1 | 127 | 0.90 | 0.99 | 1.00 | 1.00 | 0.945 |
| rerank vector | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| rerank fused 1:1 | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| rerank fused 0.5:1 | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| service | 127 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |

### Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 84 | 0.61 | 0.74 | 0.77 | 0.80 | 0.676 |
| vector | 84 | 0.87 | 0.99 | 1.00 | 1.00 | 0.932 |
| fused 1:1 | 84 | 0.77 | 0.94 | 0.96 | 0.99 | 0.862 |
| fused 0.5:1 | 84 | 0.81 | 0.94 | 0.96 | 0.99 | 0.882 |
| fused 0.25:1 | 84 | 0.82 | 0.96 | 0.99 | 0.99 | 0.894 |
| rerank vector | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| rerank fused 1:1 | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| rerank fused 0.5:1 | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |
| service | 84 | 0.94 | 0.99 | 1.00 | 1.00 | 0.965 |

### Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| vector | 43 | 0.86 | 0.98 | 0.98 | 1.00 | 0.914 |
| fused 1:1 | 43 | 0.95 | 1.00 | 1.00 | 1.00 | 0.977 |
| fused 0.5:1 | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.953 |
| fused 0.25:1 | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.950 |
| rerank vector | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| rerank fused 0.5:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

### Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 64 | 0.72 | 0.88 | 0.92 | 0.94 | 0.802 |
| vector | 64 | 0.83 | 0.98 | 1.00 | 1.00 | 0.910 |
| fused 1:1 | 64 | 0.77 | 0.95 | 0.97 | 0.98 | 0.863 |
| fused 0.5:1 | 64 | 0.80 | 0.95 | 0.97 | 0.98 | 0.878 |
| fused 0.25:1 | 64 | 0.81 | 0.98 | 0.98 | 0.98 | 0.893 |
| rerank vector | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 1:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| rerank fused 0.5:1 | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| service | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |

### Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.275 |
| vector | 20 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fused 1:1 | 20 | 0.80 | 0.90 | 0.95 | 1.00 | 0.860 |
| fused 0.5:1 | 20 | 0.85 | 0.90 | 0.95 | 1.00 | 0.894 |
| fused 0.25:1 | 20 | 0.85 | 0.90 | 1.00 | 1.00 | 0.898 |
| rerank vector | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| rerank fused 1:1 | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| rerank fused 0.5:1 | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |
| service | 20 | 0.90 | 0.95 | 1.00 | 1.00 | 0.938 |

### Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| vector | 12 | 0.75 | 0.92 | 1.00 | 1.00 | 0.854 |
| fused 1:1 | 12 | 0.75 | 1.00 | 1.00 | 1.00 | 0.875 |
| fused 0.5:1 | 12 | 0.75 | 1.00 | 1.00 | 1.00 | 0.875 |
| fused 0.25:1 | 12 | 0.75 | 1.00 | 1.00 | 1.00 | 0.875 |
| rerank vector | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| rerank fused 1:1 | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| rerank fused 0.5:1 | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| service | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |

### Misses of `service` (answer not first)

| Query | Expected | keyword | vector | fused 1:1 | fused 0.5:1 | fused 0.25:1 | rerank vector | rerank fused 1:1 | rerank fused 0.5:1 | service |
|---|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 4 | 2 | 2 | 2 | 2 | 3 | 3 | 3 | 3 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | - | 1 | 1 | 1 | 1 | 4 | 4 | 4 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | 1 | 1 | 1 | 1 | 2 | 2 | 2 | 2 |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |
| 損害賠償の上限はいくら | outsourcing-basic-terms / 第10条 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |


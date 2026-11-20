# Vault Index field context

- Evaluation set version 6: 23 documents (20 with fields), 146 cases (19 entity questions)
- Embedding model BAAI/bge-m3; heading chunking at 400 tokens; `service` is IIndexSearchAppService as configured; with fields its reranker reads title, fields and chunk
- Rerank model BAAI/bge-reranker-v2-m3, top 30 of each ranking
- Run 2026-11-20 05:29 UTC

## Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 146 | 0.82 | 0.91 | 0.91 | 0.92 | 0.864 |
| title · vector | 146 | 0.92 | 0.98 | 0.99 | 0.99 | 0.948 |
| title · fused 1:1 | 146 | 0.92 | 0.97 | 0.98 | 0.99 | 0.949 |
| title · rerank fused 1:1 | 146 | 0.97 | 0.99 | 0.99 | 0.99 | 0.983 |
| title · service | 146 | 0.97 | 0.99 | 0.99 | 0.99 | 0.983 |
| fields · keyword | 146 | 0.82 | 0.91 | 0.91 | 0.92 | 0.864 |
| fields · vector | 146 | 0.94 | 0.99 | 0.99 | 0.99 | 0.961 |
| fields · fused 1:1 | 146 | 0.95 | 0.97 | 0.98 | 0.99 | 0.962 |
| fields · rerank fused 1:1 | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |
| fields · service | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |
| fields+keyword · keyword | 146 | 0.83 | 0.91 | 0.91 | 0.92 | 0.867 |
| fields+keyword · vector | 146 | 0.94 | 0.99 | 0.99 | 0.99 | 0.961 |
| fields+keyword · fused 1:1 | 146 | 0.95 | 0.98 | 0.99 | 0.99 | 0.963 |
| fields+keyword · rerank fused 1:1 | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |
| fields+keyword · service | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |

## Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 103 | 0.59 | 0.76 | 0.80 | 0.82 | 0.675 |
| title · vector | 103 | 0.86 | 0.96 | 0.96 | 0.98 | 0.913 |
| title · fused 1:1 | 103 | 0.76 | 0.88 | 0.94 | 0.97 | 0.833 |
| title · rerank fused 1:1 | 103 | 0.93 | 0.98 | 0.99 | 0.99 | 0.957 |
| title · service | 103 | 0.93 | 0.98 | 0.99 | 0.99 | 0.957 |
| fields · keyword | 103 | 0.59 | 0.76 | 0.80 | 0.82 | 0.675 |
| fields · vector | 103 | 0.94 | 0.98 | 0.98 | 0.98 | 0.960 |
| fields · fused 1:1 | 103 | 0.79 | 0.92 | 0.96 | 0.98 | 0.863 |
| fields · rerank fused 1:1 | 103 | 0.96 | 0.98 | 0.99 | 0.99 | 0.973 |
| fields · service | 103 | 0.96 | 0.98 | 0.99 | 0.99 | 0.973 |
| fields+keyword · keyword | 103 | 0.59 | 0.76 | 0.82 | 0.84 | 0.681 |
| fields+keyword · vector | 103 | 0.94 | 0.98 | 0.98 | 0.98 | 0.960 |
| fields+keyword · fused 1:1 | 103 | 0.80 | 0.92 | 0.96 | 0.99 | 0.867 |
| fields+keyword · rerank fused 1:1 | 103 | 0.96 | 0.98 | 0.99 | 0.99 | 0.973 |
| fields+keyword · service | 103 | 0.96 | 0.98 | 0.99 | 0.99 | 0.973 |

## Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| title · vector | 43 | 0.88 | 0.98 | 1.00 | 1.00 | 0.928 |
| title · fused 1:1 | 43 | 0.93 | 1.00 | 1.00 | 1.00 | 0.965 |
| title · rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| title · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields · vector | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.950 |
| fields · fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields+keyword · vector | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.950 |
| fields+keyword · fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 83 | 0.67 | 0.87 | 0.92 | 0.93 | 0.772 |
| title · vector | 83 | 0.87 | 0.98 | 0.98 | 0.99 | 0.921 |
| title · fused 1:1 | 83 | 0.78 | 0.92 | 0.96 | 0.98 | 0.859 |
| title · rerank fused 1:1 | 83 | 0.94 | 1.00 | 1.00 | 1.00 | 0.968 |
| title · service | 83 | 0.94 | 1.00 | 1.00 | 1.00 | 0.968 |
| fields · keyword | 83 | 0.67 | 0.87 | 0.92 | 0.93 | 0.772 |
| fields · vector | 83 | 0.95 | 0.99 | 0.99 | 0.99 | 0.970 |
| fields · fused 1:1 | 83 | 0.81 | 0.95 | 0.98 | 0.99 | 0.884 |
| fields · rerank fused 1:1 | 83 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields · service | 83 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields+keyword · keyword | 83 | 0.67 | 0.84 | 0.90 | 0.94 | 0.768 |
| fields+keyword · vector | 83 | 0.95 | 0.99 | 0.99 | 0.99 | 0.970 |
| fields+keyword · fused 1:1 | 83 | 0.80 | 0.94 | 0.98 | 1.00 | 0.873 |
| fields+keyword · rerank fused 1:1 | 83 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields+keyword · service | 83 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |

## Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.274 |
| title · vector | 20 | 0.85 | 0.90 | 0.90 | 0.95 | 0.880 |
| title · fused 1:1 | 20 | 0.65 | 0.75 | 0.85 | 0.95 | 0.727 |
| title · rerank fused 1:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| title · service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields · keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.275 |
| fields · vector | 20 | 0.90 | 0.95 | 0.95 | 0.95 | 0.917 |
| fields · fused 1:1 | 20 | 0.70 | 0.80 | 0.90 | 0.95 | 0.778 |
| fields · rerank fused 1:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields · service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields+keyword · keyword | 20 | 0.25 | 0.40 | 0.45 | 0.45 | 0.321 |
| fields+keyword · vector | 20 | 0.90 | 0.95 | 0.95 | 0.95 | 0.917 |
| fields+keyword · fused 1:1 | 20 | 0.80 | 0.85 | 0.90 | 0.95 | 0.841 |
| fields+keyword · rerank fused 1:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields+keyword · service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |

## Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| title · vector | 12 | 0.75 | 0.83 | 0.83 | 0.83 | 0.792 |
| title · fused 1:1 | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| title · rerank fused 1:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| title · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields · keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| fields · vector | 12 | 0.75 | 0.83 | 0.83 | 0.83 | 0.792 |
| fields · fused 1:1 | 12 | 0.67 | 0.92 | 0.92 | 0.92 | 0.792 |
| fields · rerank fused 1:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields+keyword · keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| fields+keyword · vector | 12 | 0.75 | 0.83 | 0.83 | 0.83 | 0.792 |
| fields+keyword · fused 1:1 | 12 | 0.67 | 0.92 | 0.92 | 0.92 | 0.792 |
| fields+keyword · rerank fused 1:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields+keyword · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |

## Document level, entity questions (named only by a field value)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| title · vector | 19 | 0.95 | 1.00 | 1.00 | 1.00 | 0.965 |
| title · fused 1:1 | 19 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| title · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| title · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · keyword | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| fields · vector | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · keyword | 19 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| fields+keyword · vector | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Passage level, entity questions

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 19 | 0.47 | 0.84 | 0.89 | 0.89 | 0.645 |
| title · vector | 19 | 0.79 | 0.95 | 0.95 | 1.00 | 0.867 |
| title · fused 1:1 | 19 | 0.63 | 0.89 | 0.95 | 1.00 | 0.784 |
| title · rerank fused 1:1 | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| title · service | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| fields · keyword | 19 | 0.47 | 0.84 | 0.89 | 0.89 | 0.645 |
| fields · vector | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · fused 1:1 | 19 | 0.79 | 0.95 | 1.00 | 1.00 | 0.879 |
| fields · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · keyword | 19 | 0.47 | 0.74 | 0.89 | 1.00 | 0.642 |
| fields+keyword · vector | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · fused 1:1 | 19 | 0.74 | 0.95 | 1.00 | 1.00 | 0.838 |
| fields+keyword · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields+keyword · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Misses of `title · rerank fused 1:1` (answer not first)

| Query | Expected | title · keyword | title · vector | title · fused 1:1 | title · rerank fused 1:1 | title · service | fields · keyword | fields · vector | fields · fused 1:1 | fields · rerank fused 1:1 | fields · service | fields+keyword · keyword | fields+keyword · vector | fields+keyword · fused 1:1 | fields+keyword · rerank fused 1:1 | fields+keyword · service |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 4 | 2 | 3 | 3 | 3 | 4 | 1 | 2 | 1 | 1 | - | 1 | 4 | 1 | 1 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | - | 10 | 10 | 4 | 4 | - | 1 | 1 | 4 | 4 | - | 1 | 1 | 4 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 1 | 1 | 2 | 2 | 2 | 1 | 2 | 2 | 2 | 2 | 1 | 2 | 2 | 2 |
| 損害賠償の上限はいくら | outsourcing-basic-terms / 第10条 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |
| 山田太郎さんが借りている部屋の敷金はいくらですか | lease-shibakoen / 第3条 | 2 | 1 | 1 | 2 | 2 | 2 | 1 | 1 | 1 | 1 | 2 | 1 | 1 | 1 | 1 |
| サンプル商事との保守委託契約で業務を下請けに出せるか | outsourcing-system / 第4条 | 3 | 7 | 4 | 2 | 2 | 3 | 1 | 2 | 1 | 1 | 6 | 1 | 4 | 1 | 1 |

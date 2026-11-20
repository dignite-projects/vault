# Vault Index field context

- Evaluation set version 6: 23 documents (20 with fields), 146 cases (19 entity questions)
- Embedding model BAAI/bge-m3; heading chunking at 400 tokens; `service` is IIndexSearchAppService as configured; with fields its reranker reads title, fields and chunk
- Rerank model BAAI/bge-reranker-v2-m3, top 30 of each ranking
- Run 2026-11-20 06:06 UTC

## Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 146 | 0.81 | 0.91 | 0.92 | 0.92 | 0.857 |
| title · vector | 146 | 0.92 | 0.98 | 0.99 | 0.99 | 0.948 |
| title · fused 1:1 | 146 | 0.90 | 0.97 | 0.98 | 0.99 | 0.937 |
| title · rerank fused 1:1 | 146 | 0.97 | 0.99 | 0.99 | 0.99 | 0.983 |
| title · service | 146 | 0.97 | 0.99 | 0.99 | 0.99 | 0.983 |
| fields·identifiers · keyword | 146 | 0.82 | 0.91 | 0.92 | 0.92 | 0.865 |
| fields·identifiers · vector | 146 | 0.95 | 0.99 | 0.99 | 0.99 | 0.963 |
| fields·identifiers · fused 1:1 | 146 | 0.95 | 0.97 | 0.98 | 0.99 | 0.965 |
| fields·identifiers · rerank fused 1:1 | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |
| fields·identifiers · service | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |
| fields·Japanese names · keyword | 146 | 0.82 | 0.91 | 0.92 | 0.92 | 0.865 |
| fields·Japanese names · vector | 146 | 0.94 | 0.99 | 0.99 | 0.99 | 0.961 |
| fields·Japanese names · fused 1:1 | 146 | 0.95 | 0.97 | 0.98 | 0.99 | 0.962 |
| fields·Japanese names · rerank fused 1:1 | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |
| fields·Japanese names · service | 146 | 0.98 | 0.99 | 0.99 | 0.99 | 0.986 |

## Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 103 | 0.59 | 0.78 | 0.80 | 0.82 | 0.683 |
| title · vector | 103 | 0.86 | 0.96 | 0.96 | 0.98 | 0.913 |
| title · fused 1:1 | 103 | 0.76 | 0.87 | 0.94 | 0.97 | 0.834 |
| title · rerank fused 1:1 | 103 | 0.93 | 0.98 | 0.99 | 0.99 | 0.957 |
| title · service | 103 | 0.93 | 0.98 | 0.99 | 0.99 | 0.957 |
| fields·identifiers · keyword | 103 | 0.58 | 0.77 | 0.80 | 0.82 | 0.672 |
| fields·identifiers · vector | 103 | 0.92 | 0.98 | 0.98 | 0.98 | 0.950 |
| fields·identifiers · fused 1:1 | 103 | 0.78 | 0.91 | 0.96 | 0.98 | 0.857 |
| fields·identifiers · rerank fused 1:1 | 103 | 0.95 | 0.98 | 0.99 | 0.99 | 0.968 |
| fields·identifiers · service | 103 | 0.95 | 0.98 | 0.99 | 0.99 | 0.968 |
| fields·Japanese names · keyword | 103 | 0.58 | 0.77 | 0.80 | 0.82 | 0.674 |
| fields·Japanese names · vector | 103 | 0.94 | 0.98 | 0.98 | 0.98 | 0.960 |
| fields·Japanese names · fused 1:1 | 103 | 0.78 | 0.91 | 0.96 | 0.98 | 0.854 |
| fields·Japanese names · rerank fused 1:1 | 103 | 0.96 | 0.98 | 0.99 | 0.99 | 0.973 |
| fields·Japanese names · service | 103 | 0.96 | 0.98 | 0.99 | 0.99 | 0.973 |

## Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.984 |
| title · vector | 43 | 0.88 | 0.98 | 1.00 | 1.00 | 0.928 |
| title · fused 1:1 | 43 | 0.93 | 1.00 | 1.00 | 1.00 | 0.965 |
| title · rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| title · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields·identifiers · vector | 43 | 0.93 | 1.00 | 1.00 | 1.00 | 0.961 |
| fields·identifiers · fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · keyword | 43 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields·Japanese names · vector | 43 | 0.91 | 1.00 | 1.00 | 1.00 | 0.950 |
| fields·Japanese names · fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · rerank fused 1:1 | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 83 | 0.67 | 0.89 | 0.92 | 0.93 | 0.781 |
| title · vector | 83 | 0.87 | 0.98 | 0.98 | 0.99 | 0.921 |
| title · fused 1:1 | 83 | 0.78 | 0.90 | 0.96 | 0.98 | 0.859 |
| title · rerank fused 1:1 | 83 | 0.94 | 1.00 | 1.00 | 1.00 | 0.968 |
| title · service | 83 | 0.94 | 1.00 | 1.00 | 1.00 | 0.968 |
| fields·identifiers · keyword | 83 | 0.66 | 0.88 | 0.92 | 0.93 | 0.768 |
| fields·identifiers · vector | 83 | 0.94 | 0.99 | 0.99 | 0.99 | 0.964 |
| fields·identifiers · fused 1:1 | 83 | 0.81 | 0.94 | 0.98 | 0.99 | 0.883 |
| fields·identifiers · rerank fused 1:1 | 83 | 0.96 | 1.00 | 1.00 | 1.00 | 0.982 |
| fields·identifiers · service | 83 | 0.96 | 1.00 | 1.00 | 1.00 | 0.982 |
| fields·Japanese names · keyword | 83 | 0.66 | 0.88 | 0.92 | 0.93 | 0.770 |
| fields·Japanese names · vector | 83 | 0.95 | 0.99 | 0.99 | 0.99 | 0.970 |
| fields·Japanese names · fused 1:1 | 83 | 0.80 | 0.94 | 0.98 | 0.99 | 0.874 |
| fields·Japanese names · rerank fused 1:1 | 83 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| fields·Japanese names · service | 83 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |

## Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.274 |
| title · vector | 20 | 0.85 | 0.90 | 0.90 | 0.95 | 0.880 |
| title · fused 1:1 | 20 | 0.65 | 0.75 | 0.85 | 0.95 | 0.727 |
| title · rerank fused 1:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| title · service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields·identifiers · keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.275 |
| fields·identifiers · vector | 20 | 0.85 | 0.95 | 0.95 | 0.95 | 0.892 |
| fields·identifiers · fused 1:1 | 20 | 0.65 | 0.80 | 0.90 | 0.95 | 0.753 |
| fields·identifiers · rerank fused 1:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields·identifiers · service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields·Japanese names · keyword | 20 | 0.25 | 0.30 | 0.30 | 0.35 | 0.274 |
| fields·Japanese names · vector | 20 | 0.90 | 0.95 | 0.95 | 0.95 | 0.917 |
| fields·Japanese names · fused 1:1 | 20 | 0.70 | 0.80 | 0.90 | 0.95 | 0.770 |
| fields·Japanese names · rerank fused 1:1 | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| fields·Japanese names · service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |

## Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| title · vector | 12 | 0.75 | 0.83 | 0.83 | 0.83 | 0.792 |
| title · fused 1:1 | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| title · rerank fused 1:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| title · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields·identifiers · keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| fields·identifiers · vector | 12 | 0.75 | 0.83 | 0.83 | 0.83 | 0.792 |
| fields·identifiers · fused 1:1 | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| fields·identifiers · rerank fused 1:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields·identifiers · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields·Japanese names · keyword | 12 | 0.58 | 0.83 | 0.83 | 0.83 | 0.694 |
| fields·Japanese names · vector | 12 | 0.75 | 0.83 | 0.83 | 0.83 | 0.792 |
| fields·Japanese names · fused 1:1 | 12 | 0.67 | 0.92 | 0.92 | 0.92 | 0.792 |
| fields·Japanese names · rerank fused 1:1 | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| fields·Japanese names · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |

## Document level, entity questions (named only by a field value)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| title · vector | 19 | 0.95 | 1.00 | 1.00 | 1.00 | 0.965 |
| title · fused 1:1 | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| title · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| title · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · keyword | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.939 |
| fields·identifiers · vector | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · keyword | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.939 |
| fields·Japanese names · vector | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Passage level, entity questions

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| title · keyword | 19 | 0.47 | 0.89 | 0.89 | 0.89 | 0.675 |
| title · vector | 19 | 0.79 | 0.95 | 0.95 | 1.00 | 0.867 |
| title · fused 1:1 | 19 | 0.63 | 0.89 | 0.95 | 1.00 | 0.785 |
| title · rerank fused 1:1 | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| title · service | 19 | 0.89 | 1.00 | 1.00 | 1.00 | 0.947 |
| fields·identifiers · keyword | 19 | 0.47 | 0.89 | 0.89 | 0.89 | 0.658 |
| fields·identifiers · vector | 19 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| fields·identifiers · fused 1:1 | 19 | 0.79 | 0.95 | 1.00 | 1.00 | 0.879 |
| fields·identifiers · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·identifiers · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · keyword | 19 | 0.47 | 0.89 | 0.89 | 0.89 | 0.667 |
| fields·Japanese names · vector | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · fused 1:1 | 19 | 0.74 | 0.95 | 1.00 | 1.00 | 0.853 |
| fields·Japanese names · rerank fused 1:1 | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| fields·Japanese names · service | 19 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Misses of `title · rerank fused 1:1` (answer not first)

| Query | Expected | title · keyword | title · vector | title · fused 1:1 | title · rerank fused 1:1 | title · service | fields·identifiers · keyword | fields·identifiers · vector | fields·identifiers · fused 1:1 | fields·identifiers · rerank fused 1:1 | fields·identifiers · service | fields·Japanese names · keyword | fields·Japanese names · vector | fields·Japanese names · fused 1:1 | fields·Japanese names · rerank fused 1:1 | fields·Japanese names · service |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 5 | 2 | 4 | 3 | 3 | 5 | 1 | 2 | 1 | 1 | 5 | 1 | 2 | 1 | 1 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | - | 10 | 10 | 4 | 4 | - | 2 | 2 | 4 | 4 | - | 1 | 1 | 4 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 1 | 1 | 2 | 2 | 2 | 1 | 1 | 2 | 2 | 2 | 1 | 2 | 2 | 2 |
| 損害賠償の上限はいくら | outsourcing-basic-terms / 第10条 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |
| 山田太郎さんが借りている部屋の敷金はいくらですか | lease-shibakoen / 第3条 | 2 | 1 | 1 | 2 | 2 | 2 | 1 | 2 | 1 | 1 | 2 | 1 | 2 | 1 | 1 |
| サンプル商事との保守委託契約で業務を下請けに出せるか | outsourcing-system / 第4条 | 3 | 7 | 4 | 2 | 2 | 3 | 2 | 2 | 1 | 1 | 2 | 1 | 2 | 1 | 1 |

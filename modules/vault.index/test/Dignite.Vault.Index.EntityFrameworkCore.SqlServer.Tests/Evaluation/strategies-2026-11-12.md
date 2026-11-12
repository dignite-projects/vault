# Vault Index chunking strategies

- Evaluation set version 2: 21 documents, 108 cases (12 answered by a table)
- Each strategy is the layer default profile; the corpus is indexed again for each. `fused` is RRF 1:1 of the two legs; `service` is IIndexSearchAppService as configured
- `heading 400`: 112 chunks
- `heading 400, HTML tables dropped`: 110 chunks
- `heading 200`: 115 chunks
- `heading 800`: 112 chunks
- `token 400/40`: 25 chunks
- Rerank model BAAI/bge-reranker-v2-m3 in `service`
- Run 2026-11-12 12:59 UTC

## Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · fused | 108 | 0.92 | 0.97 | 0.98 | 0.99 | 0.947 |
| heading 400 · service | 108 | 0.97 | 0.99 | 0.99 | 0.99 | 0.981 |
| heading 400, HTML tables dropped · fused | 108 | 0.88 | 0.93 | 0.95 | 0.97 | 0.909 |
| heading 400, HTML tables dropped · service | 108 | 0.91 | 0.95 | 0.96 | 0.98 | 0.936 |
| heading 200 · fused | 108 | 0.93 | 0.97 | 0.98 | 0.99 | 0.949 |
| heading 200 · service | 108 | 0.98 | 1.00 | 1.00 | 1.00 | 0.991 |
| heading 800 · fused | 108 | 0.92 | 0.97 | 0.98 | 0.99 | 0.946 |
| heading 800 · service | 108 | 0.97 | 0.99 | 0.99 | 0.99 | 0.981 |
| token 400/40 · fused | 108 | 0.92 | 0.96 | 0.99 | 1.00 | 0.945 |
| token 400/40 · service | 108 | 0.99 | 1.00 | 1.00 | 1.00 | 0.995 |

## Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · fused | 69 | 0.75 | 0.87 | 0.96 | 0.97 | 0.828 |
| heading 400 · service | 69 | 0.94 | 0.97 | 0.99 | 0.99 | 0.958 |
| heading 400, HTML tables dropped · fused | 69 | 0.67 | 0.75 | 0.87 | 0.88 | 0.735 |
| heading 400, HTML tables dropped · service | 69 | 0.84 | 0.90 | 0.91 | 0.91 | 0.871 |
| heading 200 · fused | 69 | 0.77 | 0.88 | 0.96 | 0.97 | 0.834 |
| heading 200 · service | 69 | 0.96 | 0.99 | 1.00 | 1.00 | 0.972 |
| heading 800 · fused | 69 | 0.78 | 0.88 | 0.96 | 0.97 | 0.844 |
| heading 800 · service | 69 | 0.94 | 0.97 | 0.99 | 0.99 | 0.958 |
| token 400/40 · fused | 69 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |
| token 400/40 · service | 69 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

## Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · fused | 39 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| heading 400 · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| heading 400, HTML tables dropped · fused | 39 | 0.95 | 1.00 | 1.00 | 1.00 | 0.970 |
| heading 400, HTML tables dropped · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| heading 200 · fused | 39 | 0.95 | 1.00 | 1.00 | 1.00 | 0.970 |
| heading 200 · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| heading 800 · fused | 39 | 0.92 | 1.00 | 1.00 | 1.00 | 0.957 |
| heading 800 · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| token 400/40 · fused | 39 | 0.90 | 0.97 | 1.00 | 1.00 | 0.934 |
| token 400/40 · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · fused | 53 | 0.79 | 0.91 | 0.96 | 0.98 | 0.861 |
| heading 400 · service | 53 | 0.96 | 1.00 | 1.00 | 1.00 | 0.978 |
| heading 400, HTML tables dropped · fused | 53 | 0.74 | 0.81 | 0.89 | 0.91 | 0.790 |
| heading 400, HTML tables dropped · service | 53 | 0.89 | 0.94 | 0.94 | 0.94 | 0.912 |
| heading 200 · fused | 53 | 0.81 | 0.92 | 0.96 | 0.98 | 0.869 |
| heading 200 · service | 53 | 0.96 | 1.00 | 1.00 | 1.00 | 0.978 |
| heading 800 · fused | 53 | 0.83 | 0.92 | 0.96 | 0.98 | 0.882 |
| heading 800 · service | 53 | 0.96 | 1.00 | 1.00 | 1.00 | 0.978 |
| token 400/40 · fused | 53 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |
| token 400/40 · service | 53 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

## Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · fused | 16 | 0.63 | 0.75 | 0.94 | 0.94 | 0.718 |
| heading 400 · service | 16 | 0.88 | 0.88 | 0.94 | 0.94 | 0.891 |
| heading 400, HTML tables dropped · fused | 16 | 0.44 | 0.56 | 0.81 | 0.81 | 0.553 |
| heading 400, HTML tables dropped · service | 16 | 0.69 | 0.75 | 0.81 | 0.81 | 0.734 |
| heading 200 · fused | 16 | 0.63 | 0.75 | 0.94 | 0.94 | 0.718 |
| heading 200 · service | 16 | 0.94 | 0.94 | 1.00 | 1.00 | 0.953 |
| heading 800 · fused | 16 | 0.63 | 0.75 | 0.94 | 0.94 | 0.718 |
| heading 800 · service | 16 | 0.88 | 0.88 | 0.94 | 0.94 | 0.891 |
| token 400/40 · fused | 16 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |
| token 400/40 · service | 16 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

## Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · fused | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| heading 400 · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| heading 400, HTML tables dropped · fused | 12 | 0.25 | 0.25 | 0.42 | 0.42 | 0.283 |
| heading 400, HTML tables dropped · service | 12 | 0.25 | 0.50 | 0.50 | 0.50 | 0.375 |
| heading 200 · fused | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| heading 200 · service | 12 | 0.92 | 1.00 | 1.00 | 1.00 | 0.958 |
| heading 800 · fused | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| heading 800 · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| token 400/40 · fused | 12 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |
| token 400/40 · service | 12 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

## Misses of `heading 400 · service` (answer not first)

| Query | Expected | heading 400 · fused | heading 400 · service | heading 400, HTML tables dropped · fused | heading 400, HTML tables dropped · service | heading 200 · fused | heading 200 · service | heading 800 · fused | heading 800 · service | token 400/40 · fused | token 400/40 · service |
|---|---|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 4 | 3 | 4 | 3 | 3 | 3 | 2 | 3 | - | - |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | 5 | 4 | 5 | 4 | 5 | 4 | 5 | 4 | - | - |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | - | - | - | - | 1 | - | - | - | - |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 2 | - | 2 | 1 | 2 | 1 | 2 | - | - |

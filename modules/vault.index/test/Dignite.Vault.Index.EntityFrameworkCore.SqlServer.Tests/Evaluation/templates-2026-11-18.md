# Vault Index chunking templates

- Evaluation set version 5: 23 documents, 127 cases (12 answered by a table)
- Each template is indexed from scratch; `service` is IIndexSearchAppService as configured. `by type`: invoice Whole 1500, quote Whole 1500, expense-claim Whole 1500, price-list TableRow 400, report TableRow 400, the rest heading 400
- `heading 400`: 143 chunks, a top-3 hit 113 characters on average
- `whole 1500`: 23 chunks, a top-3 hit 823 characters on average
- `table rows 400`: 174 chunks, a top-3 hit 93 characters on average
- `by type`: 145 chunks, a top-3 hit 147 characters on average
- Rerank model BAAI/bge-reranker-v2-m3 in `service`
- Run 2026-11-18 01:33 UTC

## Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · service | 127 | 0.97 | 0.99 | 0.99 | 0.99 | 0.980 |
| whole 1500 · service | 127 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| table rows 400 · service | 127 | 0.98 | 1.00 | 1.00 | 1.00 | 0.988 |
| by type · service | 127 | 0.98 | 1.00 | 1.00 | 1.00 | 0.992 |

## Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · service | 84 | 0.94 | 0.98 | 0.99 | 0.99 | 0.959 |
| whole 1500 · service | 84 | 0.96 | 1.00 | 1.00 | 1.00 | 0.982 |
| table rows 400 · service | 84 | 0.95 | 0.99 | 1.00 | 1.00 | 0.971 |
| by type · service | 84 | 0.96 | 0.99 | 1.00 | 1.00 | 0.979 |

## Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| whole 1500 · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| table rows 400 · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| by type · service | 43 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · service | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| whole 1500 · service | 64 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |
| table rows 400 · service | 64 | 0.95 | 1.00 | 1.00 | 1.00 | 0.974 |
| by type · service | 64 | 0.97 | 1.00 | 1.00 | 1.00 | 0.984 |

## Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · service | 20 | 0.90 | 0.90 | 0.95 | 0.95 | 0.913 |
| whole 1500 · service | 20 | 0.95 | 1.00 | 1.00 | 1.00 | 0.975 |
| table rows 400 · service | 20 | 0.95 | 0.95 | 1.00 | 1.00 | 0.963 |
| by type · service | 20 | 0.95 | 0.95 | 1.00 | 1.00 | 0.963 |

## Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| heading 400 · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| whole 1500 · service | 12 | 0.83 | 1.00 | 1.00 | 1.00 | 0.917 |
| table rows 400 · service | 12 | 0.92 | 1.00 | 1.00 | 1.00 | 0.958 |
| by type · service | 12 | 0.92 | 1.00 | 1.00 | 1.00 | 0.958 |

## Misses of `heading 400 · service` (answer not first)

| Query | Expected | heading 400 · service | whole 1500 · service | table rows 400 · service | by type · service |
|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 3 | 1 | 3 | 1 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | 4 | 1 | 4 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | 2 | 1 | 1 |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 2 | 2 | 2 |
| 損害賠償の上限はいくら | outsourcing-basic-terms / 第10条 | 2 | 2 | 2 | 2 |

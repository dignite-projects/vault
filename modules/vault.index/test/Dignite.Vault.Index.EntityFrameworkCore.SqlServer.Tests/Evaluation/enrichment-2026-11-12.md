# Vault Index chunk enrichment

- Evaluation set version 2: 21 documents, 108 cases
- Heading chunking at 400 tokens; enrichment model Qwen/Qwen3-30B-A3B-Instruct-2507, 10 chunks per call. `fused` is RRF 1:1 of the two legs; `service` is IIndexSearchAppService as configured
- `no enrichment`: 112 chunks
- `keywords`: 112 chunks, 22 model calls, per document 643 input and 643 output tokens; 1 runs with enrichment failures
- `questions`: 112 chunks, 22 model calls, per document 625 input and 713 output tokens; 0 runs with enrichment failures
- `keywords + questions`: 112 chunks, 22 model calls, per document 665 input and 1020 output tokens; 0 runs with enrichment failures
- Rerank model BAAI/bge-reranker-v2-m3 in `service`
- Run 2026-11-12 15:06 UTC

## Document level, all cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| no enrichment · fused | 108 | 0.92 | 0.97 | 0.98 | 0.99 | 0.946 |
| no enrichment · service | 108 | 0.97 | 0.99 | 0.99 | 0.99 | 0.981 |
| keywords · fused | 108 | 0.91 | 0.97 | 0.99 | 0.99 | 0.944 |
| keywords · service | 108 | 0.97 | 0.99 | 0.99 | 0.99 | 0.981 |
| questions · fused | 108 | 0.93 | 0.99 | 0.99 | 0.99 | 0.955 |
| questions · service | 108 | 0.97 | 0.99 | 0.99 | 0.99 | 0.981 |
| keywords + questions · fused | 108 | 0.93 | 0.99 | 0.99 | 0.99 | 0.957 |
| keywords + questions · service | 108 | 0.97 | 0.99 | 0.99 | 0.99 | 0.981 |

## Passage level (written questions)

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| no enrichment · fused | 69 | 0.77 | 0.88 | 0.96 | 0.97 | 0.836 |
| no enrichment · service | 69 | 0.94 | 0.97 | 0.99 | 0.99 | 0.958 |
| keywords · fused | 69 | 0.78 | 0.87 | 0.96 | 0.97 | 0.845 |
| keywords · service | 69 | 0.94 | 0.97 | 0.99 | 0.99 | 0.958 |
| questions · fused | 69 | 0.81 | 0.90 | 0.97 | 0.97 | 0.868 |
| questions · service | 69 | 0.94 | 0.97 | 0.99 | 0.99 | 0.958 |
| keywords + questions · fused | 69 | 0.80 | 0.94 | 0.97 | 0.99 | 0.866 |
| keywords + questions · service | 69 | 0.94 | 0.97 | 0.99 | 0.99 | 0.958 |

## Document level, field cases

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| no enrichment · fused | 39 | 0.92 | 1.00 | 1.00 | 1.00 | 0.957 |
| no enrichment · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| keywords · fused | 39 | 0.92 | 1.00 | 1.00 | 1.00 | 0.962 |
| keywords · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| questions · fused | 39 | 0.97 | 1.00 | 1.00 | 1.00 | 0.987 |
| questions · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| keywords + questions · fused | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |
| keywords + questions · service | 39 | 1.00 | 1.00 | 1.00 | 1.00 | 1.000 |

## Passage level, written questions in Japanese

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| no enrichment · fused | 53 | 0.81 | 0.92 | 0.96 | 0.98 | 0.872 |
| no enrichment · service | 53 | 0.96 | 1.00 | 1.00 | 1.00 | 0.978 |
| keywords · fused | 53 | 0.81 | 0.91 | 0.96 | 0.98 | 0.871 |
| keywords · service | 53 | 0.96 | 1.00 | 1.00 | 1.00 | 0.978 |
| questions · fused | 53 | 0.83 | 0.91 | 0.98 | 0.98 | 0.883 |
| questions · service | 53 | 0.96 | 1.00 | 1.00 | 1.00 | 0.978 |
| keywords + questions · fused | 53 | 0.81 | 0.96 | 0.98 | 1.00 | 0.884 |
| keywords + questions · service | 53 | 0.96 | 1.00 | 1.00 | 1.00 | 0.978 |

## Passage level, written questions in Chinese and English

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| no enrichment · fused | 16 | 0.63 | 0.75 | 0.94 | 0.94 | 0.718 |
| no enrichment · service | 16 | 0.88 | 0.88 | 0.94 | 0.94 | 0.891 |
| keywords · fused | 16 | 0.69 | 0.75 | 0.94 | 0.94 | 0.759 |
| keywords · service | 16 | 0.88 | 0.88 | 0.94 | 0.94 | 0.891 |
| questions · fused | 16 | 0.75 | 0.88 | 0.94 | 0.94 | 0.818 |
| questions · service | 16 | 0.88 | 0.88 | 0.94 | 0.94 | 0.891 |
| keywords + questions · fused | 16 | 0.75 | 0.88 | 0.94 | 0.94 | 0.807 |
| keywords + questions · service | 16 | 0.88 | 0.88 | 0.94 | 0.94 | 0.891 |

## Passage level, questions answered by a table

| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |
|---|---|---|---|---|---|---|
| no enrichment · fused | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| no enrichment · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| keywords · fused | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| keywords · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| questions · fused | 12 | 0.75 | 0.92 | 0.92 | 0.92 | 0.833 |
| questions · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |
| keywords + questions · fused | 12 | 0.67 | 0.92 | 0.92 | 0.92 | 0.778 |
| keywords + questions · service | 12 | 0.83 | 0.92 | 0.92 | 0.92 | 0.875 |

## Misses of `no enrichment · service` (answer not first)

| Query | Expected | no enrichment · fused | no enrichment · service | keywords · fused | keywords · service | questions · fused | questions · service | keywords + questions · fused | keywords + questions · service |
|---|---|---|---|---|---|---|---|---|---|
| サンプル商事に送った請求書の振込先口座 | invoice-1112 / お振込先 | 3 | 3 | 4 | 3 | 3 | 3 | 4 | 3 |
| Can the contractor subcontract the system maintenance work? | outsourcing-system / 第4条 | 5 | 4 | 5 | 4 | 3 | 4 | 3 | 4 |
| What is the lead time for clear files? | price-list-stationery / 単価一覧 | - | - | - | - | - | - | - | - |
| 大阪出張の宿泊費はいくら | expense-claim-1103 / 明細 | 2 | 2 | 2 | 2 | 1 | 2 | 2 | 2 |

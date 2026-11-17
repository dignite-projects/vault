# Vault Index abstention

- Evaluation set version 5: 23 documents; 127 answerable cases, 20 unanswerable questions
- Embedding model BAAI/bge-m3; rerank model BAAI/bge-reranker-v2-m3; search as configured (keyword weight 1, vector weight 1)
- Answerable cases whose top result is the expected document: 123 of 127
- Keyword leg found nothing: 9 answerable, 5 unanswerable
- Run 2026-11-17 04:02 UTC

## Reranker score of the top result

| Questions | Min | 5% | Median | 95% | Max |
| --- | ---: | ---: | ---: | ---: | ---: |
| answerable | 0.001 | 0.079 | 0.978 | 0.999 | 1.000 |
| answerable, top correct | 0.001 | 0.083 | 0.979 | 0.999 | 1.000 |
| unanswerable | 0.000 | 0.000 | 0.006 | 0.143 | 0.894 |

| Threshold | Answerable kept | Correct top kept | Unanswerable turned away |
| ---: | ---: | ---: | ---: |
| 0.005 | 123 / 127 | 120 / 123 | 10 / 20 |
| 0.010 | 122 / 127 | 119 / 123 | 12 / 20 |
| 0.020 | 121 / 127 | 118 / 123 | 13 / 20 |
| 0.050 | 121 / 127 | 118 / 123 | 17 / 20 |
| 0.100 | 119 / 127 | 116 / 123 | 18 / 20 |
| 0.200 | 113 / 127 | 111 / 123 | 19 / 20 |
| 0.300 | 112 / 127 | 110 / 123 | 19 / 20 |
| 0.500 | 108 / 127 | 106 / 123 | 19 / 20 |

## Cosine similarity of the vector leg's top result

| Questions | Min | 5% | Median | 95% | Max |
| --- | ---: | ---: | ---: | ---: | ---: |
| answerable | 0.484 | 0.542 | 0.682 | 0.772 | 0.816 |
| answerable, top correct | 0.484 | 0.542 | 0.683 | 0.772 | 0.816 |
| unanswerable | 0.401 | 0.452 | 0.542 | 0.608 | 0.796 |

| Threshold | Answerable kept | Correct top kept | Unanswerable turned away |
| ---: | ---: | ---: | ---: |
| 0.300 | 127 / 127 | 123 / 123 | 0 / 20 |
| 0.350 | 127 / 127 | 123 / 123 | 0 / 20 |
| 0.400 | 127 / 127 | 123 / 123 | 0 / 20 |
| 0.450 | 127 / 127 | 123 / 123 | 1 / 20 |
| 0.500 | 125 / 127 | 121 / 123 | 6 / 20 |
| 0.550 | 120 / 127 | 116 / 123 | 12 / 20 |
| 0.600 | 107 / 127 | 104 / 123 | 18 / 20 |

## Unanswerable questions

| Question | Rerank | Vector | Keyword |
| --- | ---: | ---: | --- |
| 名古屋営業所の事務所の月額賃料はいくらですか | 0.143 | 0.608 | found |
| 社用車を私用で使ってもよいですか | 0.000 | 0.479 | found |
| 育児休業は最長でどのくらい取得できますか | 0.028 | 0.555 | found |
| 請求書 INV-2026-2001 の支払期限はいつですか | 0.894 | 0.796 | found |
| 社員食堂の営業時間は何時から何時までですか | 0.047 | 0.599 | found |
| 第4四半期の売上目標はいくらですか | 0.010 | 0.542 | found |
| 健康保険組合の問い合わせ先の電話番号は | 0.002 | 0.534 | found |
| 新入社員研修はいつ実施されますか | 0.034 | 0.545 | found |
| 会議室の予約はどのシステムで行いますか | 0.000 | 0.474 | found |
| 在宅勤務の通信費手当は月いくら支給されますか | 0.006 | 0.542 | found |
| インフルエンザ予防接種の費用は会社が負担しますか | 0.002 | 0.459 | found |
| 福利厚生のスポーツジム利用補助はありますか | 0.000 | 0.452 | found |
| 社内の Wi-Fi のパスワードは何ですか | 0.009 | 0.562 | found |
| 公司年会在什么时候举行？ | 0.050 | 0.550 | none |
| 员工停车场每月收费多少？ | 0.001 | 0.541 | found |
| 出差时可以乘坐商务舱吗？ | 0.000 | 0.484 | none |
| What is the dress code for client meetings? | 0.000 | 0.503 | none |
| How do I reset my email password? | 0.000 | 0.573 | none |
| Who is the data protection officer? | 0.001 | 0.401 | none |
| What is the warranty period of the X-500 copier? | 0.029 | 0.571 | found |

## Answerable cases with the lowest reranker score

| Question | Rerank | Vector | Top correct |
| --- | ---: | ---: | --- |
| Are pets allowed in the Fukuoka apartment? | 0.001 | 0.510 | yes |
| What is the lead time for clear files? | 0.002 | 0.559 | no |
| 在宅勤務中にカフェの Wi-Fi を使うときに守ること | 0.003 | 0.536 | yes |
| What is the monthly rent of the Umeda office? | 0.004 | 0.509 | yes |
| 福岡の部屋でペットを飼ってもよいか | 0.009 | 0.602 | yes |
| 复印机卡纸了应该怎么处理 | 0.010 | 0.638 | yes |
| Which court handles disputes under the master agreement? | 0.079 | 0.564 | yes |
| 検査の期限を過ぎて甲が何も言わなかったらどうなる | 0.083 | 0.507 | yes |
| Where may employees smoke? | 0.117 | 0.662 | yes |
| 密码策略有什么变化 | 0.118 | 0.641 | yes |

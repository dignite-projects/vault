# Vault Index outline navigation

- Evaluation set version 3: 23 documents; 84 written questions whose answer is under a heading
- Heading chunking at 400 tokens; chat model Qwen/Qwen3-30B-A3B-Instruct-2507 compiles the summaries and navigates
- The navigator gets the question and the expected document's outline and picks one section. `precise`: the pick's heading path contains the expected heading. `covers`: the pick is that section, inside it or above it. `chunks`: the chunks a read of the pick returns, on average
- Compiling: 23 documents, 74 model calls, per document 1746 input and 458 output tokens; 1 with a note
- Run 2026-11-13 02:47 UTC

| Questions | Outline | Cases | Precise | Covers | Chunks |
| --- | --- | ---: | ---: | ---: | ---: |
| all | titles | 84 | 76% | 76% | 1.0 |
| all | titles + summaries | 84 | 99% | 99% | 1.0 |
| captioned headings | titles | 69 | 90% | 90% | 1.0 |
| captioned headings | titles + summaries | 69 | 100% | 100% | 1.0 |
| uncaptioned headings (v3) | titles | 15 | 13% | 13% | 1.0 |
| uncaptioned headings (v3) | titles + summaries | 15 | 93% | 93% | 0.9 |
| Japanese | titles | 64 | 78% | 78% | 1.0 |
| Japanese | titles + summaries | 64 | 98% | 98% | 1.0 |
| Chinese and English | titles | 20 | 70% | 70% | 1.0 |
| Chinese and English | titles + summaries | 20 | 100% | 100% | 1.0 |

1 of 168 answers named no section of the outline.

Missed with titles only:

- `manual-printer` 紙詰まり: コピー機に E-12 が表示された → `1.4` 4. エラーコード一覧
- `manual-vpn` トラブルシューティング: ホテルの無線 LAN から社内ネットワークにつながらない → `1.3` 3. 接続方法
- `quote-office-move` 見積内容: サーバーラックの移設にいくらかかるか → `1.3` 合計
- `invoice-1112` お振込先: When is payment due for invoice INV-2026-1112? → `1.2` 合計
- `price-list-stationery` 単価一覧: ファイルボックスの納期は何営業日 → `1.2` 発注条件
- `price-list-stationery` 単価一覧: What is the lead time for clear files? → `1.2` 発注条件
- `sales-report-q3` 製品別受注: 保守サービスの解約件数 → `1.1` 支店別売上
- `outsourcing-basic-terms` 第5条: 再委託するには何が必要か → `1.14` 第14条
- `outsourcing-basic-terms` 第8条: 成果物の著作権はいつ甲に移るか → `1.15` 第15条
- `outsourcing-basic-terms` 第3条: 検査の期限を過ぎて甲が何も言わなかったらどうなる → `1.14` 第14条
- `outsourcing-basic-terms` 第10条: 損害賠償の上限はいくら → `1.16` 第16条
- `outsourcing-basic-terms` 第13条: 基本契約はいつまで有効で、自動更新されるか → `1.14` 第14条
- `outsourcing-basic-terms` 第7条: 個人情報が漏れたときに乙がすべきこと → `1.8` 第8条
- `outsourcing-basic-terms` 第2条: 个别合同什么时候成立 → `1.3` 第3条
- `outsourcing-basic-terms` 第15条: Which court handles disputes under the master agreement? → `1.16` 第16条
- `safety-rules` 第6条: 高所作業で必要な安全対策は → `1.2.4` 第7条
- `safety-rules` 第8条: 深夜業の従業員の健康診断の頻度は → `1.3.3` 第10条
- `safety-rules` 第4条: 安全衛生委員会はどのくらいの頻度で開かれるか → `1.3.4` 第11条
- `safety-rules` 第9条: 长时间加班的员工可以申请什么 → `1.4.3` 第14条
- `safety-rules` 第11条: Where may employees smoke? → `1.3.3` 第10条

Missed with summaries:

- `outsourcing-basic-terms` 第8条: 成果物の著作権はいつ甲に移るか → `unreadable: {  				    				    				    				    				    				    				    	` 

Sample (`outsourcing-basic-terms`):

- Document: この文書は、株式会社ミナト精機と株式会社ディグナイトの間で締結されるシステム開発の業務委託基本契約書である。契約の対象はシステム開発業務であり、具体的な金額、期間、条件などは本契約書に別途定める。双方の権利義務、情報保護、知的財産権の帰属などが定められている。
- 1 業務委託基本契約書: 株式会社ミナト精機と株式会社ディグナイトのシステム開発業務委託基本契約。
- 1.1 第1条: 本契約はシステム開発業務に関する基本事項を定める。
- 1.2 第2条: 本業務の内容・納期・委託料は個別契約で定め、甲の注文書と乙の注文請書で成立。個別契約が優先。
- 1.3 第3条: 乙は個別契約納期までに成果物を納入。甲は10営業日以内に検査し、異議無くれば合格とみなす。
- 1.4 第4条: 甲は検収翌月末までに委託料を乙指定口座に振込。振込手数料は甲負担。
- 1.5 第5条: 乙は甲書面承諾無しでは再委託不可。再委託先には同等義務を負わせ、責任を甲に対し負う。

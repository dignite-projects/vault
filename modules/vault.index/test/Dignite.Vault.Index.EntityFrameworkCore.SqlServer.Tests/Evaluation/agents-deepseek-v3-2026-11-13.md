# Vault Index cross-document questions

- Evaluation set version 4: 23 documents; 13 questions only several documents answer, each asked 2 times per toolset
- Agent: deepseek-ai/DeepSeek-V3 with tool calls (at most 12 rounds), the MCP tools as published; summaries compiled by Qwen/Qwen3-30B-A3B-Instruct-2507. `before P2`: search, outline (titles), read, type list. `P2`: the same plus the catalog, after compiling section and document summaries (heading chunking at 400 tokens)
- An answer is scored by the facts it contains (any one spelling of each, after NFKC, without spaces or commas). `complete`: every fact. `calls`: tool calls per answer
- Run 2026-11-13 04:27 UTC

| Toolset | Answers | Complete | Facts | Calls | Input tokens | Output tokens |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| before P2 | 26 | 22 | 56 / 66 | 3.0 | 9586 | 302 |
| P2 | 26 | 20 | 56 / 66 | 3.5 | 14207 | 332 |

Tool calls with the P2 toolset: vault_index_search 36, vault_index_outline 21, vault_extract_list_document_types 20, vault_index_catalog 11, vault_index_read 4

| # | Question | Before P2 (facts per run) | P2 (facts per run) |
| ---: | --- | --- | --- |
| 1 | 賃貸借契約のうち、犬や猫などのペットの飼育を禁止しているのはどれですか | 1/1 (2 calls), 1/1 (1 calls) | 1/1 (3 calls), 1/1 (7 calls) |
| 2 | 4 件の賃貸借契約のうち、月額賃料が最も高い物件と最も安い物件はどれで、それぞれいくらですか | 4/4 (2 calls), 4/4 (2 calls) | 4/4 (6 calls), 4/4 (3 calls) |
| 3 | 株式会社ディグナイトが賃借人になっている賃貸借契約はどれで、月額賃料はいくらですか | 2/2 (4 calls), 2/2 (4 calls) | 2/2 (3 calls), 2/2 (4 calls) |
| 4 | 賃借人から中途解約するとき、予告期間が最も長い契約はどれで、何か月前までに予告が必要ですか | 2/2 (2 calls), 2/2 (2 calls) | 2/2 (1 calls), 2/2 (2 calls) |
| 5 | 2026年11月に支払う(口座振替・カード決済・振込期限が11月の)請求書を、日付と金額と一緒に挙げてください | 3/3 (6 calls), 0/3 (3 calls) | 3/3 (3 calls), 2/3 (6 calls) |
| 6 | 再委託や転貸に相手方の書面による承諾が必要と定めている契約をすべて挙げてください | 3/3 (4 calls), 3/3 (3 calls) | 3/3 (4 calls), 3/3 (3 calls) |
| 7 | 損害賠償に上限を設けている契約はどれで、上限はそれぞれどう決まりますか | 2/2 (3 calls), 2/2 (4 calls) | 2/2 (4 calls), 2/2 (4 calls) |
| 8 | 11月の会議で決まった事項のうち、2027年に実施または適用されるものを挙げてください | 3/3 (1 calls), 3/3 (1 calls) | 3/3 (1 calls), 3/3 (1 calls) |
| 9 | 本社移転の見積りの合計金額と、本社移転の時期を承認した会議の日付を教えてください | 2/2 (2 calls), 2/2 (2 calls) | 2/2 (2 calls), 2/2 (2 calls) |
| 10 | 請求書は何通あり、それぞれの発行元と合計金額はいくらですか | 4/4 (2 calls), 1/4 (8 calls) | 2/4 (6 calls), 2/4 (6 calls) |
| 11 | 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? | 0/2 (4 calls), 0/2 (6 calls) | 0/2 (4 calls), 0/2 (5 calls) |
| 12 | 哪些会议已经安排了12月的下一次会议?分别是哪一天? | 2/2 (1 calls), 2/2 (1 calls) | 1/2 (3 calls), 2/2 (1 calls) |
| 13 | Which agreements renew automatically unless a party objects, and how far before expiry must the objection be made for each? | 3/3 (3 calls), 3/3 (4 calls) | 3/3 (4 calls), 3/3 (4 calls) |

Missed facts:

- before P2 · 2026年11月に支払う(口座振替・カード決済・振込期限が11月の)請求書を、日付と金額と一緒に挙げてください · missed 58960, 1234640, 33000 · answer: емойThe tools encountered an issue while trying to retrieve the requested information. It seems there might be a technical problem or no relevant documents matching the criteria.   If you have specific details about the invoices (such as a 
- before P2 · 請求書は何通あり、それぞれの発行元と合計金額はいくらですか · missed 東都電力, みどり印刷, ノーススター · answer: 請求書は以下の4通あり、それぞれの発行元と合計金額は以下の通りです。  1. **請求書 INV-2026-1112**    - 発行元: ディグナイト    - 合計金額: 211,200 円  2. **請求書 MP-0987(名刺印刷)**    - 発行元: ディグナイト    - 合計金額: 33,000 円  3. **電気料金請求書 2026年10月分**    - 発行元: 電気会社    - 合計金額: 58,960 円  4. **請求書 CL-7741
- before P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 公司与以下委托方签订了业务委托合同：  1. **ミナト精機**      合同内容：システム開発業務（系统开发业务）。      委托费：在个别合同中具体规定。  2. **SALES-ONE**      合同内容：販売管理システム「SALES-ONE」的障害対応、定期点検、軽微な改修及問い合わせ対応（销售管理系统“SALES-ONE”的故障应对、定期检查、轻微修改及咨询应对）。      委托费：未在合同中明确，需参考个别合同。  使用的文档： - 業務委託基本契約書(
- before P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 我们公司作为委托方签订了以下业务委托合同：  1. **業務委託基本契約書(ミナト精機)**    - 委托业务内容：系统开发业务（具体的细节在个别合同中规定）。    - 委托费：根据个别合同的规定，委托费在验收后的次月末之前支付。    - [文档内容](document:3269c606-d92c-bf30-7c49-d0d0a95b5997)  2. **システム保守業務委託契約書(SALES-ONE)**    - 委托业务内容：销售管理系统“SALES-ONE”的
- P2 · 2026年11月に支払う(口座振替・カード決済・振込期限が11月の)請求書を、日付と金額と一緒に挙げてください · missed 33000 · answer: 2026年11月に支払う請求書の情報は以下の通りです。すべての情報は請求書の内容に基づいています。  1. **請求書 CL-7741-2210（クラウド利用料）**    - **発行日**: 2026年11月2日    - **金額**: 合計 1,234,640 円 (内訳: 小計 1,122,400 円 + 消費税 112,240 円)    - **支払方法**: クレジットカード決済（カード末尾4421）    - **支払予定日**: 2026年11月25日  
- P2 · 請求書は何通あり、それぞれの発行元と合計金額はいくらですか · missed 東都電力, ノーススター · answer: 請求書は4通あり、それぞれの発行元と合計金額は以下のとおりです：  1. **請求書 CL-7741-2210(クラウド利用料)**      - 発行元: 株式会社ディグナイト      - 合計金額: 1,234,640円    2. **請求書 INV-2026-1112**      - 発行元: 株式会社ディグナイト      - 合計金額: 211,200円    3. **請求書 MP-0987(名刺印刷)**      - 発行元: 株式会社みどり印刷    
- P2 · 請求書は何通あり、それぞれの発行元と合計金額はいくらですか · missed 東都電力, ノーススター · answer: 請求書は4通あり、それぞれの発行元と合計金額は以下の通りです。  1. **請求書 CL-7741-2210(クラウド利用料)**      - 発行元: 株式会社ディグナイト      - 合計金額: 1,234,640円    2. **請求書 INV-2026-1112**      - 発行元: 記載なし      - 合計金額: 211,200円    3. **請求書 MP-0987(名刺印刷)**      - 発行元: 株式会社みどり印刷      - 合計金
- P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 答案为公司的委托方签订了两份业务委托合同：    1. **业务委托基本契约书（ミナト精机）**      - 内容：系统开发业务的基本契约。      - 每月委托费：未在文档中明确说明，需通过个别契约具体约定。    2. **系统维护业务委托契约书（SALES-ONE）**      - 内容：销售管理系统“SALES-ONE”的维护服务（包括故障处理、定期检查等）。      - 每月委托费：月额30万日元（不含税），支付时间为次月末。    使用的文档：   - 《
- P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 公司作为委托方签订的业务委托合同如下：  1. **システム保守業務委託契約書(SALES-ONE)**      委托内容：基幹システムの保守業務（障害対応、定期点検、軽微な改修及び問い合わせ対応）。      每月委托费：月额300,000日元（不含税），支付截止时间为次月末。  2. **業務委託基本契約書(ミナト精機)**      委托内容：システム開発業務。具体金额和支付方式在个别契约中约定，当前文档未明确月额委托费。  使用的文档： - [システム保守業務委託契
- P2 · 哪些会议已经安排了12月的下一次会议?分别是哪一天? · missed 12月6日 · answer: 住宿creening 12月的会议安排如下：  1. **情報セキュリティ委員会**      - 下一次会议日期：2026年12月3日      - 会议时间：15:00    此信息来自《情報セキュリティ委員会 議事録 2026-11-05》文档。其他会议记录中未提及12月的会议安排。

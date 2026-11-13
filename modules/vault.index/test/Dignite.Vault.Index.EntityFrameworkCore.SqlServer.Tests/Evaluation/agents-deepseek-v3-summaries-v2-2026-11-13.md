# Vault Index cross-document questions

- Evaluation set version 4: 23 documents; 13 questions only several documents answer, each asked 2 times per toolset
- Agent: deepseek-ai/DeepSeek-V3 with tool calls (at most 12 rounds), the MCP tools as published; summaries compiled by Qwen/Qwen3-30B-A3B-Instruct-2507. `before P2`: search, outline (titles), read, type list. `P2`: the same plus the catalog, after compiling section and document summaries (heading chunking at 400 tokens)
- An answer is scored by the facts it contains (any one spelling of each, after NFKC, without spaces or commas). `complete`: every fact. `calls`: tool calls per answer
- Run 2026-11-13 06:15 UTC

| Toolset | Answers | Complete | Facts | Calls | Input tokens | Output tokens |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| before P2 | 26 | 18 | 53 / 66 | 3.7 | 11724 | 325 |
| P2 | 26 | 19 | 54 / 66 | 2.7 | 10428 | 295 |

Tool calls with the P2 toolset: vault_extract_list_document_types 21, vault_index_search 20, vault_index_read 14, vault_index_catalog 11, vault_index_outline 5

| # | Question | Before P2 (facts per run) | P2 (facts per run) |
| ---: | --- | --- | --- |
| 1 | 賃貸借契約のうち、犬や猫などのペットの飼育を禁止しているのはどれですか | 0/1 (2 calls), 1/1 (5 calls) | 1/1 (4 calls), 1/1 (2 calls) |
| 2 | 4 件の賃貸借契約のうち、月額賃料が最も高い物件と最も安い物件はどれで、それぞれいくらですか | 4/4 (2 calls), 4/4 (2 calls) | 3/4 (2 calls), 2/4 (6 calls) |
| 3 | 株式会社ディグナイトが賃借人になっている賃貸借契約はどれで、月額賃料はいくらですか | 2/2 (4 calls), 2/2 (5 calls) | 2/2 (2 calls), 2/2 (2 calls) |
| 4 | 賃借人から中途解約するとき、予告期間が最も長い契約はどれで、何か月前までに予告が必要ですか | 2/2 (1 calls), 2/2 (2 calls) | 2/2 (6 calls), 2/2 (6 calls) |
| 5 | 2026年11月に支払う(口座振替・カード決済・振込期限が11月の)請求書を、日付と金額と一緒に挙げてください | 3/3 (10 calls), 1/3 (5 calls) | 3/3 (2 calls), 3/3 (2 calls) |
| 6 | 再委託や転貸に相手方の書面による承諾が必要と定めている契約をすべて挙げてください | 3/3 (4 calls), 2/3 (2 calls) | 3/3 (1 calls), 3/3 (3 calls) |
| 7 | 損害賠償に上限を設けている契約はどれで、上限はそれぞれどう決まりますか | 2/2 (3 calls), 2/2 (4 calls) | 2/2 (2 calls), 2/2 (2 calls) |
| 8 | 11月の会議で決まった事項のうち、2027年に実施または適用されるものを挙げてください | 3/3 (1 calls), 3/3 (1 calls) | 3/3 (1 calls), 3/3 (1 calls) |
| 9 | 本社移転の見積りの合計金額と、本社移転の時期を承認した会議の日付を教えてください | 2/2 (2 calls), 2/2 (2 calls) | 2/2 (2 calls), 2/2 (2 calls) |
| 10 | 請求書は何通あり、それぞれの発行元と合計金額はいくらですか | 1/4 (10 calls), 3/4 (1 calls) | 2/4 (2 calls), 2/4 (2 calls) |
| 11 | 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? | 0/2 (10 calls), 0/2 (6 calls) | 0/2 (2 calls), 0/2 (7 calls) |
| 12 | 哪些会议已经安排了12月的下一次会议?分别是哪一天? | 2/2 (1 calls), 2/2 (1 calls) | 2/2 (2 calls), 2/2 (2 calls) |
| 13 | Which agreements renew automatically unless a party objects, and how far before expiry must the objection be made for each? | 3/3 (6 calls), 2/3 (3 calls) | 3/3 (3 calls), 2/3 (3 calls) |

Missed facts:

- before P2 · 賃貸借契約のうち、犬や猫などのペットの飼育を禁止しているのはどれですか · missed 薬院 · answer:  palm_tree賃貸借契約書の中でペットの飼育を禁止している具体的な条項は見つかりませんでした。検索した内容は契約の目的や期間、敷金に関するものでした。より詳細な検索が必要であれば、他のキーワードや文書を確認してみますか？
- before P2 · 2026年11月に支払う(口座振替・カード決済・振込期限が11月の)請求書を、日付と金額と一緒に挙げてください · missed 1234640, 33000 · answer: 2026年11月に支払う請求書とその支払日と金額は以下の通りです：  1. **請求書 CL-7741-2210 (クラウド利用料)**      - 支払方法: クレジットカード決済      - 支払日: 2026年11月25日      - 金額: (明記されていないため、別途確認が必要です)      - ドキュメント: <請求書 CL-7741-2210(クラウド利用料)>  2. **電気料金請求書 2026年10月分**      - 支払方法: 口座振替   
- before P2 · 再委託や転貸に相手方の書面による承諾が必要と定めている契約をすべて挙げてください · missed 薬院 · answer: 以下の契約書において、再委託や転貸に相手方の書面による承諾が必要と定められています：  1. **業務委託基本契約書(ミナト精機)**      - **第5条**: 乙は、あらかじめ甲の書面による承諾を得た場合に限り、本業務の一部を第三者に再委託することができる。      - **文書ID**: 3269c606-d92c-bf30-7c49-d0d0a95b5997    2. **システム保守業務委託契約書(SALES-ONE)**      - **第4条**: 乙
- before P2 · 請求書は何通あり、それぞれの発行元と合計金額はいくらですか · missed 東都電力, みどり印刷, ノーススター · answer: 以下の4通の請求書があり、それぞれの合計金額は次のとおりです：  1. **請求書 INV-2026-1112**      - 発行元: 株式会社ディグナイト      - 合計金額: 211,200 円    2. **請求書 MP-0987(名刺印刷)**      - 発行元: 株式会社ディグナイト      - 合計金額: 33,000 円    3. **電気料金請求書 2026年10月分**      - 発行元: （電気料金請求書のため記載なし）      -
- before P2 · 請求書は何通あり、それぞれの発行元と合計金額はいくらですか · missed みどり印刷 · answer: 請求書は3通あります。それぞれの発行元と合計金額は以下の通りです。  1. **請求書 INV-2026-1112**      - 発行元: 株式会社サンプル商事      - 合計金額: 211,200 円      - 発行日: 2026年11月12日    2. **電気料金請求書 2026年10月分**      - 発行元: 東都電力株式会社      - 合計金額: 58,960 円      - 請求年月: 2026年10月分    3. **請求書 CL-7
- before P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 我们公司作为委托方签订了以下业务委托合同：  1. **业务委托基本契约书（ミナト精机）**: 委托内容为系统开发业务。具体费用在个别契约中规定（每月委托费未在文档中明确列出）。    - 文档来源：《业务委托基本契约书》第2条和第4条。  2. **系统维护业务委托契约书（SALES-ONE）**: 委托内容为销售管理系统“SALES-ONE”的故障应对、定期检查、轻微修改及咨询对应。    - 文档来源：《系统维护业务委托契约书》第1条。  每月委托费的具体金额未在上述合
- before P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 公司作为委托方签订了以下业务委托合同：  1. **業務委託基本契約書(ミナト精機)**      - 合同内容：系统开发业务的委托。    - 委托费：根据个别合同约定，具体金额未在基本合同中明确。  2. **システム保守業務委託契約書(SALES-ONE)**      - 合同内容：销售管理系统“SALES-ONE”的维护、定期检查、轻微修改及咨询应对。    - 每月委托费：300,000 日元（不含消费税），支付至乙方指定账户。超过每月20小时的修改工作需另行报价
- before P2 · Which agreements renew automatically unless a party objects, and how far before expiry must the objection be made for each? · missed 芝公園 · answer: The following agreements renew automatically unless a party objects:  1. **倉庫賃貸借契約書(札幌 B 棟)** (Lease Agreement for Sapporo Building B):    - Renewal term: 1 year.    - Objection deadline: 1 month before expiry.      (Document: 倉庫賃貸借契約書(札幌 B
- P2 · 4 件の賃貸借契約のうち、月額賃料が最も高い物件と最も安い物件はどれで、それぞれいくらですか · missed 420000 · answer: 4件の賃貸借契約について、以下の通り月額賃料が最も高い物件と最も安い物件を特定しました：  - **最も高い物件**:     **芝公園レジデンス 702号室**     **月額賃料**: 185,000円     （契約書名: 建物賃貸借契約書(芝公園レジデンス 702)）  - **最も安い物件**:     **薬院パークハウス 305号室**     **月額賃料**: 76,000円     （契約書名: 住宅賃貸借契約書(薬院パークハウス 305)）  [参考
- P2 · 4 件の賃貸借契約のうち、月額賃料が最も高い物件と最も安い物件はどれで、それぞれいくらですか · missed 梅田, 420000 · answer: 契約書の詳細を取得できませんでしたが、各契約書の概要から賃料を確認しました。以下はその結果です：  - **最も高い月額賃料**:   - **芝公園レジデンス 702号室**: 月額185,000円    - **最も安い月額賃料**:   - **薬院パークハウス 305号室**: 月額76,000円  参照した契約書: 1. 芝公園レジデンス 702号室 2. 薬院パークハウス 305号室
- P2 · 請求書は何通あり、それぞれの発行元と合計金額はいくらですか · missed 東都電力, ノーススター · answer: 請求書は合計**4通**あります。それぞれの発行元と合計金額は以下の通りです：  1. **請求書 CL-7741-2210(クラウド利用料)**    - 発行元: 株式会社ディグナイト    - 合計金額: 1,234,640円 (うち消費税112,240円)  2. **請求書 INV-2026-1112**    - 発行元: 株式会社ディグナイト    - 合計金額: 211,200円 (うち消費税19,200円)  3. **請求書 MP-0987(名刺印刷)**
- P2 · 請求書は何通あり、それぞれの発行元と合計金額はいくらですか · missed 東都電力, ノーススター · answer: 請求書は4通あります。それぞれの発行元と合計金額は以下の通りです：  1. **請求書 CL-7741-2210(クラウド利用料)**      - 発行元: 株式会社ディグナイト      - 合計金額: 1,234,640円（うち消費税112,240円）      - ドキュメントID: 0bddedc9-2dea-5fce-154d-56a27d0431cd    2. **請求書 INV-2026-1112**      - 発行元: 株式会社ディグナイト      
- P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 我们公司作为委托方签订了以下业务委托合同：  1. **业务委托基本契约书(ミナト精机)**: 这份合同适用于公司委托ミナト精机进行系统开发业务的基本事项。 2. **系统维护业务委托契约书(SALES-ONE)**: 这份合同委托SALES-ONE进行销售管理系统「SALES-ONE」的维护、定期检查、轻微修改和咨询对应。  关于每月委托费，系统维护业务委托契约书(SALES-ONE)中规定，委托费为每月300,000日元（不含消费税），并在次月末前支付。  使用的文档： 
- P2 · 我们公司作为委托方签订了哪些业务委托合同?每月委托费是多少? · missed クリーンワークス, 120000 · answer: 根据搜索结果，我们公司作为委托方签订了以下业务委托合同：  1. **業務委託基本契約書（ミナト精機）**：    - 合同涉及系统开发业务，具体内容、交付期、委托费用等交易条件在个别合同中另行约定。    - 委托费的支付方式为验收后的次月末通过银行转账支付，具体金额在个别合同中约定（参考文档：業務委託基本契約書，第4条）。  2. **システム保守業務委託契約書（SALES-ONE）**：    - 委托内容包括销售管理系统“SALES-ONE”的故障对应、定期检查、轻微
- P2 · Which agreements renew automatically unless a party objects, and how far before expiry must the objection be made for each? · missed ミナト · answer: The following agreements renew automatically unless a party objects, along with the notice period required for objection:  1. **倉庫賃貸借契約書(札幌 B 棟)**      - **Notice Period**: 1 month before expiry.      - **Clause**: "契約期間は 2026年4月1日から 2027年3

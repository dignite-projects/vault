"""Generates the Vault Parse evaluation corpus: every document and its ground truth.

The truth is defined here, as data, and each document is rendered from it, so what the evaluation expects is exactly
what the document contains. Output: <out>/<id>.<ext> plus <out>/<id>.json (the truth). Rendering is deterministic
(fixed seeds), so re-running reproduces the same files.

    python -I etc/eval/parse_corpus.py modules/vault.parse/test/Dignite.Vault.Parse.Evaluation.Tests/Corpus

Needs PyMuPDF (pymupdf), Pillow and openpyxl. DOCX and PPTX are written as minimal OpenXML packages by hand.
"""
import datetime
import io
import json
import os
import random
import re
import sys
import zipfile
from xml.sax.saxutils import escape

import pymupdf
from openpyxl import Workbook
from PIL import Image, ImageFilter


def h(level, text):
    return {"type": "heading", "level": level, "text": text}


def p(text):
    return {"type": "paragraph", "text": text}


def ul(*items):
    return {"type": "list", "items": list(items)}


def table(rows, spans=(), header_rows=1):
    """rows: the grid, with None in cells covered by a span; spans: (row, col, rowspan, colspan)."""
    return {"type": "table", "headerRows": header_rows, "rows": [list(r) for r in rows],
            "spans": [list(s) for s in spans]}


# ----------------------------------------------------------------------------------------------- the documents

INVOICE = dict(
    id="invoice", format="pdf", language="ja",
    elements=[
        h(1, "請求書"),
        p("請求番号：INV-2026-1120"),
        p("発行日：2026年11月20日"),
        p("株式会社サンプル商事 御中"),
        p("下記のとおりご請求申し上げます。"),
        h(2, "ご請求明細"),
        table([
            ["区分", "品名", "数量", "単価", "金額"],
            ["保守", "システム保守（11月分）", "1", "120,000", "120,000"],
            [None, "障害対応（11月12日）", "2", "15,000", "30,000"],
            ["開発", "帳票機能の追加", "1", "280,000", "280,000"],
            ["小計", None, None, None, "430,000"],
            ["消費税（10%）", None, None, None, "43,000"],
            ["合計", None, None, None, "473,000"],
        ], spans=[(1, 0, 2, 1), (4, 0, 1, 4), (5, 0, 1, 4), (6, 0, 1, 4)]),
        h(2, "お振込先"),
        p("みずなみ銀行 本店営業部 普通預金 1234567 カ）デジナイト"),
        p("お支払期日：2026年12月31日"),
    ])

CONTRACT = dict(
    id="contract", format="pdf", language="ja",
    header="業務委託契約書（写）", footer="page", lineHeight=2.3,
    elements=[
        h(1, "業務委託契約書"),
        p("株式会社サンプル商事（以下「甲」という。）と株式会社デジナイト（以下「乙」という。）は、甲が乙に委託する業務について、次のとおり契約を締結する。"),
        h(2, "第1条（目的）"),
        p("甲は、甲の基幹システムの保守業務（以下「本業務」という。）を乙に委託し、乙はこれを受託する。"),
        h(2, "第2条（業務内容）"),
        p("本業務の内容は、別紙仕様書に定めるとおりとする。"),
        p("乙は、本業務の遂行にあたり、善良なる管理者の注意をもってこれを行う。"),
        h(2, "第3条（契約期間）"),
        p("本契約の期間は、2026年12月1日から2027年11月30日までとする。ただし、期間満了の1か月前までにいずれの当事者からも書面による申し出がないときは、同一条件でさらに1年間更新されるものとし、以後も同様とする。"),
        h(2, "第4条（委託料）"),
        p("甲は、乙に対し、本業務の委託料として月額金120,000円（消費税別）を支払う。"),
        p("乙は、毎月末日締めで当月分の委託料を甲に請求し、甲は請求書受領月の翌月末日までに乙の指定する銀行口座に振り込む方法で支払う。振込手数料は甲の負担とする。"),
        h(2, "第5条（再委託）"),
        p("乙は、甲の事前の書面による承諾を得た場合に限り、本業務の一部を第三者に再委託することができる。この場合、乙は再委託先の行為について甲に対し責任を負う。"),
        h(2, "第6条（秘密保持）"),
        p("甲及び乙は、本契約に関して知り得た相手方の技術上又は営業上の情報を、相手方の事前の書面による承諾なく第三者に開示又は漏えいしてはならない。"),
        p("前項の規定は、本契約終了後も3年間有効に存続する。"),
        h(2, "第7条（損害賠償）"),
        p("甲又は乙は、本契約に違反して相手方に損害を与えたときは、その損害を賠償する責任を負う。ただし、乙の賠償額は、損害発生の直前12か月間に甲が乙に支払った委託料の総額を上限とする。"),
        h(2, "第8条（解除）"),
        p("甲又は乙は、相手方が次の各号のいずれかに該当したときは、何らの催告を要せず直ちに本契約を解除することができる。"),
        ul("本契約に定める義務に違反し、相当の期間を定めて催告したにもかかわらず是正しないとき",
           "支払停止又は支払不能の状態に陥ったとき",
           "破産手続開始、民事再生手続開始又は会社更生手続開始の申立てがあったとき"),
        h(2, "第9条（協議）"),
        p("本契約に定めのない事項又は本契約の解釈について疑義が生じた事項については、甲乙誠実に協議のうえ解決する。"),
        h(2, "第10条（管轄裁判所）"),
        p("本契約に関する一切の紛争については、東京地方裁判所を第一審の専属的合意管轄裁判所とする。"),
        p("本契約締結の証として本書2通を作成し、甲乙記名押印のうえ、各1通を保有する。"),
        p("2026年11月20日"),
        p("甲：東京都港区芝公園一丁目1番1号 株式会社サンプル商事 代表取締役 山田 太郎"),
        p("乙：東京都千代田区丸の内二丁目2番2号 株式会社デジナイト 代表取締役 佐藤 花子"),
    ])

REPORT = dict(
    id="report", format="pdf", language="en", layout="two-column",
    # The first two elements span the page; the rest flows through two columns.
    fullWidth=2,
    elements=[
        h(1, "Quarterly Operations Report: Document Intake"),
        p("This report summarizes how incoming documents were processed in the third quarter, where the pipeline lost time, and which changes are planned for the next quarter."),
        h(2, "1. Volume"),
        p("The intake service received 18,420 documents, 12 percent more than in the second quarter. Invoices made up 41 percent of the volume, contracts 23 percent and receipts 19 percent."),
        p("Peak days followed the month-end closing, when the queue grew to four times its average length."),
        h(2, "2. Processing Time"),
        p("The median time from upload to a ready document fell from 96 seconds to 71 seconds. Scanned documents remained the slowest group because every page passes through optical character recognition."),
        p("Field extraction accounted for most of the remaining time on long contracts, where the whole text is sent to the model in one request."),
        h(2, "3. Review Rate"),
        p("Operators reviewed 8.4 percent of the documents, down from 11.2 percent. Most reviews were caused by missing invoice numbers on photographed receipts and by unclear contract parties."),
        p("Reviews took a median of three minutes each, and nine out of ten ended with a correction to a single field."),
        h(2, "4. Next Quarter"),
        p("The team will run independent stages in parallel, retry throttled model calls with backoff, and measure parsing quality on a fixed corpus before every release."),
        p("A layout-aware parser will replace the plain text layer for scanned pages, so that answers can cite the page they come from and reviewers can see the region a value was read from."),
        p("The review queue will be split by document type, so that the operators who know a type best receive its documents first."),
    ])

_PRICE_ROWS = [
    ("保守", [("システム保守（標準）", "月", "120,000"), ("システム保守（拡張）", "月", "180,000"),
              ("障害対応（平日日中）", "時間", "15,000"), ("障害対応（夜間休日）", "時間", "25,000"),
              ("定期点検", "回", "60,000"), ("セキュリティ更新適用", "回", "40,000")]),
    ("開発", [("要件定義", "人日", "90,000"), ("基本設計", "人日", "85,000"), ("詳細設計", "人日", "80,000"),
              ("プログラム開発", "人日", "75,000"), ("結合テスト", "人日", "75,000"), ("帳票作成", "帳票", "50,000"),
              ("データ移行", "式", "300,000"), ("画面追加", "画面", "120,000")]),
    ("運用", [("ジョブ監視", "月", "60,000"), ("バックアップ運用", "月", "40,000"), ("利用者問い合わせ対応", "月", "80,000"),
              ("アカウント管理", "月", "30,000"), ("月次報告書作成", "月", "25,000"), ("性能分析", "回", "70,000"),
              ("容量計画", "回", "50,000"), ("ログ分析", "回", "45,000")]),
    ("教育", [("管理者研修", "回", "150,000"), ("利用者研修", "回", "100,000"), ("操作マニュアル作成", "式", "200,000"),
              ("研修動画作成", "本", "80,000"), ("eラーニング設定", "式", "120,000"), ("フォローアップ研修", "回", "60,000")]),
]


def _price_table():
    rows, spans = [["区分", "サービス", "単位", "価格"]], []
    for category, items in _PRICE_ROWS:
        spans.append((len(rows), 0, len(items), 1))
        for i, (service, unit, price) in enumerate(items):
            rows.append([category if i == 0 else None, service, unit, price])
    return table(rows, spans)


PRICELIST = dict(
    id="pricelist", format="pdf", language="ja", cellPadding=7,
    elements=[
        h(1, "サービス価格表"),
        p("2026年12月1日改定。価格はすべて税抜です。"),
        _price_table(),
        p("表示価格は標準的な作業量に基づく目安です。個別のお見積りは営業担当までご依頼ください。"),
    ])

CONTRACT_SCAN = dict(id="contract-scan", format="pdf-scan", source="contract", language="ja", ocr=True)

RECEIPT = dict(
    id="receipt", format="image", language="ja", ocr=True,
    elements=[
        p("カフェこもれび 芝公園店"),
        p("東京都港区芝公園1-2-3"),
        p("TEL 03-1234-5678"),
        p("2026年11月14日(土) 12:34"),
        p("ブレンドコーヒー ¥480"),
        p("チーズケーキ ¥520"),
        p("サンドイッチ ¥680"),
        p("小計 ¥1,680"),
        p("消費税 ¥168"),
        p("合計 ¥1,848"),
        p("お預り ¥2,000"),
        p("お釣り ¥152"),
        p("ありがとうございました"),
    ])

MEMO = dict(
    id="memo", format="docx", language="ja",
    elements=[
        h(1, "社内メモ：文書取込の運用変更"),
        p("宛先：経理部・総務部 各位"),
        p("2026年12月1日から、紙の請求書は受付窓口でスキャンし、文書取込システムへ登録してください。"),
        h(2, "変更点"),
        ul("原本は受付後30日間保管し、その後廃棄する",
           "登録時に文書の種類を選ぶ必要はない（自動で分類される）",
           "分類できなかった文書は担当者の確認待ちになる"),
        h(2, "担当窓口"),
        table([
            ["拠点", "窓口", "担当", "内線"],
            ["本社", "受付", "鈴木", "1201"],
            [None, "経理", "高橋", "1305"],
            [None, "総務", "田中", "1410"],
            ["大阪支社", "受付", "伊藤", "2101"],
        ], spans=[(1, 0, 3, 1)]),
        p("ご不明な点は総務部までお問い合わせください。"),
    ])

DECK = dict(
    id="deck", format="pptx", language="en",
    slides=[
        [h(1, "Vault Extract Rollout"), p("Phase 2 plan, December 2026")],
        [h(1, "Goals"), ul("Process every scanned invoice within two minutes",
                           "Keep the review rate under 5 percent",
                           "Cite the source page for every extracted field")],
        [h(1, "Schedule"), table([["Phase", "Start", "End"],
                                  ["Pilot", "2026-12-01", "2026-12-20"],
                                  ["Rollout", "2027-01-05", "2027-02-28"],
                                  ["Review", "2027-03-01", "2027-03-15"]])],
    ])

LEDGER = dict(
    id="ledger", format="xlsx", language="ja",
    sheets=[
        ("売上", [["日付", "顧客", "金額"],
                ["2026-11-01", "株式会社サンプル商事", "473000"],
                ["2026-11-04", "みらい物産株式会社", "128000"],
                ["2026-11-08", "株式会社ひかり建設", "960000"],
                ["2026-11-12", "株式会社サンプル商事", "30000"],
                ["2026-11-15", "合同会社つばさ", "56000"],
                ["2026-11-20", "みらい物産株式会社", "212000"],
                ["2026-11-25", "株式会社ひかり建設", "340000"],
                ["2026-11-28", "合同会社つばさ", "88000"]]),
        ("経費", [["日付", "科目", "金額", "備考"],
                ["2026-11-02", "旅費交通費", "12400", "大阪出張"],
                ["2026-11-06", "通信費", "8800", "回線使用料"],
                ["2026-11-10", "消耗品費", "15600", "トナー"],
                ["2026-11-18", "会議費", "4200", "打合せ"],
                ["2026-11-27", "支払手数料", "1650", "振込手数料"]]),
    ])

CUSTOMERS = dict(
    id="customers", format="csv", language="ja", encoding="cp932",
    rows=[["顧客コード", "顧客名", "担当者", "電話番号"],
          ["C001", "株式会社サンプル商事", "山田 太郎", "03-1111-2222"],
          ["C002", "みらい物産株式会社", "中村 一郎", "06-3333-4444"],
          ["C003", "株式会社ひかり建設", "小林 美咲", "052-555-6666"],
          ["C004", "合同会社つばさ", "加藤 健", "092-777-8888"],
          ["C005", "有限会社こもれび", "吉田 さくら", "011-999-0000"],
          ["C006", "株式会社デジナイト", "佐藤 花子", "03-1234-5678"]])

DOCUMENTS = [INVOICE, CONTRACT, REPORT, PRICELIST, CONTRACT_SCAN, RECEIPT, MEMO, DECK, LEDGER, CUSTOMERS]
EXTENSIONS = {"pdf": "pdf", "pdf-scan": "pdf", "image": "png", "docx": "docx", "pptx": "pptx", "xlsx": "xlsx",
              "csv": "csv"}
MEDIA_TYPES = {"pdf": "application/pdf", "png": "image/png",
               "docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
               "pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
               "xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "csv": "text/csv"}

# ----------------------------------------------------------------------------------------------- PDF rendering

_JP_FONT = pymupdf.Font("japan")  # Droid Sans Fallback, shipped with PyMuPDF


def _archive_and_css(extra=""):
    archive = pymupdf.Archive()
    archive.add(_JP_FONT.buffer, "jp.font")
    css = ("@font-face {font-family: jp; src: url(jp.font);} "
           "* {font-family: jp; font-size: 10.5pt; line-height: 1.7;} "
           "h1 {font-size: 18pt; margin: 0 0 10pt 0;} h2 {font-size: 12.5pt; margin: 12pt 0 4pt 0;} "
           "p {margin: 0 0 6pt 0;} ul {margin: 0 0 6pt 0;} "
           "table {border-collapse: collapse; margin: 6pt 0;} td, th {border: 0.8pt solid #333; padding: 3pt 5pt;} "
           + extra)
    return archive, css


def _html(elements):
    out = []
    for e in elements:
        if e["type"] == "heading":
            out.append(f"<h{e['level']}>{escape(e['text'])}</h{e['level']}>")
        elif e["type"] == "paragraph":
            out.append(f"<p>{escape(e['text'])}</p>")
        elif e["type"] == "list":
            out.append("<ul>" + "".join(f"<li>{escape(i)}</li>" for i in e["items"]) + "</ul>")
        elif e["type"] == "table":
            spans = {(r, c): (rs, cs) for r, c, rs, cs in e["spans"]}
            rows = []
            for r, row in enumerate(e["rows"]):
                cells = []
                for c, value in enumerate(row):
                    if value is None:
                        continue
                    rs, cs = spans.get((r, c), (1, 1))
                    tag = "th" if r < e["headerRows"] else "td"
                    attrs = (f' rowspan="{rs}"' if rs > 1 else "") + (f' colspan="{cs}"' if cs > 1 else "")
                    cells.append(f"<{tag}{attrs}>{escape(value)}</{tag}>")
                rows.append("<tr>" + "".join(cells) + "</tr>")
            out.append("<table>" + "".join(rows) + "</table>")
    return "\n".join(out)


def _page_furniture(page, number, spec):
    if spec.get("header"):
        page.insert_text((60, 38), spec["header"], fontname="japan", fontsize=8.5, color=(0.35, 0.35, 0.35))
    if spec.get("footer") == "page":
        text = f"- {number} -"
        width = pymupdf.get_text_length(text, fontname="helv", fontsize=9)
        page.insert_text(((page.rect.width - width) / 2, page.rect.height - 30), text, fontname="helv", fontsize=9)


def render_pdf(spec, path):
    extra = f"td, th {{padding: {spec['cellPadding']}pt 5pt;}}" if spec.get("cellPadding") else ""
    if spec.get("lineHeight"):
        extra += f" * {{line-height: {spec['lineHeight']};}}"
    archive, css = _archive_and_css(extra)
    mediabox = pymupdf.paper_rect("a4")
    body = mediabox + (60, 60, -60, -60)
    buffer = io.BytesIO()
    writer = pymupdf.DocumentWriter(buffer)
    if spec.get("layout") == "two-column":
        head = pymupdf.Story(html=_html(spec["elements"][:spec["fullWidth"]]), user_css=css, archive=archive)
        rest = pymupdf.Story(html=_html(spec["elements"][spec["fullWidth"]:]), user_css=css, archive=archive)
        gap, first = 18, True
        more = True
        while more:
            device = writer.begin_page(mediabox)
            top = body.y0
            if first:
                _, filled = head.place(body)
                head.draw(device)
                top = pymupdf.Rect(filled).y1 + 10
                first = False
            half = (body.width - gap) / 2
            for x0 in (body.x0, body.x0 + half + gap):
                if not more:
                    break
                more, _ = rest.place(pymupdf.Rect(x0, top, x0 + half, body.y1))
                rest.draw(device)
            writer.end_page()
    else:
        story = pymupdf.Story(html=_html(spec["elements"]), user_css=css, archive=archive)
        more = True
        while more:
            device = writer.begin_page(mediabox)
            more, _ = story.place(body)
            story.draw(device)
            writer.end_page()
    writer.close()
    doc = pymupdf.open("pdf", buffer.getvalue())
    for number, page in enumerate(doc, start=1):
        _page_furniture(page, number, spec)
    doc.subset_fonts()
    doc.set_metadata({"title": spec["id"], "creator": "Vault Parse evaluation corpus", "producer": "PyMuPDF",
                      "creationDate": "D:20261114000000", "modDate": "D:20261114000000"})
    doc.save(path, garbage=4, deflate=True, no_new_id=True)
    return doc.page_count


def _degrade(image, seed, angle):
    rng = random.Random(seed)
    image = image.convert("L").rotate(angle, resample=Image.BICUBIC, expand=True, fillcolor=255)
    pixels = image.load()
    width, height = image.size
    for _ in range(width * height // 60):
        x, y = rng.randrange(width), rng.randrange(height)
        pixels[x, y] = rng.choice((0, 90, 170, 255))
    return image.filter(ImageFilter.GaussianBlur(radius=0.6))


def render_scan(spec, source_pdf, path):
    source = pymupdf.open(source_pdf)
    out = pymupdf.open()
    for index, page in enumerate(source):
        pix = page.get_pixmap(dpi=150, colorspace=pymupdf.csGRAY)
        image = Image.open(io.BytesIO(pix.tobytes("png")))
        image = _degrade(image, seed=1000 + index, angle=0.6 if index % 2 == 0 else -0.4)
        jpeg = io.BytesIO()
        image.save(jpeg, format="JPEG", quality=70)
        target = out.new_page(width=page.rect.width, height=page.rect.height)
        target.insert_image(target.rect, stream=jpeg.getvalue())
    out.set_metadata({"title": spec["id"], "creator": "Vault Parse evaluation corpus", "producer": "PyMuPDF",
                      "creationDate": "D:20261114000000", "modDate": "D:20261114000000"})
    out.save(path, garbage=4, deflate=True, no_new_id=True)
    return out.page_count


def render_receipt(spec, path):
    archive, css = _archive_and_css("* {font-size: 9.5pt; line-height: 1.5;} p {margin: 0 0 3pt 0;}")
    mediabox = pymupdf.Rect(0, 0, 230, 420)
    buffer = io.BytesIO()
    writer = pymupdf.DocumentWriter(buffer)
    story = pymupdf.Story(html=_html(spec["elements"]), user_css=css, archive=archive)
    device = writer.begin_page(mediabox)
    story.place(mediabox + (16, 16, -16, -16))
    story.draw(device)
    writer.end_page()
    writer.close()
    pix = pymupdf.open("pdf", buffer.getvalue())[0].get_pixmap(dpi=220)
    image = Image.open(io.BytesIO(pix.tobytes("png")))
    image = _degrade(image, seed=7, angle=1.8)
    image.save(path, format="PNG", optimize=True)
    return 1

# ----------------------------------------------------------------------------------------------- OpenXML


def _zip(path, parts):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in parts.items():
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 14, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)


W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'


def render_docx(spec, path):
    body = []
    for e in spec["elements"]:
        if e["type"] == "heading":
            body.append(f'<w:p><w:pPr><w:pStyle w:val="Heading{e["level"]}"/></w:pPr><w:r><w:t>{escape(e["text"])}</w:t></w:r></w:p>')
        elif e["type"] == "paragraph":
            body.append(f'<w:p><w:r><w:t>{escape(e["text"])}</w:t></w:r></w:p>')
        elif e["type"] == "list":
            for item in e["items"]:
                body.append('<w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>'
                            f'<w:r><w:t>{escape(item)}</w:t></w:r></w:p>')
        elif e["type"] == "table":
            spans = {(r, c): (rs, cs) for r, c, rs, cs in e["spans"]}
            covered = {}
            for (r, c), (rs, cs) in spans.items():
                for rr in range(r + 1, r + rs):
                    covered[(rr, c)] = cs
            columns = len(e["rows"][0])
            rows = []
            for r, row in enumerate(e["rows"]):
                cells, c = [], 0
                while c < columns:
                    if (r, c) in covered:
                        cs = covered[(r, c)]
                        span = f'<w:gridSpan w:val="{cs}"/>' if cs > 1 else ""
                        cells.append(f'<w:tc><w:tcPr>{span}<w:vMerge/></w:tcPr><w:p/></w:tc>')
                        c += cs
                        continue
                    rs, cs = spans.get((r, c), (1, 1))
                    pr = (f'<w:gridSpan w:val="{cs}"/>' if cs > 1 else "") + ('<w:vMerge w:val="restart"/>' if rs > 1 else "")
                    cells.append(f'<w:tc><w:tcPr>{pr}</w:tcPr><w:p><w:r><w:t>{escape(row[c] or "")}</w:t></w:r></w:p></w:tc>')
                    c += cs
                rows.append("<w:tr>" + "".join(cells) + "</w:tr>")
            grid = "".join('<w:gridCol w:w="2000"/>' for _ in range(columns))
            borders = ('<w:tblBorders>' + "".join(f'<w:{side} w:val="single" w:sz="4" w:space="0" w:color="000000"/>'
                                                  for side in ("top", "left", "bottom", "right", "insideH", "insideV"))
                       + '</w:tblBorders>')
            body.append(f'<w:tbl><w:tblPr>{borders}</w:tblPr><w:tblGrid>{grid}</w:tblGrid>{"".join(rows)}</w:tbl>')
    document = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document {W}><w:body>'
                + "".join(body) + '<w:sectPr/></w:body></w:document>')
    styles = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles {W}>'
              '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>'
              '<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style>'
              '<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>'
              '<w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/></w:style>'
              '</w:styles>')
    numbering = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:numbering {W}>'
                 '<w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/></w:lvl></w:abstractNum>'
                 '<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>')
    _zip(path, {
        "[Content_Types].xml": '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                               '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                               '<Default Extension="xml" ContentType="application/xml"/>'
                               '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
                               '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>'
                               '<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>'
                               '</Types>',
        "_rels/.rels": '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                       '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>',
        "word/_rels/document.xml.rels": '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                                        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>'
                                        '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/></Relationships>',
        "word/document.xml": document,
        "word/styles.xml": styles,
        "word/numbering.xml": numbering,
    })
    return None


P = ('xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
     'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" '
     'xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"')
TABLE_URI = "http://schemas.openxmlformats.org/drawingml/2006/table"


def _shape(shape_id, paragraphs, y, title=False):
    ph = '<p:ph type="title"/>' if title else ""
    return (f'<p:sp><p:nvSpPr><p:cNvPr id="{shape_id}" name="Shape{shape_id}"/><p:cNvSpPr/><p:nvPr>{ph}</p:nvPr></p:nvSpPr>'
            f'<p:spPr><a:xfrm><a:off x="600000" y="{y}"/><a:ext cx="8000000" cy="900000"/></a:xfrm></p:spPr>'
            '<p:txBody><a:bodyPr/><a:lstStyle/>' + "".join(paragraphs) + '</p:txBody></p:sp>')


def render_pptx(spec, path):
    parts, slide_ids, slide_rels, overrides = {}, [], [], []
    for index, slide in enumerate(spec["slides"], start=1):
        shapes, shape_id, y = [], 2, 400000
        for e in slide:
            if e["type"] == "heading":
                shapes.append(_shape(shape_id, [f'<a:p><a:r><a:t>{escape(e["text"])}</a:t></a:r></a:p>'], y, title=True))
            elif e["type"] == "paragraph":
                shapes.append(_shape(shape_id, [f'<a:p><a:r><a:t>{escape(e["text"])}</a:t></a:r></a:p>'], y))
            elif e["type"] == "list":
                items = [f'<a:p><a:pPr marL="285750" indent="-285750"><a:buChar char="•"/></a:pPr><a:r><a:t>{escape(i)}</a:t></a:r></a:p>'
                         for i in e["items"]]
                shapes.append(_shape(shape_id, items, y))
            elif e["type"] == "table":
                columns = len(e["rows"][0])
                grid = "".join('<a:gridCol w="2600000"/>' for _ in range(columns))
                rows = "".join('<a:tr h="370000">' + "".join(
                    f'<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{escape(cell)}</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc>'
                    for cell in row) + '</a:tr>' for row in e["rows"])
                shapes.append(f'<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="{shape_id}" name="Table{shape_id}"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>'
                              f'<p:xfrm><a:off x="600000" y="{y}"/><a:ext cx="7800000" cy="1600000"/></p:xfrm>'
                              f'<a:graphic><a:graphicData uri="{TABLE_URI}"><a:tbl><a:tblPr/><a:tblGrid>{grid}</a:tblGrid>{rows}</a:tbl></a:graphicData></a:graphic></p:graphicFrame>')
            shape_id += 1
            y += 1100000
        parts[f"ppt/slides/slide{index}.xml"] = (
            f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld {P}><p:cSld><p:spTree>'
            '<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>'
            + "".join(shapes) + '</p:spTree></p:cSld></p:sld>')
        slide_ids.append(f'<p:sldId id="{255 + index}" r:id="rId{index}"/>')
        slide_rels.append(f'<Relationship Id="rId{index}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide{index}.xml"/>')
        overrides.append(f'<Override PartName="/ppt/slides/slide{index}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>')
    parts["ppt/presentation.xml"] = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation {P}>'
                                     f'<p:sldIdLst>{"".join(slide_ids)}</p:sldIdLst><p:sldSz cx="9144000" cy="6858000"/></p:presentation>')
    parts["ppt/_rels/presentation.xml.rels"] = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                                                + "".join(slide_rels) + '</Relationships>')
    parts["_rels/.rels"] = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/></Relationships>')
    parts["[Content_Types].xml"] = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                                    '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                                    '<Default Extension="xml" ContentType="application/xml"/>'
                                    '<Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>'
                                    + "".join(overrides) + '</Types>')
    ordered = {k: parts[k] for k in ["[Content_Types].xml", "_rels/.rels", "ppt/presentation.xml", "ppt/_rels/presentation.xml.rels"]}
    ordered.update({k: v for k, v in parts.items() if k not in ordered})
    _zip(path, ordered)
    return len(spec["slides"])


def render_xlsx(spec, path):
    workbook = Workbook()
    workbook.remove(workbook.active)
    for name, rows in spec["sheets"]:
        sheet = workbook.create_sheet(name)
        for row in rows:
            sheet.append(row)
    workbook.properties.creator = "Vault Parse evaluation corpus"
    workbook.properties.created = datetime.datetime(2026, 11, 14)
    buffer = io.BytesIO()
    workbook.save(buffer)
    # Re-zip with fixed timestamps so the file is byte-stable. openpyxl stamps the save time as dcterms:modified
    # whatever the properties say, so that is pinned too.
    with zipfile.ZipFile(io.BytesIO(buffer.getvalue())) as source:
        parts = {name: source.read(name) for name in source.namelist()}
    parts["docProps/core.xml"] = re.sub(
        rb"(<dcterms:modified[^>]*>)[^<]*", rb"\g<1>2026-11-14T00:00:00Z", parts["docProps/core.xml"])
    _zip(path, parts)
    return None


def render_csv(spec, path):
    text = "\r\n".join(",".join(row) for row in spec["rows"]) + "\r\n"
    with open(path, "wb") as f:
        f.write(text.encode(spec["encoding"]))
    return None

# ----------------------------------------------------------------------------------------------- truth


def truth_elements(spec, by_id):
    if spec.get("source"):
        return by_id[spec["source"]]["elements"]
    if spec["format"] == "pptx":
        return [e for slide in spec["slides"] for e in slide]
    if spec["format"] == "xlsx":
        return [e for name, rows in spec["sheets"] for e in (h(2, name), table(rows))]
    if spec["format"] == "csv":
        return [table(spec["rows"])]
    return spec["elements"]


def main(out):
    os.makedirs(out, exist_ok=True)
    by_id = {d["id"]: d for d in DOCUMENTS}
    for spec in DOCUMENTS:
        ext = EXTENSIONS[spec["format"]]
        file = f"{spec['id']}.{ext}"
        path = os.path.join(out, file)
        if spec["format"] == "pdf":
            pages = render_pdf(spec, path)
        elif spec["format"] == "pdf-scan":
            pages = render_scan(spec, os.path.join(out, f"{spec['source']}.pdf"), path)
        elif spec["format"] == "image":
            pages = render_receipt(spec, path)
        else:
            pages = {"docx": render_docx, "pptx": render_pptx, "xlsx": render_xlsx, "csv": render_csv}[spec["format"]](spec, path)
        source = by_id.get(spec.get("source"), {})
        truth = {
            "id": spec["id"],
            "file": file,
            "mediaType": MEDIA_TYPES[ext],
            "format": spec["format"],
            "language": spec["language"],
            "ocr": spec.get("ocr", False),
            "pages": pages,
            "noise": [n for n in (spec.get("header") or source.get("header"),) if n],
            "pageNumbers": (spec.get("footer") or source.get("footer")) == "page",
            "elements": truth_elements(spec, by_id),
        }
        with open(os.path.join(out, f"{spec['id']}.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(truth, f, ensure_ascii=False, indent=2)
            f.write("\n")
        print(f"{file}: {pages or '-'} page(s), {os.path.getsize(path):,} bytes")


if __name__ == "__main__":
    main(sys.argv[1])

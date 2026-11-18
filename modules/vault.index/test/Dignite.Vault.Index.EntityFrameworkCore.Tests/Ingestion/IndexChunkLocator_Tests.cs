using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>Finding each chunk's range of the Markdown, over the evaluation corpus and the cases that make it hard.</summary>
public class IndexChunkLocator_Tests
{
    private readonly ITestOutputHelper _output;

    public IndexChunkLocator_Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<int> Budgets => new() { IndexProfileDefaults.MaxTokensPerChunk, 120 };

    [Theory]
    [MemberData(nameof(Budgets))]
    public async Task Every_chunk_of_the_evaluation_corpus_is_found_where_its_text_is(int maxTokens)
    {
        var chunker = new IndexChunker();
        var profile = new ResolvedIndexProfile(ChunkingStrategy.Heading, maxTokens, IndexProfileDefaults.OverlapTokens);
        int total = 0, located = 0;
        double coverage = 0;
        foreach (var file in Directory.GetFiles(CorpusDirectory(), "*.md").Order(StringComparer.Ordinal))
        {
            var markdown = await File.ReadAllTextAsync(file);
            var chunks = await chunker.ChunkAsync(markdown, Path.GetFileName(file), profile);
            var ranges = IndexChunkLocator.Locate(markdown, chunks.Select(c => (c.HeadingPath, c.Text)).ToList());
            for (var i = 0; i < chunks.Count; i++)
            {
                total++;
                if (ranges[i] is not { } range)
                {
                    _output.WriteLine($"{Path.GetFileName(file)} #{i}: not found");
                    continue;
                }

                located++;
                coverage += Covered(markdown.Substring(range.Offset, range.Length), chunks[i]);
            }
        }

        _output.WriteLine($"{maxTokens} tokens: {located} of {total} chunks located, mean line coverage {coverage / Math.Max(located, 1):P1}");
        located.ShouldBe(total);
        // A split table's repeated header row is outside its later pieces' ranges on purpose; little else is.
        (coverage / located).ShouldBeGreaterThan(0.93);
    }

    [Fact]
    public void A_chunk_rendered_from_an_html_table_is_found_in_the_html()
    {
        const string markdown = "# 請求書\n\n<table><tr><th>品目</th><th>金額</th></tr><tr><td>コピー用紙</td><td>4,800円</td></tr></table>\n\n以上";
        (string?, string) chunk = ("# 請求書", "# 請求書\n| 品目 | 金額 |\n| --- | --- |\n| コピー用紙 | 4,800円 |");

        var range = IndexChunkLocator.Locate(markdown, [chunk]).Single().ShouldNotBeNull();

        markdown.Substring(range.Offset, range.Length).ShouldStartWith("品目");
        markdown.Substring(range.Offset, range.Length).ShouldEndWith("4,800円");
    }

    [Fact]
    public void Repeated_text_is_placed_in_document_order()
    {
        const string markdown = "## 第1条\n\n本契約は甲乙間で締結する。\n\n## 第2条\n\n本契約は甲乙間で締結する。\n\n賃料は月末に支払う。";
        (string?, string) first = ("## 第1条", "## 第1条\n本契約は甲乙間で締結する。");
        (string?, string) second = ("## 第2条", "## 第2条\n本契約は甲乙間で締結する。\n賃料は月末に支払う。");

        var ranges = IndexChunkLocator.Locate(markdown, [first, second]);

        ranges[0]!.Value.Offset.ShouldBe(markdown.IndexOf("本契約", StringComparison.Ordinal));
        ranges[1]!.Value.Offset.ShouldBe(markdown.LastIndexOf("本契約", StringComparison.Ordinal));
    }

    [Fact]
    public void The_second_piece_of_a_split_table_starts_at_its_own_rows_not_the_header_it_repeats()
    {
        const string markdown = "| 品名 | 金額 |\n| --- | --- |\n| コピー用紙 | 4,800 |\n| トナー | 12,000 |\n| ファイル | 900 |\n| 封筒 | 1,200 |";
        (string?, string) first = (null, "| 品名 | 金額 |\n| --- | --- |\n| コピー用紙 | 4,800 |\n| トナー | 12,000 |");
        (string?, string) second = (null, "| 品名 | 金額 |\n| --- | --- |\n| ファイル | 900 |\n| 封筒 | 1,200 |");

        var ranges = IndexChunkLocator.Locate(markdown, [first, second]);

        ranges[0]!.Value.Offset.ShouldBe(markdown.IndexOf("品名", StringComparison.Ordinal));
        ranges[1]!.Value.Offset.ShouldBe(markdown.IndexOf("ファイル", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_whose_merged_cell_the_chunker_repeated_is_found_by_its_own_cells()
    {
        // The parse wrote a rowspan; the chunker's Markdown repeats 運用 into the second row, which the HTML lacks.
        const string markdown = "<table><tr><th>区分</th><th>サービス</th><th>価格</th></tr>"
            + "<tr><td rowspan=\"2\">運用</td><td>データ移行作業</td><td>300,000</td></tr>"
            + "<tr><td>画面追加作業</td><td>120,000</td></tr></table>";
        (string?, string) first = (null, "| 区分 | サービス | 価格 |\n| --- | --- | --- |\n| 運用 | データ移行作業 | 300,000 |");
        (string?, string) second = (null, "| 区分 | サービス | 価格 |\n| --- | --- | --- |\n| 運用 | 画面追加作業 | 120,000 |");

        var ranges = IndexChunkLocator.Locate(markdown, [first, second]);

        var range = ranges[1].ShouldNotBeNull();
        markdown.Substring(range.Offset, range.Length).ShouldStartWith("画面追加作業");
        markdown.Substring(range.Offset, range.Length).ShouldEndWith("120,000");
        ranges[0]!.Value.Offset.ShouldBeLessThan(range.Offset);
    }

    [Fact]
    public void Rows_of_a_table_followed_by_one_with_the_same_header_stay_in_their_table()
    {
        const string markdown = "| 品名 | 金額 |\n| --- | --- |\n| りんご | 100 |\n| みかん | 200 |\n\n"
            + "| 品名 | 金額 |\n| --- | --- |\n| ぶどう | 300 |\n| もも | 400 |";
        string Row(string row) => "| 品名 | 金額 |\n| --- | --- |\n" + row;
        (string?, string)[] chunks =
        [
            (null, Row("| りんご | 100 |")), (null, Row("| みかん | 200 |")), (null, Row("| ぶどう | 300 |")), (null, Row("| もも | 400 |"))
        ];

        var ranges = IndexChunkLocator.Locate(markdown, chunks);

        string At(int i) => markdown.Substring(ranges[i]!.Value.Offset, ranges[i]!.Value.Length);
        At(1).ShouldBe("みかん | 200");
        // The second table's first row takes that table's header with it, nothing of the first table.
        At(2).ShouldStartWith("品名");
        At(2).ShouldEndWith("ぶどう | 300");
        ranges[2]!.Value.Offset.ShouldBeGreaterThan(markdown.IndexOf("\n\n", StringComparison.Ordinal));
        At(3).ShouldBe("もも | 400");
    }

    [Fact]
    public void A_line_with_a_zero_width_space_of_its_own_is_found()
    {
        // The chunker drops U+200B (it holds empty table cells open with it), so the Markdown's own must not count.
        var markdown = "# 契約書\n\n第1条" + (char)0x200B + "本契約の目的を定める。";
        (string?, string) chunk = (null, "第1条本契約の目的を定める。");

        IndexChunkLocator.Locate(markdown, [chunk]).Single().ShouldNotBeNull();
    }

    [Fact]
    public void A_chunk_whose_text_is_not_in_the_markdown_has_no_range()
    {
        (string?, string) chunk = (null, "この文章は原文にありません。");

        IndexChunkLocator.Locate("# 契約書\n\n本文です。", [chunk]).Single().ShouldBeNull();
    }

    [Fact]
    public void A_chunk_is_placed_on_the_pages_whose_text_it_holds()
    {
        const string markdown = "# 契約書\n\n第1条 本契約の目的を定める。\n\n第2条 賃料は月末に支払う。\n\n第3条 契約期間は二年とする。";
        int At(string text) => markdown.IndexOf(text, StringComparison.Ordinal);
        var pages = new List<DocumentPageSpansDto>
        {
            Page(1, (At("# 契約書"), At("第2条") - 2 - At("# 契約書"))),
            Page(2, (At("第2条"), markdown.Length - At("第2条")))
        };
        (string?, string)[] chunks =
        [
            ("# 契約書", "# 契約書\n第1条 本契約の目的を定める。"),
            ("# 契約書", "# 契約書\n第2条 賃料は月末に支払う。\n第3条 契約期間は二年とする。"),
            ("# 契約書", "# 契約書\nどこにもない文章です。")
        ];

        var placed = IndexChunkLocator.Pages(markdown, pages, chunks);

        placed[0].ShouldBe((1, 1));
        placed[1].ShouldBe((2, 2));
        placed[2].ShouldBe((null, null));
        IndexChunkLocator.Pages(markdown, null, chunks).ShouldAllBe(p => p.First == null && p.Last == null);

        // A chunk that runs across a page break is on both.
        IndexChunkLocator.Pages(markdown, pages, [("# 契約書", "# 契約書\n第1条 本契約の目的を定める。\n第2条 賃料は月末に支払う。")])
            .Single().ShouldBe((1, 2));
    }

    private static DocumentPageSpansDto Page(int number, params (int Offset, int Length)[] spans) => new()
    {
        PageNumber = number,
        Spans = spans.Select(s => new DocumentTextSpanDto { Offset = s.Offset, Length = s.Length }).ToList()
    };

    /// <summary>The share of the chunk's searchable text that the range holds.</summary>
    private static double Covered(string range, IndexChunkDraft chunk)
    {
        var haystack = NormalizedMarkdown.Key(range);
        var keys = chunk.Text.Split('\n').Select(NormalizedMarkdown.Key).Where(k => k.Length >= IndexChunkLocator.MinKeyLength).ToList();
        if (chunk.HeadingPath is not null && keys.Count > 0 && keys[0].StartsWith(NormalizedMarkdown.Key(chunk.HeadingPath), StringComparison.Ordinal))
        {
            keys.RemoveAt(0);
        }

        var all = keys.Sum(k => k.Length);
        return all == 0 ? 1 : (double)keys.Where(k => haystack.Contains(k, StringComparison.Ordinal)).Sum(k => k.Length) / all;
    }

    private static string CorpusDirectory([CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "Dignite.Vault.Index.EntityFrameworkCore.SqlServer.Tests", "Evaluation", "Corpus");
}

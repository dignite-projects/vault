using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// HTML tables and figures in a document's Markdown, as Azure Document Intelligence writes them, reach the chunker as
/// Markdown: DataIngestion's reader drops an HTML block with all its text.
/// </summary>
public class MarkdownHtmlNormalizer_Tests
{
    [Fact]
    public void Markdown_without_html_blocks_is_returned_as_it_is()
    {
        const string markdown = "# 請求書\n\n| 品名 | 金額 |\n| --- | --- |\n| 保守 | 150,000 |\n\n金額は a < b のとき<b>太字</b>。\n";

        MarkdownHtmlNormalizer.Normalize(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void A_table_becomes_a_gfm_table_with_its_caption_above()
    {
        const string markdown = """
            # 月次報告

            <table>
            <caption>表1 売上</caption>
            <tr><th>部門</th><th>10月</th><th>11月</th></tr>
            <tr><td>営業</td><td>1,200</td><td>1,350</td></tr>
            <tr><td>保守&amp;運用</td><td>800</td><td>a|b<br/>c</td></tr>
            </table>
            注:単位は千円。

            ## 次節
            """;

        var normalized = MarkdownHtmlNormalizer.Normalize(markdown);

        normalized.ShouldNotContain("<table>");
        normalized.ShouldContain("表1 売上\n\n| 部門 | 10月 | 11月 |\n| --- | --- | --- |\n| 営業 | 1,200 | 1,350 |\n| 保守&運用 | 800 | a\\|b c |");
        normalized.ShouldContain("注:単位は千円。");
        normalized.ShouldContain("## 次節");
    }

    [Fact]
    public void A_merged_cell_is_repeated_into_every_cell_it_spans()
    {
        const string markdown = """
            <table>
            <tr><th rowspan="2">地域</th><th colspan="2">売上</th></tr>
            <tr><th>上期</th><th>下期</th></tr>
            <tr><td>東京</td><td>10</td><td>12</td></tr>
            </table>
            """;

        var lines = MarkdownHtmlNormalizer.Normalize(markdown).Trim().Split('\n');

        lines.ShouldBe([
            "| 地域 | 売上 | 売上 |",
            "| --- | --- | --- |",
            "| 地域 | 上期 | 下期 |",
            "| 東京 | 10 | 12 |"
        ]);
    }

    [Fact]
    public void A_figure_becomes_its_text()
    {
        const string markdown = "前文。\n\n<figure>\n<figcaption>図1 構成</figcaption>\nサーバーとクライアント\n</figure>\n\n後文。";

        var normalized = MarkdownHtmlNormalizer.Normalize(markdown);

        normalized.ShouldNotContain("<figure>");
        normalized.ShouldContain("図1 構成");
        normalized.ShouldContain("サーバーとクライアント");
    }

    [Fact]
    public void A_table_in_a_code_block_and_other_html_are_left_alone()
    {
        const string markdown = "```html\n<table><tr><td>x</td></tr></table>\n```\n\n<div>\nメモ\n</div>\n";

        MarkdownHtmlNormalizer.Normalize(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void Normalizing_twice_changes_nothing()
    {
        const string markdown = "# A\n\n<table>\n<tr><th>x</th></tr>\n<tr><td>1</td></tr>\n</table>\n";

        var once = MarkdownHtmlNormalizer.Normalize(markdown);

        MarkdownHtmlNormalizer.Normalize(once).ShouldBe(once);
    }

    [Fact]
    public async Task An_html_table_too_large_for_one_chunk_is_split_by_rows_repeating_its_header()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"<tr><td>品目{i}</td><td>{i * 1000}</td><td>備考{i}の説明文</td></tr>"));
        var markdown = $"# 請求書\n\n## 明細\n\n下表に品目を示す。\n\n<table>\n<tr><th>品名</th><th>金額</th><th>備考</th></tr>\n{rows}\n</table>\n\n## 支払\n\n月末払い。\n";

        var chunks = await new IndexChunker().ChunkAsync(markdown, "doc", new ResolvedIndexProfile(ChunkingStrategy.Heading, 300, 0));

        var tableChunks = chunks.Where(c => c.HeadingPath == "# 請求書 ## 明細").ToList();
        tableChunks.Count.ShouldBeGreaterThan(1);
        tableChunks.ShouldAllBe(c => c.Text.Contains("品名") && c.Text.Contains("金額"));
        string.Concat(tableChunks.Select(c => c.Text)).ShouldContain("品目60");
        tableChunks[0].Text.ShouldContain("下表に品目を示す。");
        chunks.ShouldContain(c => c.HeadingPath == "# 請求書 ## 支払");
    }
}

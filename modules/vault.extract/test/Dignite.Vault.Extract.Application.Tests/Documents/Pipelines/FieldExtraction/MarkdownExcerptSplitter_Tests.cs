using System;
using System.Linq;
using System.Text;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Where <see cref="MarkdownExcerptSplitter"/> cuts a document whose tables are read in parts: only between lines, never
/// inside a table's header or an HTML row, with the header carried across a cut inside a table and context given across
/// a cut between lines of text. The excerpts cover the Markdown once, so the rows they yield need no deduplication.
/// </summary>
public class MarkdownExcerptSplitter_Tests
{
    private const string PipeHeader = "| 日付 | 入金金額 | 出金金額 | 残高 | 摘要 |\n| --- | --- | --- | --- | --- |";

    private static string Row(int i) => $"| 2025/{1 + (i / 28 % 12):00}/{1 + (i % 28):00} | ¥{i * 1000:N0} | | ¥{i * 7919:N0} | 振込 カ）サンプル{i} |";

    /// <summary>A four-page statement as the parse writes it: each page's header text, then its table with the header repeated.</summary>
    private static string Statement(int pages, int rowsPerPage)
    {
        var sb = new StringBuilder();
        for (var page = 0; page < pages; page++)
        {
            sb.Append("# GMOあおぞらネット銀行\n\n## 取引明細書\n\n作成日：2025年9月26日\n\n").Append(PipeHeader).Append('\n');
            for (var row = 0; row < rowsPerPage; row++)
            {
                sb.Append(Row((page * rowsPerPage) + row)).Append('\n');
            }

            sb.Append($"\n次ページに続く\n\n-{page + 1}/{pages}-\n\n");
        }

        return sb.ToString();
    }

    private static string Owned(string markdown, MarkdownExcerpt excerpt) => markdown.Substring(excerpt.Offset, excerpt.Length);

    [Fact]
    public void The_excerpts_cover_the_markdown_once_in_order()
    {
        var markdown = Statement(pages: 4, rowsPerPage: 30);

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 1024);

        excerpts.Count.ShouldBeGreaterThan(4);
        string.Concat(excerpts.Select(excerpt => Owned(markdown, excerpt))).ShouldBe(markdown);
        excerpts.ShouldAllBe(excerpt => excerpt.Length <= 1024);
        excerpts.Zip(excerpts.Skip(1)).ShouldAllBe(pair => pair.First.Offset + pair.First.Length == pair.Second.Offset);
    }

    [Fact]
    public void A_cut_inside_a_pipe_table_falls_between_rows_and_carries_the_header()
    {
        var markdown = "残高明細\n\n" + PipeHeader + "\n" + string.Join('\n', Enumerable.Range(0, 120).Select(Row)) + "\n";

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 1024);

        excerpts.Count.ShouldBeGreaterThan(3);
        excerpts[0].TableHeader.ShouldBeNull();
        foreach (var excerpt in excerpts.Skip(1))
        {
            excerpt.Text.ShouldStartWith("| 2025/");
            excerpt.TableHeader.ShouldBe(PipeHeader);
            excerpt.ContextBefore.ShouldBeNull();
        }

        // Every row line lands whole in exactly one excerpt.
        excerpts.SelectMany(excerpt => excerpt.Text.Split('\n')).Count(line => line.StartsWith("| 2025/", StringComparison.Ordinal))
            .ShouldBe(120);
    }

    [Fact]
    public void No_cut_leaves_an_excerpt_with_only_a_table_header()
    {
        var markdown = PipeHeader + "\n" + string.Join('\n', Enumerable.Range(0, 40).Select(Row));

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 40);

        excerpts[0].Text.ShouldStartWith(PipeHeader);
        excerpts[0].Text.Split('\n').Length.ShouldBe(3);
        excerpts.Skip(1).ShouldAllBe(excerpt => excerpt.Text.Split('\n').Length == 1 && excerpt.TableHeader == PipeHeader);
    }

    [Fact]
    public void An_html_table_is_cut_between_its_rows_but_not_under_a_rowspan()
    {
        var sb = new StringBuilder("<table>\n<tr><th>日付</th><th>摘要</th><th>金額</th></tr>\n");
        for (var i = 0; i < 30; i++)
        {
            sb.Append(i == 10
                ? $"<tr><td rowspan=\"3\">2025/01/{i:00}</td><td>振込{i}</td><td>{i}</td></tr>\n"
                : $"<tr><td>2025/01/{i:00}</td><td>振込{i}</td><td>{i}</td></tr>\n");
        }

        var markdown = sb.Append("</table>").ToString();

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 120);

        excerpts.Count.ShouldBeGreaterThan(5);
        string.Concat(excerpts.Select(excerpt => Owned(markdown, excerpt))).ShouldBe(markdown);
        foreach (var excerpt in excerpts.Skip(1))
        {
            excerpt.Text.ShouldStartWith("<tr><td");
            excerpt.TableHeader.ShouldBe("<table>\n<tr><th>日付</th><th>摘要</th><th>金額</th></tr>");
            // Rows 11 and 12 share row 10's date cell: an excerpt starting at either would hold rows without it.
            excerpt.Text.ShouldNotStartWith("<tr><td>2025/01/11<");
            excerpt.Text.ShouldNotStartWith("<tr><td>2025/01/12<");
        }

        excerpts.Last().Text.ShouldEndWith("</table>");
    }

    [Fact]
    public void An_html_row_written_across_lines_is_never_cut()
    {
        var sb = new StringBuilder("<table>\n");
        for (var i = 0; i < 20; i++)
        {
            sb.Append("<tr>\n<td>2025/01/").Append(i.ToString("00")).Append("</td>\n<td>").Append(i).Append("</td>\n</tr>\n");
        }

        var markdown = sb.Append("</table>").ToString();

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 100);

        excerpts.Count.ShouldBeGreaterThan(3);
        excerpts.Skip(1).ShouldAllBe(excerpt => excerpt.Text.StartsWith("<tr>\n<td>2025/01/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cut_between_lines_of_text_gives_both_excerpts_the_neighbouring_lines()
    {
        // A table flattened into lines of text, with nothing in the Markdown that marks it as one.
        var lines = Enumerable.Range(0, 60).Select(i => $"2025/01/{i % 28 + 1:00} 振込 カ）サンプル{i} {i * 1000}").ToList();
        var markdown = "取引明細\n" + string.Join('\n', lines);

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 600);

        excerpts.Count.ShouldBeGreaterThan(2);
        for (var i = 1; i < excerpts.Count; i++)
        {
            var previous = excerpts[i - 1].Text.Split('\n');
            var current = excerpts[i].Text.Split('\n');
            excerpts[i].ContextBefore.ShouldBe(string.Join('\n', previous.TakeLast(MarkdownExcerptSplitter.ContextLines)));
            excerpts[i - 1].ContextAfter.ShouldBe(string.Join('\n', current.Take(MarkdownExcerptSplitter.ContextLines)));
            excerpts[i].TableHeader.ShouldBeNull();
        }

        excerpts[0].ContextBefore.ShouldBeNull();
        excerpts[^1].ContextAfter.ShouldBeNull();
    }

    [Fact]
    public void A_cut_at_a_blank_line_is_preferred_and_needs_no_context()
    {
        var paragraph = string.Join('\n', Enumerable.Range(0, 6).Select(i => $"本文の行 {i} はここに続きます。"));
        var markdown = string.Join("\n\n", Enumerable.Range(0, 8).Select(_ => paragraph));

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 400);

        excerpts.Count.ShouldBeGreaterThan(2);
        excerpts.ShouldAllBe(excerpt => excerpt.ContextBefore == null && excerpt.ContextAfter == null);
        excerpts.ShouldAllBe(excerpt => excerpt.Text.StartsWith("本文の行 0 ", StringComparison.Ordinal));
    }

    [Fact]
    public void Context_is_capped_at_a_character_boundary()
    {
        // A supplementary-plane character is two UTF-16 units. The ASCII letters at both ends put a pair across each cap:
        // the 400th unit from the end of the context before, and the 400th from the start of the context after.
        var longLine = "a" + string.Concat(Enumerable.Repeat("𠀋", 400)) + "a";
        var markdown = string.Join('\n', Enumerable.Range(0, 6).Select(_ => longLine));

        var excerpts = MarkdownExcerptSplitter.Split(markdown, (longLine.Length + 1) * 3);

        excerpts.Count.ShouldBe(2);
        excerpts[1].ContextBefore!.Length.ShouldBeLessThanOrEqualTo(MarkdownExcerptSplitter.MaxContextChars);
        char.IsLowSurrogate(excerpts[1].ContextBefore![0]).ShouldBeFalse();
        excerpts[0].ContextAfter!.Length.ShouldBeLessThanOrEqualTo(MarkdownExcerptSplitter.MaxContextChars);
        char.IsHighSurrogate(excerpts[0].ContextAfter![^1]).ShouldBeFalse();
    }

    /// <summary>
    /// The measured failure: an excerpt holding the end of page 1's table, the page break and the start of page 2's table
    /// got back only page 1's rows from two models. A table's edge inside the excerpt is where it ends instead.
    /// </summary>
    [Fact]
    public void A_cut_at_a_tables_edge_is_preferred_to_one_inside_the_next_table()
    {
        // Each page's table is longer than one excerpt, so some excerpt starts inside a table and reaches the next page.
        var markdown = Statement(pages: 4, rowsPerPage: 50);

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 2048);

        excerpts.ShouldContain(excerpt => excerpt.TableHeader != null);
        foreach (var excerpt in excerpts)
        {
            var headers = excerpt.Text.Split('\n').Count(line => line.StartsWith("| --- |", StringComparison.Ordinal));
            // Starting inside a table (its header carried) and then opening another is the mix the cut avoids.
            (excerpt.TableHeader != null && headers > 0).ShouldBeFalse();
        }

        string.Concat(excerpts.Select(excerpt => Owned(markdown, excerpt))).ShouldBe(markdown);
    }

    [Fact]
    public void Table_rows_counts_the_rows_that_begin_in_each_excerpt()
    {
        var pipe = Statement(pages: 3, rowsPerPage: 30);
        var pipeExcerpts = MarkdownExcerptSplitter.Split(pipe, 700);
        pipeExcerpts.Sum(excerpt => excerpt.TableRows).ShouldBe(90);
        pipeExcerpts.ShouldAllBe(excerpt =>
            excerpt.TableRows == excerpt.Text.Split('\n', StringSplitOptions.None).Count(line => line.StartsWith("| 2025/", StringComparison.Ordinal)));

        var html = "<table>\n<tr><th>日付</th><th>金額</th></tr>\n<tr><td rowspan=\"2\">2025/01/01</td><td>1</td></tr>\n<tr><td>2</td></tr>\n<tr><td>2025/01/02</td><td>3</td></tr>\n</table>";
        MarkdownExcerptSplitter.Split(html, 10_000).Single().TableRows.ShouldBe(3);
    }

    [Fact]
    public void A_line_longer_than_the_budget_is_an_excerpt_of_its_own()
    {
        var markdown = "前文\n\n" + new string('長', 500) + "\n\n後文";

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 100);

        excerpts.Select(excerpt => excerpt.Text).ShouldContain(new string('長', 500));
        string.Concat(excerpts.Select(excerpt => Owned(markdown, excerpt))).ShouldBe(markdown);
    }

    [Fact]
    public void A_trailing_line_break_is_not_an_excerpt_of_its_own()
    {
        var markdown = Statement(pages: 1, rowsPerPage: 60) + "\n\n\n";

        var excerpts = MarkdownExcerptSplitter.Split(markdown, 512);

        excerpts.ShouldAllBe(excerpt => !string.IsNullOrWhiteSpace(excerpt.Text));
        string.Concat(excerpts.Select(excerpt => Owned(markdown, excerpt))).ShouldBe(markdown);
    }

    [Fact]
    public void Halve_cuts_at_the_allowed_cut_nearest_the_middle()
    {
        var markdown = PipeHeader + "\n" + string.Join('\n', Enumerable.Range(0, 40).Select(Row));
        var whole = MarkdownExcerptSplitter.Split(markdown, markdown.Length).Single();

        var halves = MarkdownExcerptSplitter.Halve(markdown, whole);

        halves.Count.ShouldBe(2);
        (Owned(markdown, halves[0]) + Owned(markdown, halves[1])).ShouldBe(markdown);
        halves[0].Text.Split('\n').Count(line => line.StartsWith("| 2025/", StringComparison.Ordinal)).ShouldBeInRange(19, 21);
        halves[1].TableHeader.ShouldBe(PipeHeader);
    }

    [Fact]
    public void Halve_keeps_the_context_and_header_of_the_excerpt_it_cuts()
    {
        var markdown = PipeHeader + "\n" + string.Join('\n', Enumerable.Range(0, 120).Select(Row));
        var second = MarkdownExcerptSplitter.Split(markdown, 1024)[1];

        var halves = MarkdownExcerptSplitter.Halve(markdown, second);

        halves.Count.ShouldBe(2);
        halves[0].Offset.ShouldBe(second.Offset);
        (halves[1].Offset + halves[1].Length).ShouldBe(second.Offset + second.Length);
        halves.ShouldAllBe(half => half.TableHeader == PipeHeader);
    }

    [Fact]
    public void Halve_returns_an_excerpt_with_no_cut_inside_unchanged()
    {
        const string markdown = "前文\n\nたった一行の長い本文\n\n後文";
        var single = MarkdownExcerptSplitter.Split(markdown, 8).Single(excerpt => excerpt.Text == "たった一行の長い本文");

        MarkdownExcerptSplitter.Halve(markdown, single).ShouldBe([single]);
    }

    [Fact]
    public void Table_row_chars_counts_the_rows_of_recognised_tables_only()
    {
        var rows = Enumerable.Range(0, 5).Select(Row).ToList();
        var markdown = "本文 | パイプを含む行\n\n" + PipeHeader + "\n" + string.Join('\n', rows) + "\n\n<table>\n<tr><th>a</th></tr>\n<tr><td>b</td></tr>\n</table>\n";

        MarkdownExcerptSplitter.TableRowChars(markdown)
            .ShouldBe(rows.Sum(row => row.Length) + "<tr><td>b</td></tr>".Length + "</table>".Length);
    }
}

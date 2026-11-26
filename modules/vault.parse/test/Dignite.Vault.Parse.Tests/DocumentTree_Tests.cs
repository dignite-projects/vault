using System.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// <see cref="MarkdownDocumentReader"/> reads Markdown into the document tree and <see cref="ExtractionDocumentMarkdown"/>
/// renders it back: each construct keeps its meaning in the tree, and rendering then reading gives the same tree.
/// </summary>
public class DocumentTree_Tests
{
    [Fact]
    public void Headings_and_paragraphs_keep_their_level_and_their_inline_Markdown()
    {
        const string markdown = "# 契約書\n\n本契約は **甲** と [乙](https://example.com) の間で締結される。\n\n### 第1条 ###";

        var elements = Elements(markdown);

        elements.Count.ShouldBe(3);
        Text(elements[0]).ShouldBe(("契約書", ParseElementRoles.Heading, 1));
        Text(elements[1]).ShouldBe(("本契約は **甲** と [乙](https://example.com) の間で締結される。", null, null));
        Text(elements[2]).ShouldBe(("第1条", ParseElementRoles.Heading, 3));
        Render(markdown).ShouldBe("# 契約書\n\n本契約は **甲** と [乙](https://example.com) の間で締結される。\n\n### 第1条");
    }

    [Fact]
    public void A_nested_list_keeps_each_items_depth_and_marker()
    {
        const string markdown = "1. 原本は保管する\n2. 登録時に選ぶ\n   - 自動で分類される\n   - 確認待ちになる\n3. 以上";

        var items = Elements(markdown).Cast<ExtractionDocumentTextElement>().ToList();

        items.ShouldAllBe(item => item.Role == ParseElementRoles.ListItem);
        items.Select(item => ((TextContent)item.Content!).Text).ShouldBe(["原本は保管する", "登録時に選ぶ", "自動で分類される", "確認待ちになる", "以上"]);
        items.Select(item => item.Content!.AdditionalProperties![ParseContentProperties.ListLevel]).ShouldBe([0, 0, 1, 1, 0]);
        items.Select(item => item.Content!.AdditionalProperties![ParseContentProperties.ListMarker]).ShouldBe(["1.", "2.", "-", "-", "3."]);
        Render(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void A_pipe_table_keeps_its_header_and_cells_with_pipes_and_line_breaks()
    {
        const string markdown = "| 品名 | 金額 |\n| --- | --- |\n| A\\|B | 1<br>2 |\n| 合計 |  |";

        var table = Elements(markdown).Single().ShouldBeOfType<ExtractionDocumentTableElement>();

        table.Cells.RowCount.ShouldBe(3);
        table.Cells.ColumnCount.ShouldBe(2);
        table.Cells[0, 0]!.Role.ShouldBe(ParseElementRoles.ColumnHeader);
        CellText(table, 1, 0).ShouldBe("A|B");
        CellText(table, 1, 1).ShouldBe("1\n2");
        table.Cells[2, 1]!.Content.ShouldBeNull();
        Render(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void An_html_table_keeps_its_spans_and_renders_as_html_because_a_pipe_table_cannot()
    {
        const string markdown = "<table>\n<tr><th>区分</th><th>品名</th></tr>\n<tr><td rowspan=\"2\">保守</td><td>システム保守</td></tr>\n<tr><td>障害対応</td></tr>\n<tr><td colspan=\"2\">合計 &amp; 税</td></tr>\n</table>";

        var table = Elements(markdown).Single().ShouldBeOfType<ExtractionDocumentTableElement>();

        table.Cells.RowCount.ShouldBe(4);
        table.Cells[1, 0]!.RowSpan.ShouldBe(2);
        table.Cells[2, 0].ShouldBeNull();
        CellText(table, 2, 1).ShouldBe("障害対応");
        table.Cells[3, 0]!.ColumnSpan.ShouldBe(2);
        CellText(table, 3, 0).ShouldBe("合計 & 税");
        Render(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void An_html_cell_keeps_the_angle_brackets_its_entities_encode()
    {
        var table = Elements("<table><tr><th>条件</th><th>注</th></tr><tr><td>p &lt; 0.05, n &gt; 30</td><td><b>有意</b></td></tr></table>")
            .Single().ShouldBeOfType<ExtractionDocumentTableElement>();

        CellText(table, 1, 0).ShouldBe("p < 0.05, n > 30");
        CellText(table, 1, 1).ShouldBe("有意");
    }

    [Fact]
    public void An_html_table_without_spans_renders_as_a_pipe_table()
    {
        Render("<table><tr><th>a</th><th>b</th></tr><tr><td>1</td><td>2</td></tr></table>")
            .ShouldBe("| a | b |\n| --- | --- |\n| 1 | 2 |");
    }

    [Fact]
    public void A_figure_becomes_an_image_whose_caption_is_its_alternative_text()
    {
        const string markdown = "前文\n\n<figure id=\"fig-1\">\n<figcaption>表1 売上</figcaption>\n\n| a | b |\n| --- | --- |\n\n</figure>\n\n後文";

        var document = MarkdownDocumentReader.Read(markdown, "doc");
        var elements = document.Sections.Single().Elements;

        var image = elements[1].ShouldBeOfType<ExtractionDocumentImageElement>();
        image.Text.ShouldBe("| a | b |\n| --- | --- |");
        image.AlternativeText.ShouldBe("表1 売上");
        ExtractionDocumentMarkdown.Render(document).ShouldBe(markdown);
    }

    [Fact]
    public void Figures_are_numbered_in_reading_order_whatever_they_were_numbered_before()
    {
        // Azure's figures carry no id, a re-read Markdown may carry stale ones: the renderer numbers them afresh.
        const string markdown = "<figure>\n\na\n\n</figure>\n\n本文\n\n<figure id=\"fig-7\">\n\nb\n\n</figure>";

        Render(markdown).ShouldBe("<figure id=\"fig-1\">\n\na\n\n</figure>\n\n本文\n\n<figure id=\"fig-2\">\n\nb\n\n</figure>");
    }

    [Fact]
    public void A_figure_in_a_code_block_is_code()
    {
        // An HTML sample: the tags are code, so no figure is made of them and they are written as they are.
        const string markdown = "```html\n<figure>\n  <img src=\"a.png\">\n</figure>\n```";

        Elements(markdown).Single().ShouldBeOfType<ExtractionDocumentTextElement>().Role.ShouldBe(ParseElementRoles.Code);
        Render(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void Code_holding_a_fence_line_is_fenced_longer_so_it_ends_where_it_should()
    {
        // A sample that shows a Markdown fence: written inside ``` it would close the block at its own fence line.
        var document = new ExtractionDocument("doc");
        var section = new ExtractionDocumentSection();
        document.Sections.Add(section);
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("```\nx\n```"), ParseElementRoles.Code));
        section.Elements.Add(new ExtractionDocumentImageElement(text: "図の文字"));

        var markdown = ExtractionDocumentMarkdown.Render(document);

        markdown.ShouldStartWith("````\n```\nx\n```\n````\n\n");
        FigureMarkup.Contains(markdown).ShouldBeTrue();
        Elements(markdown).Select(e => e.GetType()).ShouldBe([typeof(ExtractionDocumentTextElement), typeof(ExtractionDocumentImageElement)]);
    }

    [Fact]
    public void An_open_figure_tag_never_closed_is_ordinary_text()
    {
        Elements("<figure>\n本文").Single().ShouldBeOfType<ExtractionDocumentTextElement>();
    }

    [Fact]
    public void Text_that_reads_as_a_figure_tag_is_written_escaped_so_no_figure_is_made_of_it()
    {
        // A document's own text with a line that is a tag: an open tag no close follows (which the reader keeps as
        // text) and a list item holding a close tag line.
        var paragraph = Render("<figure>\n本文");
        var document = new ExtractionDocument("doc");
        var section = new ExtractionDocumentSection();
        document.Sections.Add(section);
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("項目\n</figure>"), ParseElementRoles.ListItem));
        var list = ExtractionDocumentMarkdown.Render(document);

        paragraph.ShouldBe("\\<figure>\n本文");
        list.ShouldBe("- 項目\n  \\</figure>");
        FigureMarkup.Contains(paragraph + "\n\n" + list).ShouldBeFalse();
        FigureMarkup.ExtractBodies(paragraph + "\n\n" + list).ShouldBeEmpty();
    }

    [Fact]
    public void Code_quotes_and_what_the_tree_does_not_model_are_kept_as_written()
    {
        const string markdown = "```json\n{ \"a\": 1 }\n```\n\n> 引用文\n> 二行目\n\n<!-- Slide number: 2 -->\n\n---";

        var elements = Elements(markdown).Cast<ExtractionDocumentTextElement>().ToList();

        elements.Select(e => e.Role).ShouldBe([ParseElementRoles.Code, ParseElementRoles.Quote, ParseElementRoles.Markdown, ParseElementRoles.Markdown]);
        elements[0].Content!.AdditionalProperties![ParseContentProperties.CodeLanguage].ShouldBe("json");
        ((TextContent)elements[1].Content!).Text.ShouldBe("引用文\n二行目");
        Render(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void An_indented_block_is_kept_as_written_not_read_as_code()
    {
        // The second paragraph of a footnote definition, as the DOCX engine writes it: CommonMark alone reads the
        // indented line as code, which would come back fenced and detached from its definition.
        const string markdown = "Body[^fn2]\n\n[^fn2]: First note paragraph.\n\n    Second note paragraph.";

        var elements = Elements(markdown).Cast<ExtractionDocumentTextElement>().ToList();

        elements[2].Role.ShouldBe(ParseElementRoles.Markdown);
        Render(markdown).ShouldBe(markdown);
    }

    [Fact]
    public void Read_into_a_page_refers_every_element_to_it()
    {
        var document = new ExtractionDocument("doc");
        var page = new ExtractionDocumentSourcePage("page-3", 3);
        document.SourcePages.Add(page);
        var section = new ExtractionDocumentSection();
        document.Sections.Add(section);

        MarkdownDocumentReader.ReadInto(section, "## 見出し\n\n本文\n\n| a |\n| --- |\n| 1 |\n\n<figure>\n\n図\n\n</figure>", page);

        section.Elements.Count.ShouldBe(4);
        section.Elements[3].ShouldBeOfType<ExtractionDocumentImageElement>();
        section.Elements.ShouldAllBe(element => element.Sources.Single().Page == page);
    }

    [Fact]
    public void Page_headers_and_footers_stay_in_the_tree_and_out_of_the_Markdown()
    {
        var document = new ExtractionDocument("doc");
        var section = new ExtractionDocumentSection();
        document.Sections.Add(section);
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("社外秘"), ParseElementRoles.PageHeader));
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("本文")));
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("- 1 -"), ParseElementRoles.PageFooter));

        ExtractionDocumentMarkdown.Render(document).ShouldBe("本文");
    }

    private static string Render(string markdown) => ExtractionDocumentMarkdown.Render(MarkdownDocumentReader.Read(markdown, "doc"));

    private static System.Collections.Generic.List<ExtractionDocumentElement> Elements(string markdown)
        => MarkdownDocumentReader.Read(markdown, "doc").Sections.Single().Elements;

    private static (string Text, string? Role, int? Level) Text(ExtractionDocumentElement element)
    {
        var text = element.ShouldBeOfType<ExtractionDocumentTextElement>();
        return (((TextContent)text.Content!).Text, text.Role, text.HeadingLevel);
    }

    private static string CellText(ExtractionDocumentTableElement table, int row, int column)
        => ((TextContent)table.Cells[row, column]!.Content!.Content!).Text;
}

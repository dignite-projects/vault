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
    public void An_html_table_without_spans_renders_as_a_pipe_table()
    {
        Render("<table><tr><th>a</th><th>b</th></tr><tr><td>1</td><td>2</td></tr></table>")
            .ShouldBe("| a | b |\n| --- | --- |\n| 1 | 2 |");
    }

    [Fact]
    public void A_figure_transcription_becomes_an_image_on_its_page()
    {
        const string markdown = "前文\n\n*[Image OCR p:2]*\n| a | b |\n| --- | --- |\n*[End OCR]*\n\n後文";

        var document = MarkdownDocumentReader.Read(markdown, "doc");
        var elements = document.Sections.Single().Elements;

        var image = elements[1].ShouldBeOfType<ExtractionDocumentImageElement>();
        image.Text.ShouldBe("| a | b |\n| --- | --- |");
        image.Sources.Single().Page.PageNumber.ShouldBe(2);
        document.SourcePages.Single().PageNumber.ShouldBe(2);
        ExtractionDocumentMarkdown.Render(document).ShouldBe(markdown);
    }

    [Fact]
    public void An_open_figure_marker_never_closed_is_ordinary_text()
    {
        Elements("*[Image OCR]*\n本文").Single().ShouldBeOfType<ExtractionDocumentTextElement>();
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

        MarkdownDocumentReader.ReadInto(document, section, "## 見出し\n\n本文\n\n| a |\n| --- |\n| 1 |", page);

        section.Elements.Count.ShouldBe(3);
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

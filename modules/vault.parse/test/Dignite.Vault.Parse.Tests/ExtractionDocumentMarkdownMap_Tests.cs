using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// <see cref="ExtractionDocumentMarkdown.Map"/> renders exactly what <see cref="ExtractionDocumentMarkdown.Render"/> does
/// and records where each element landed, so a range of the stored Markdown leads back to the page regions it came from.
/// </summary>
public class ExtractionDocumentMarkdownMap_Tests
{
    private static readonly ExtractionDocumentSourcePage Page1 = new("p1", 1);

    [Theory]
    [InlineData("# 契約書\n\n本契約は **甲** と乙の間で締結される。\n\n### 第1条")]
    [InlineData("1. 原本は保管する\n2. 登録時に選ぶ\n   - 自動で分類される\n   - 確認待ちになる\n3. 以上")]
    [InlineData("| 品名 | 金額 |\n| --- | --- |\n| A\\|B | 1<br>2 |\n| 合計 |  |")]
    [InlineData("<table>\n<tr><th>区分</th><th>品名</th></tr>\n<tr><td rowspan=\"2\">保守</td><td>システム保守</td></tr>\n<tr><td>障害対応</td></tr>\n<tr><td colspan=\"2\">合計 &amp; 税</td></tr>\n</table>")]
    [InlineData("前文\n\n> 引用\n>\n> 続き\n\n```csharp\nvar x = 1;\n```\n\n- a\n  続き行\n- b")]
    public void The_map_renders_what_Render_renders(string markdown)
    {
        var tree = MarkdownDocumentReader.Read(markdown, "doc");

        ExtractionDocumentMarkdown.Map(tree).Markdown.ShouldBe(ExtractionDocumentMarkdown.Render(tree));
    }

    [Fact]
    public void Each_paragraph_heading_and_list_item_is_found_at_its_range()
    {
        var document = Document(
            Text("請求書", ParseElementRoles.Heading, 1, Box(0, 0)),
            Text("請求日 2026年11月1日", null, null, Box(0, 1)),
            ListItem("保守費", Box(0, 2)),
            ListItem("障害対応", Box(0, 3)));

        var map = ExtractionDocumentMarkdown.Map(document);

        map.Markdown.ShouldBe("# 請求書\n\n請求日 2026年11月1日\n\n- 保守費\n- 障害対応");
        Covered(map, "請求日 2026年11月1日").ShouldBe([Box(0, 1)]);
        Covered(map, "2026年11月1日").ShouldBe([Box(0, 1)]);
        Covered(map, "障害対応").ShouldBe([Box(0, 3)]);
        Covered(map, "# 請求書").ShouldBe([Box(0, 0)]);
    }

    [Fact]
    public void A_range_inside_a_table_leads_to_the_cell_not_the_whole_table()
    {
        var table = new ExtractionDocumentTableElement(null, 2, 2);
        table.Sources.Add(Box(10, 10));
        table.Cells[0, 0] = Cell("品名", Box(1, 1));
        table.Cells[0, 1] = Cell("金額", Box(2, 1));
        table.Cells[1, 0] = Cell("保守", Box(1, 2));
        table.Cells[1, 1] = Cell("12,000", Box(2, 2));
        var map = ExtractionDocumentMarkdown.Map(Document(table));

        map.Markdown.ShouldBe("| 品名 | 金額 |\n| --- | --- |\n| 保守 | 12,000 |");
        Covered(map, "12,000").ShouldBe([Box(2, 2)]);

        // A row quoted whole reaches every cell in it, still not the table.
        Covered(map, "保守 | 12,000").ShouldBe([Box(1, 2), Box(2, 2)]);

        // Only the separator line: no cell is there, so the table answers.
        Covered(map, "| --- | --- |").ShouldBe([Box(10, 10)]);
    }

    [Fact]
    public void A_cell_of_an_html_table_is_found_through_its_encoded_text()
    {
        var table = new ExtractionDocumentTableElement(null, 1, 2);
        table.Cells[0, 0] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("合計 & 税")), 1, 2, null);
        table.Cells[0, 0]!.Sources.Add(Box(5, 5));
        var map = ExtractionDocumentMarkdown.Map(Document(table));

        map.Markdown.ShouldBe("<table>\n<tr><td colspan=\"2\">合計 &amp; 税</td></tr>\n</table>");
        Covered(map, "合計 &amp; 税").ShouldBe([Box(5, 5)]);
    }

    [Fact]
    public void Text_outside_every_rendered_element_leads_nowhere()
    {
        var map = ExtractionDocumentMarkdown.Map(Document(
            Text("前", null, null, Box(0, 0)),
            Text("後", null, null, Box(0, 1))));

        map.Markdown.ShouldBe("前\n\n後");
        map.SourcesOf(1, 2).ShouldBeEmpty();
    }

    private static List<ExtractionDocumentSourceReference> Covered(ExtractionDocumentMarkdownMap map, string text)
    {
        var offset = map.Markdown.IndexOf(text, System.StringComparison.Ordinal);
        offset.ShouldBeGreaterThanOrEqualTo(0);
        return map.SourcesOf(offset, text.Length).ToList();
    }

    private static ExtractionDocument Document(params ExtractionDocumentElement[] elements)
    {
        var document = new ExtractionDocument("doc");
        document.SourcePages.Add(Page1);
        var section = new ExtractionDocumentSection();
        section.Elements.AddRange(elements);
        document.Sections.Add(section);
        return document;
    }

    private static ExtractionDocumentTextElement Text(string text, string? role, int? level, ExtractionDocumentSourceReference source)
    {
        var element = new ExtractionDocumentTextElement(new TextContent(text), role) { HeadingLevel = level };
        element.Sources.Add(source);
        return element;
    }

    private static ExtractionDocumentTextElement ListItem(string text, ExtractionDocumentSourceReference source)
        => Text(text, ParseElementRoles.ListItem, null, source);

    private static ExtractionDocumentTableCell Cell(string text, ExtractionDocumentSourceReference source)
    {
        var cell = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent(text)), 1, 1, null);
        cell.Sources.Add(source);
        return cell;
    }

    // One cached reference per grid square, so equality is reference equality and the assertions read as positions.
    private static readonly Dictionary<(int, int), ExtractionDocumentSourceReference> Boxes = [];

    private static ExtractionDocumentSourceReference Box(int column, int row)
    {
        if (!Boxes.TryGetValue((column, row), out var box))
        {
            box = new ExtractionDocumentSourceReference(Page1,
            [
                new ExtractionDocumentPoint(column * 10, row * 10),
                new ExtractionDocumentPoint(column * 10 + 10, row * 10),
                new ExtractionDocumentPoint(column * 10 + 10, row * 10 + 10),
                new ExtractionDocumentPoint(column * 10, row * 10 + 10)
            ]);
            Boxes[(column, row)] = box;
        }

        return box;
    }
}

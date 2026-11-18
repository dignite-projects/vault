using System.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// <see cref="ExtractionDocumentPages"/> cuts a page range out of a parsed tree as if those pages had been parsed
/// alone, and leads ranges of the rendered Markdown to the pages they were read from.
/// </summary>
public class ExtractionDocumentPages_Tests
{
    [Fact]
    public void A_page_range_keeps_its_pages_renumbered_from_one_with_their_regions()
    {
        var document = ThreePages();

        var selected = ExtractionDocumentPages.Select(document, 2, 3);

        selected.SourcePages.Select(page => (page.Identifier, page.PageNumber)).ShouldBe([("p2", 2 - 1), ("p3", 3 - 1)]);
        ExtractionDocumentMarkdown.Render(selected).ShouldBe("納品書\n\n品名 数量\n\n契約書");
        var paragraph = selected.Sections[0].Elements[0];
        paragraph.Sources.Single().Page.PageNumber.ShouldBe(1);
        paragraph.Sources.Single().Polygon!.First().ShouldBe(new ExtractionDocumentPoint(10, 20));
        selected.GetProvider().ShouldBe("pdf");
    }

    [Fact]
    public void Each_range_of_the_Markdown_counts_its_text_on_the_pages_its_elements_were_read_from()
    {
        var map = ExtractionDocumentMarkdown.Map(ThreePages());
        var second = map.Markdown.IndexOf("納品書", System.StringComparison.Ordinal);
        var middle = map.Markdown.IndexOf("品名", System.StringComparison.Ordinal);
        var third = map.Markdown.IndexOf("契約書", System.StringComparison.Ordinal);

        var text = ExtractionDocumentPages.TextPerPage(map, [(0, second), (second, middle), (middle, third), (third, map.Markdown.Length)]);

        text[0].ShouldBe(new System.Collections.Generic.Dictionary<int, int> { [1] = "請求書".Length });
        text[1].ShouldBe(new System.Collections.Generic.Dictionary<int, int> { [2] = "納品書".Length });
        text[2].ShouldBe(new System.Collections.Generic.Dictionary<int, int> { [2] = "品名 数量".Length });
        text[3].ShouldBe(new System.Collections.Generic.Dictionary<int, int> { [3] = "契約書".Length });
    }

    [Fact]
    public void A_table_counts_once_through_its_cells()
    {
        var document = new ExtractionDocument("doc");
        var page = new ExtractionDocumentSourcePage("p1", 1);
        document.SourcePages.Add(page);
        var table = new ExtractionDocumentTableElement(null, 1, 2);
        table.Sources.Add(new ExtractionDocumentSourceReference(page));
        table.Cells[0, 0] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("品名")), 1, 1, null);
        table.Cells[0, 0]!.Sources.Add(new ExtractionDocumentSourceReference(page));
        table.Cells[0, 1] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("金額")), 1, 1, null);
        table.Cells[0, 1]!.Sources.Add(new ExtractionDocumentSourceReference(page));
        var section = new ExtractionDocumentSection();
        section.Elements.Add(table);
        document.Sections.Add(section);
        var map = ExtractionDocumentMarkdown.Map(document);

        ExtractionDocumentPages.TextPerPage(map, [(0, map.Markdown.Length)])[0][1].ShouldBe("品名".Length + "金額".Length);

        // The cells are one range of the page: only table markup lies between them.
        var range = ExtractionDocumentPages.Spans(map)[1].ShouldHaveSingleItem();
        map.Markdown.Substring(range.Offset, range.Length).ShouldStartWith("品名");
        map.Markdown.Substring(range.Offset, range.Length).ShouldEndWith("金額");
    }

    [Fact]
    public void Each_page_lists_the_ranges_of_the_Markdown_read_from_it()
    {
        var map = ExtractionDocumentMarkdown.Map(ThreePages());
        string Text((int Offset, int Length) span) => map.Markdown.Substring(span.Offset, span.Length);

        var pages = ExtractionDocumentPages.Spans(map);

        pages.Keys.ShouldBe([1, 2, 3]);
        pages[1].Select(Text).ShouldBe(["請求書"]);
        pages[2].Select(Text).ShouldBe(["納品書\n\n品名 数量"]);
        pages[3].Select(Text).ShouldBe(["契約書"]);
    }

    /// <summary>An invoice on page 1, a delivery note on page 2, a contract on page 3: one section per page.</summary>
    private static ExtractionDocument ThreePages()
    {
        var document = new ExtractionDocument("bundle");
        document.SetProvider("pdf");
        string[][] texts = [["請求書"], ["納品書", "品名 数量"], ["契約書"]];
        for (var number = 1; number <= 3; number++)
        {
            var page = new ExtractionDocumentSourcePage("p" + number, number);
            document.SourcePages.Add(page);
            var section = new ExtractionDocumentSection();
            section.Sources.Add(new ExtractionDocumentSourceReference(page));
            foreach (var text in texts[number - 1])
            {
                var element = new ExtractionDocumentTextElement(new TextContent(text));
                element.Sources.Add(new ExtractionDocumentSourceReference(page,
                [
                    new ExtractionDocumentPoint(10, 20), new ExtractionDocumentPoint(100, 20),
                    new ExtractionDocumentPoint(100, 40), new ExtractionDocumentPoint(10, 40)
                ]));
                section.Elements.Add(element);
            }

            document.Sections.Add(section);
        }

        return document;
    }
}

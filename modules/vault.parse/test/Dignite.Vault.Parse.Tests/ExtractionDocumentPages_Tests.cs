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

    [Fact]
    public void A_region_is_placed_as_fractions_of_its_page_from_the_top_left_corner()
    {
        // A4 in PDF points from the bottom-left corner, as the PDF engine writes it; a text 100 pt from the left edge and
        // 200 pt below the top edge, 300 by 40 pt.
        var page = new ExtractionDocumentSourcePage("p1", 1, new ExtractionDocumentPageDimensions(600, 800),
            ExtractionDocumentCoordinateUnit.Point, ExtractionDocumentCoordinateOrigin.BottomLeft);
        var map = ExtractionDocumentMarkdown.Map(OneElement(page, Rectangle(100, 560, 400, 600)));

        var region = ExtractionDocumentPages.Regions(map).ShouldHaveSingleItem();

        map.Markdown.Substring(region.Offset, region.Length).ShouldBe("請求書");
        region.PageNumber.ShouldBe(1);
        region.Left.ShouldBe(100d / 600, 0.0001);
        region.Top.ShouldBe(200d / 800, 0.0001);
        region.Width.ShouldBe(300d / 600, 0.0001);
        region.Height.ShouldBe(40d / 800, 0.0001);
    }

    [Fact]
    public void A_region_is_handed_out_to_four_places_after_the_frame_is_flipped()
    {
        // 1 - 0.92875 is 0.07125000000000004 in binary floating point; the box must not carry that noise.
        var page = new ExtractionDocumentSourcePage("p1", 1, new ExtractionDocumentPageDimensions(600, 800),
            ExtractionDocumentCoordinateUnit.Point, ExtractionDocumentCoordinateOrigin.BottomLeft);

        var region = ExtractionDocumentPages.Regions(ExtractionDocumentMarkdown.Map(OneElement(page, Rectangle(71.1, 735, 526.3, 743.1))))
            .ShouldHaveSingleItem();

        foreach (var value in new[] { region.Left, region.Top, region.Width, region.Height })
        {
            value.ShouldBe(System.Math.Round(value, 4));
            value.ToString(System.Globalization.CultureInfo.InvariantCulture).Length.ShouldBeLessThanOrEqualTo(6);
        }
    }

    [Fact]
    public void A_top_left_frame_and_normalized_coordinates_place_the_same_box()
    {
        var pixels = new ExtractionDocumentSourcePage("p1", 1, new ExtractionDocumentPageDimensions(1000, 2000),
            ExtractionDocumentCoordinateUnit.Pixel, ExtractionDocumentCoordinateOrigin.TopLeft);
        var normalized = new ExtractionDocumentSourcePage("p1", 1, null,
            ExtractionDocumentCoordinateUnit.Normalized, ExtractionDocumentCoordinateOrigin.TopLeft);

        var fromPixels = ExtractionDocumentPages.Regions(ExtractionDocumentMarkdown.Map(OneElement(pixels, Rectangle(100, 500, 600, 700))));
        var fromFractions = ExtractionDocumentPages.Regions(ExtractionDocumentMarkdown.Map(OneElement(normalized, Rectangle(0.1, 0.25, 0.6, 0.35))));

        fromPixels.ShouldHaveSingleItem().ShouldBe(fromFractions.ShouldHaveSingleItem());
        fromPixels[0].Top.ShouldBe(0.25, 0.0001);
        fromPixels[0].Height.ShouldBe(0.1, 0.0001);
    }

    [Fact]
    public void A_region_that_cannot_be_placed_is_left_out()
    {
        // An OCR page names only the page; an engine without a page size gives coordinates in nothing known.
        var pageOnly = new ExtractionDocumentSourcePage("p1", 1, new ExtractionDocumentPageDimensions(600, 800),
            ExtractionDocumentCoordinateUnit.Point, ExtractionDocumentCoordinateOrigin.BottomLeft);
        var noSize = new ExtractionDocumentSourcePage("p1", 1, null,
            ExtractionDocumentCoordinateUnit.Point, ExtractionDocumentCoordinateOrigin.BottomLeft);

        ExtractionDocumentPages.Regions(ExtractionDocumentMarkdown.Map(OneElement(pageOnly, null))).ShouldBeEmpty();
        ExtractionDocumentPages.Regions(ExtractionDocumentMarkdown.Map(OneElement(noSize, Rectangle(100, 560, 400, 600)))).ShouldBeEmpty();
        ExtractionDocumentPages.Regions(ExtractionDocumentMarkdown.Map(ThreePages())).ShouldBeEmpty();
    }

    [Fact]
    public void A_table_is_placed_through_its_cells()
    {
        var page = new ExtractionDocumentSourcePage("p1", 1, new ExtractionDocumentPageDimensions(600, 800),
            ExtractionDocumentCoordinateUnit.Point, ExtractionDocumentCoordinateOrigin.BottomLeft);
        var table = new ExtractionDocumentTableElement(null, 1, 2);
        table.Sources.Add(new ExtractionDocumentSourceReference(page, Rectangle(0, 0, 600, 800)));
        table.Cells[0, 0] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("品名")), 1, 1, null);
        table.Cells[0, 0]!.Sources.Add(new ExtractionDocumentSourceReference(page, Rectangle(0, 700, 300, 800)));
        table.Cells[0, 1] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("金額")), 1, 1, null);
        table.Cells[0, 1]!.Sources.Add(new ExtractionDocumentSourceReference(page, Rectangle(300, 700, 600, 800)));
        var document = new ExtractionDocument("doc");
        document.SourcePages.Add(page);
        var section = new ExtractionDocumentSection();
        section.Elements.Add(table);
        document.Sections.Add(section);
        var map = ExtractionDocumentMarkdown.Map(document);

        var regions = ExtractionDocumentPages.Regions(map);

        regions.Select(region => map.Markdown.Substring(region.Offset, region.Length)).ShouldBe(["品名", "金額"]);
        regions.Select(region => region.Left).ShouldBe([0, 0.5]);
    }

    private static ExtractionDocument OneElement(ExtractionDocumentSourcePage page, ExtractionDocumentPoint[]? polygon)
    {
        var document = new ExtractionDocument("doc");
        document.SourcePages.Add(page);
        var element = new ExtractionDocumentTextElement(new TextContent("請求書"));
        element.Sources.Add(polygon is null ? new ExtractionDocumentSourceReference(page) : new ExtractionDocumentSourceReference(page, polygon));
        var section = new ExtractionDocumentSection();
        section.Elements.Add(element);
        document.Sections.Add(section);
        return document;
    }

    private static ExtractionDocumentPoint[] Rectangle(double left, double low, double right, double high) =>
    [
        new(left, low), new(right, low), new(right, high), new(left, high)
    ];

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

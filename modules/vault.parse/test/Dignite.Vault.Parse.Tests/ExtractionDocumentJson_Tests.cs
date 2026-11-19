using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse;

public class ExtractionDocumentJson_Tests
{
    [Fact]
    public void A_tree_survives_json_with_its_pages_regions_spans_and_properties()
    {
        const string markdown = "# Title\n\n- one\n  - two\n\n<table>\n<tr><th colspan=\"2\">Head</th></tr>\n<tr><td>a</td><td>b</td></tr>\n</table>\n\n"
            + "<figure>\n<figcaption>図1 領収書</figcaption>\n\nscan\n\n</figure>\n\nBody with **bold**.";
        var document = MarkdownDocumentReader.Read(markdown, "doc").SetProvider("Test").MarkIncomplete("one page lost");
        var page = new ExtractionDocumentSourcePage("scan-1", 1, new ExtractionDocumentPageDimensions(595, 842),
            ExtractionDocumentCoordinateUnit.Point, ExtractionDocumentCoordinateOrigin.BottomLeft);
        document.SourcePages.Insert(0, page);
        var heading = document.Sections[0].Elements[0];
        heading.Sources.Add(new ExtractionDocumentSourceReference(page, [new(10, 800), new(300, 800), new(300, 780), new(10, 780)]));

        var json = ExtractionDocumentJson.Serialize(document);
        var read = ExtractionDocumentJson.Deserialize(json);

        ExtractionDocumentMarkdown.Render(read).ShouldBe(ExtractionDocumentMarkdown.Render(document));
        read.GetProvider().ShouldBe("Test");
        read.GetIncompleteReason().ShouldBe("one page lost");
        var readPage = read.SourcePages.First(p => p.Identifier == "scan-1");
        readPage.Dimensions.ShouldBe(new ExtractionDocumentPageDimensions(595, 842));
        readPage.CoordinateOrigin.ShouldBe(ExtractionDocumentCoordinateOrigin.BottomLeft);
        var readHeading = (ExtractionDocumentTextElement)read.Sections[0].Elements[0];
        readHeading.HeadingLevel.ShouldBe(1);
        readHeading.Sources.ShouldHaveSingleItem().Page.ShouldBeSameAs(readPage);
        readHeading.Sources[0].Polygon.ShouldBe(heading.Sources[0].Polygon);
        var table = read.Sections[0].Elements.OfType<ExtractionDocumentTableElement>().ShouldHaveSingleItem();
        table.Cells[0, 0]!.ColumnSpan.ShouldBe(2);
        table.Cells[0, 0]!.Role.ShouldBe(ParseElementRoles.ColumnHeader);
        var image = read.Sections[0].Elements.OfType<ExtractionDocumentImageElement>().ShouldHaveSingleItem();
        image.Text.ShouldBe("scan");
        image.AlternativeText.ShouldBe("図1 領収書");
        ExtractionDocumentJson.Serialize(read).ShouldBe(json);
    }

    [Fact]
    public void Refuses_another_version()
        => Should.Throw<JsonException>(() => ExtractionDocumentJson.Deserialize("{\"version\":2}"));
}

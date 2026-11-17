using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Layout;
using Dignite.Vault.Parse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using NSubstitute;
using Shouldly;
using Volo.Abp.BlobStoring;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The layout read places each field's Markdown ranges on the page: the regions of the narrowest elements its text was
/// rendered from, while the layout still describes the current Markdown.
/// </summary>
public class DocumentLayoutStore_Tests
{
    private static readonly System.Guid DocumentId = System.Guid.Parse("5a5a5a5a-0000-0000-0000-000000000001");

    [Fact]
    public async Task A_field_source_is_placed_on_the_cell_it_was_quoted_from()
    {
        var (store, markdown) = await StoredAsync();
        var offset = markdown.IndexOf("13,200", System.StringComparison.Ordinal);

        var layout = await ReadAsync(store, markdown, new() { ["total"] = [new DocumentTextSpan(offset, 6)] });

        layout["current"]!.GetValue<bool>().ShouldBeTrue();
        var field = layout["fields"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        field["name"]!.GetValue<string>().ShouldBe("total");
        field["spans"]![0]!["offset"]!.GetValue<int>().ShouldBe(offset);
        var source = field["sources"]!.AsArray().ShouldHaveSingleItem()!;
        source["page"]!.GetValue<string>().ShouldBe("p1");
        source["polygon"]![0]![0]!.GetValue<double>().ShouldBe(300);
    }

    [Fact]
    public async Task Without_a_current_layout_no_field_is_placed()
    {
        var (store, markdown) = await StoredAsync();

        var layout = await ReadAsync(store, markdown + "（訂正）", new() { ["total"] = [new DocumentTextSpan(0, 3)] });

        layout["current"]!.GetValue<bool>().ShouldBeFalse();
        layout["fields"]!.AsArray().ShouldBeEmpty();
    }

    private static async Task<(DocumentLayoutStore Store, string Markdown)> StoredAsync()
    {
        var blobs = Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>();
        byte[]? stored = null;
        await blobs.SaveAsync(Arg.Any<string>(), Arg.Do<Stream>(stream =>
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            stored = copy.ToArray();
        }), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        blobs.GetOrNullAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(stored is null ? null : new MemoryStream(stored)));

        var tree = Invoice();
        var markdown = ExtractionDocumentMarkdown.Render(tree);
        var store = new DocumentLayoutStore(blobs);
        await store.SaveAsync(DocumentId, tree, markdown);
        return (store, markdown);
    }

    private static async Task<JsonObject> ReadAsync(
        DocumentLayoutStore store, string markdown, Dictionary<string, IReadOnlyList<DocumentTextSpan>> sources)
        => JsonNode.Parse((await store.GetAsync(DocumentId, markdown, sources))!)!.AsObject();

    /// <summary>A heading, then a two-by-two table whose cells sit at x = 100 (labels) and x = 300 (amounts).</summary>
    private static ExtractionDocument Invoice()
    {
        var document = new ExtractionDocument("doc");
        var page = new ExtractionDocumentSourcePage("p1", 1);
        document.SourcePages.Add(page);
        var section = new ExtractionDocumentSection();
        var heading = new ExtractionDocumentTextElement(new TextContent("請求書"), ParseElementRoles.Heading) { HeadingLevel = 1 };
        heading.Sources.Add(new ExtractionDocumentSourceReference(page, Box(100, 50)));
        section.Elements.Add(heading);

        var table = new ExtractionDocumentTableElement(null, 2, 2);
        table.Sources.Add(new ExtractionDocumentSourceReference(page, Box(100, 200)));
        string[,] texts = { { "小計", "12,000" }, { "合計", "13,200" } };
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 2; column++)
            {
                var cell = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent(texts[row, column])), 1, 1, null);
                cell.Sources.Add(new ExtractionDocumentSourceReference(page, Box(column == 0 ? 100 : 300, 200 + row * 20)));
                table.Cells[row, column] = cell;
            }
        }

        section.Elements.Add(table);
        document.Sections.Add(section);
        return document;
    }

    private static List<ExtractionDocumentPoint> Box(double x, double y)
        => [new(x, y), new(x + 100, y), new(x + 100, y + 20), new(x, y + 20)];
}

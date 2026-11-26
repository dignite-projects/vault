using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// <see cref="MarkdownExtractionClient"/> reads Markdown a client produced into the document tree: pages it marks
/// with <see cref="PageMarkup"/> become sections referring to their pages, running headers and footers stay out of
/// the text, and anything that is not UTF-8 is refused.
/// </summary>
public class MarkdownExtractionClient_Tests
{
    private readonly MarkdownExtractionClient _engine = new();

    [Theory]
    [InlineData(".md", "application/octet-stream")]
    [InlineData(".markdown", "application/octet-stream")]
    [InlineData("", "text/markdown")]
    [InlineData(".txt", "text/x-markdown")]
    public void Claims_markdown_by_extension_or_media_type(string extension, string mediaType)
    {
        _engine.CanExtract(extension, mediaType).ShouldBeTrue();
    }

    [Fact]
    public void Leaves_other_files_to_their_engines()
    {
        _engine.CanExtract(".txt", "text/plain").ShouldBeFalse();
        _engine.CanExtract(".pdf", "application/pdf").ShouldBeFalse();
    }

    [Fact]
    public async Task Page_breaks_give_one_section_per_page_and_the_rendered_markdown_drops_them()
    {
        const string markdown = "# 請求書\n\n請求番号 INV-1\n\n<!-- PageBreak -->\n\n合計 1,000円\n";

        var result = await ParseAsync(markdown);

        result.Document.SourcePages.Select(page => page.PageNumber).ShouldBe([1, 2]);
        result.Document.Sections.Count.ShouldBe(2);
        PagesOf(result.Document.Sections[0]).ShouldAllBe(page => page == 1);
        PagesOf(result.Document.Sections[1]).ShouldAllBe(page => page == 2);
        result.Markdown.ShouldBe("# 請求書\n\n請求番号 INV-1\n\n合計 1,000円");
        result.ProviderName.ShouldBe(MarkdownExtractionClient.ProviderIdentifier);
    }

    [Fact]
    public async Task Markdown_without_page_breaks_has_no_pages()
    {
        var result = await ParseAsync("# Memo\n\nOne paragraph.");

        result.Document.SourcePages.ShouldBeEmpty();
        result.Document.Sections.Single().Elements
            .SelectMany(element => element.Sources)
            .ShouldBeEmpty();
        result.Markdown.ShouldBe("# Memo\n\nOne paragraph.");
    }

    [Fact]
    public async Task Running_headers_and_footers_stay_in_the_tree_and_out_of_the_text_and_page_numbers_go()
    {
        const string markdown =
            "<!-- PageHeader=\"株式会社サンプル &amp; 社外秘\" -->\n\n本文一\n\n<!-- PageNumber=\"1\" -->\n<!-- PageFooter=\"1 / 2\" -->\n"
            + "<!-- PageBreak -->\n"
            + "<!-- PageHeader=\"株式会社サンプル &amp; 社外秘\" -->\n\n本文二";

        var result = await ParseAsync(markdown);

        result.Markdown.ShouldBe("本文一\n\n本文二");
        var first = result.Document.Sections[0].Elements.OfType<ExtractionDocumentTextElement>().ToList();
        first.First().Role.ShouldBe(ParseElementRoles.PageHeader);
        ((TextContent)first.First().Content!).Text.ShouldBe("株式会社サンプル & 社外秘");
        first.Last().Role.ShouldBe(ParseElementRoles.PageFooter);
        first.ShouldNotContain(element => ((TextContent)element.Content!).Text.Contains("PageNumber"));
    }

    [Fact]
    public async Task Page_metadata_in_a_code_block_or_indented_as_code_is_text()
    {
        const string markdown = "Example:\n\n```html\n<!-- PageBreak -->\n```\n\n    <!-- PageBreak -->";

        var result = await ParseAsync(markdown);

        result.Document.SourcePages.ShouldBeEmpty();
        result.Markdown.ShouldContain("```html\n<!-- PageBreak -->\n```");
    }

    [Fact]
    public async Task A_figure_on_a_page_keeps_its_page()
    {
        const string markdown = "前文\n\n<!-- PageBreak -->\n\n<figure>\n<figcaption>領収書</figcaption>\n\n金額 500円\n\n</figure>";

        var result = await ParseAsync(markdown);

        var image = result.Document.Sections[1].Elements.OfType<ExtractionDocumentImageElement>().Single();
        image.Text.ShouldBe("金額 500円");
        image.Sources.Single().Page.PageNumber.ShouldBe(2);
    }

    [Fact]
    public async Task A_page_break_inside_a_figure_takes_effect_after_it()
    {
        const string markdown = "前文\n\n<figure>\n<figcaption>領収書</figcaption>\n\n金額 500円\n\n<!-- PageBreak -->\n\n但し書き\n\n</figure>\n\n後文\n\n<!-- PageBreak -->\n\n三頁目";

        var result = await ParseAsync(markdown);

        result.Document.Sections.Count.ShouldBe(3);
        var image = result.Document.Sections[0].Elements.OfType<ExtractionDocumentImageElement>().Single();
        image.Text.ShouldBe("金額 500円\n\n但し書き");
        image.AlternativeText.ShouldBe("領収書");
        result.Markdown.ShouldNotContain("\\<figure");
        result.Markdown.ShouldNotContain("\\</figure>");
        PagesOf(result.Document.Sections[2]).ShouldAllBe(page => page == 3);
    }

    [Fact]
    public async Task A_byte_order_mark_is_skipped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("本文")).ToArray();

        var result = await _engine.ParseAsync(new MemoryStream(bytes), "client.md", "text/markdown");

        result.Markdown.ShouldBe("本文");
    }

    [Fact]
    public async Task A_file_that_is_not_utf8_is_refused_as_invalid_data()
    {
        // "本文" in Shift_JIS: not a valid UTF-8 sequence.
        var bytes = new byte[] { 0x96, 0x7B, 0x95, 0xB6 };

        await Should.ThrowAsync<InvalidDataException>(
            () => _engine.ParseAsync(new MemoryStream(bytes), "client.md", "text/markdown"));
    }

    [Fact]
    public void Is_exposed_as_an_engine()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(MarkdownExtractionClient))
            .ShouldContain(typeof(ParseExtractionClient));
        typeof(ITransientDependency).IsAssignableFrom(typeof(MarkdownExtractionClient)).ShouldBeTrue();
    }

    private Task<ParseResult> ParseAsync(string markdown)
        => _engine.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(markdown)), "client.md", "text/markdown");

    private static int?[] PagesOf(ExtractionDocumentSection section)
        => section.Elements.SelectMany(element => element.Sources).Select(source => source.Page.PageNumber).ToArray();
}

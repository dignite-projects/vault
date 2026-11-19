using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// <see cref="FigureMarkup"/>: a figure written the way Azure Document Intelligence's layout Markdown writes one
/// (<c>&lt;figure&gt;</c>, an optional <c>&lt;figcaption&gt;</c>, the content, <c>&lt;/figure&gt;</c>), read back, and
/// taken apart for a sub-document's seed. Only whole trimmed lines are tags.
/// </summary>
public class FigureMarkup_Tests
{
    [Fact]
    public void Writes_a_figure_as_Azure_Document_Intelligence_does()
    {
        // The exact strings are the egress format; pin them so they cannot drift silently.
        FigureMarkup.Write("Values\n300", "Figure 2 This is a figure")
            .ShouldBe("<figure>\n<figcaption>Figure 2 This is a figure</figcaption>\n\nValues\n300\n\n</figure>");
        FigureMarkup.Write("inv", caption: null).ShouldBe("<figure>\n\ninv\n\n</figure>");
        FigureMarkup.Write(null, "図1").ShouldBe("<figure>\n<figcaption>図1</figcaption>\n</figure>");
    }

    [Fact]
    public void A_caption_is_one_HTML_encoded_line()
    {
        // The caption sits inside the HTML block: markup in it is text, and a blank line in it would end the block.
        var written = FigureMarkup.Write("x", "a <b> & c\n\n d");

        written.ShouldStartWith("<figure>\n<figcaption>a &lt;b&gt; &amp; c d</figcaption>\n");
        FigureMarkup.Split(written).Single().Caption.ShouldBe("a <b> & c d");
    }

    [Fact]
    public void Split_reads_a_figure_with_its_caption_and_the_text_around_it()
    {
        var parts = FigureMarkup.Split("intro\n\n" + FigureMarkup.Write("| a |\n| --- |\n| 1 |", "表1") + "\n\noutro").ToList();

        parts.Count.ShouldBe(3);
        parts[0].ShouldBe(new FigureMarkupPart(false, "intro\n", null));
        parts[1].ShouldBe(new FigureMarkupPart(true, "| a |\n| --- |\n| 1 |", "表1"));
        parts[2].ShouldBe(new FigureMarkupPart(false, "\noutro", null));
    }

    [Fact]
    public void Split_reads_Azure_Document_Intelligence_output()
    {
        const string azure = "<figure>\n<figcaption>Figure 2 This is a figure</figcaption>\n\nValues\n300\n200\n\nJan Feb Mar\n\n</figure>\n\nThis is footnote if the figure have.";

        var parts = FigureMarkup.Split(azure).ToList();

        parts[0].ShouldBe(new FigureMarkupPart(true, "Values\n300\n200\n\nJan Feb Mar", "Figure 2 This is a figure"));
        parts[1].Text.ShouldBe("\nThis is footnote if the figure have.");
    }

    [Fact]
    public void An_open_tag_never_closed_is_text()
    {
        FigureMarkup.Split("<figure>\nno end").Single().ShouldBe(new FigureMarkupPart(false, "<figure>\nno end", null));
    }

    [Fact]
    public void A_transcription_cannot_close_or_open_a_figure_of_its_own()
    {
        // A picture whose text holds a tag line: the figure still ends at its own close tag.
        var written = FigureMarkup.Write("a\n</figure>\n<figure>\nb", caption: null);

        written.ShouldBe("<figure>\n\na\n\\</figure>\n\\<figure>\nb\n\n</figure>");
        FigureMarkup.Split(written).Single().Text.ShouldBe("a\n\\</figure>\n\\<figure>\nb");
        FigureMarkup.ExtractBodies(written).ShouldBe("a\n\\</figure>\n\\<figure>\nb");
    }

    [Fact]
    public void Escape_leaves_text_without_a_tag_line_as_it_is()
    {
        const string text = "see <figure> inline\nfigure> results";
        FigureMarkup.Escape(text).ShouldBeSameAs(text);
    }

    [Fact]
    public void Strip_drops_the_tags_and_keeps_the_caption_as_text()
    {
        var marked = "a\n\n" + FigureMarkup.Write("inv", "図1 領収書") + "\n\nb";

        FigureMarkup.Strip(marked).ShouldBe("a\n\n図1 領収書\n\ninv\n\n\nb");
        FigureMarkup.Strip(FigureMarkup.Write("inv", caption: null)).ShouldBe("\ninv\n");
    }

    [Fact]
    public void Strip_returns_text_without_a_figure_unchanged()
    {
        const string plain = "just some prose\nover two lines";
        FigureMarkup.Strip(plain).ShouldBe(plain);

        // A tag mentioned inside a longer line is prose.
        const string midLine = "see <figure> note";
        FigureMarkup.Strip(midLine).ShouldBe(midLine);
    }

    [Fact]
    public void ExtractBodies_keeps_each_figures_content_and_nothing_around_it()
    {
        var markdown = "parent prose\n\n" + FigureMarkup.Write("inv 1", "図1") + "\n\nmore prose\n\n" + FigureMarkup.Write("inv 2", null);

        FigureMarkup.ExtractBodies(markdown).ShouldBe("inv 1\n\ninv 2");
        FigureMarkup.ExtractBodies("just prose").ShouldBe(string.Empty);
    }

    [Fact]
    public void ExtractBodies_keeps_the_rest_of_a_figure_whose_close_tag_is_cut_off()
    {
        FigureMarkup.ExtractBodies("<figure>\n<figcaption>図1</figcaption>\n\ninv\nmore").ShouldBe("inv\nmore");
    }

    [Fact]
    public void Contains_and_the_line_predicates_match_whole_lines_only()
    {
        FigureMarkup.Contains(FigureMarkup.Write("inv", null)).ShouldBeTrue();
        FigureMarkup.Contains("just some prose").ShouldBeFalse();
        FigureMarkup.Contains("see <figure> inline").ShouldBeFalse();
        FigureMarkup.Contains("\\<figure>").ShouldBeFalse();

        FigureMarkup.IsOpenLine("  <figure>  ").ShouldBeTrue();
        FigureMarkup.IsOpenLine("<figure class=\"x\">").ShouldBeFalse();
        FigureMarkup.IsCloseLine("</figure>").ShouldBeTrue();
        FigureMarkup.IsCloseLine("</figure> trailing").ShouldBeFalse();
    }

    [Fact]
    public void A_numbered_figure_opens_with_its_id()
    {
        var written = FigureMarkup.Write("inv", "図1", FigureMarkup.Id(3));

        written.ShouldStartWith("<figure id=\"fig-3\">\n<figcaption>図1</figcaption>");
        FigureMarkup.IsOpenLine("<figure id=\"fig-3\">").ShouldBeTrue();
        FigureMarkup.Split(written).Single().ShouldBe(new FigureMarkupPart(true, "inv", "図1"));
        Should.Throw<System.ArgumentException>(() => FigureMarkup.OpenLine("a\"><script>"));
    }

    [Fact]
    public void A_tag_indented_four_spaces_is_code_not_a_figure()
    {
        // CommonMark reads an HTML block only at three spaces of indentation or fewer.
        FigureMarkup.IsOpenLine("   <figure>").ShouldBeTrue();
        FigureMarkup.IsOpenLine("    <figure>").ShouldBeFalse();
        FigureMarkup.IsCloseLine("\t</figure>").ShouldBeFalse();
        FigureMarkup.Contains("text\n\n    <figure>\n\n    x\n\n    </figure>").ShouldBeFalse();
    }

    [Fact]
    public void Tags_in_a_fenced_code_block_are_code()
    {
        const string markdown = "intro\n\n```html\n<figure>\n  <img src=\"a.png\">\n</figure>\n```\n\nafter";

        FigureMarkup.Contains(markdown).ShouldBeFalse();
        FigureMarkup.Split(markdown).Single().IsFigure.ShouldBeFalse();
        FigureMarkup.Escape(markdown).ShouldBeSameAs(markdown);
        FigureMarkup.ExtractBodies(markdown).ShouldBeEmpty();
        FigureMarkup.Strip(markdown).ShouldBe(markdown);
        FigureMarkup.OpensFigureAt(markdown, markdown.IndexOf("<figure>", System.StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void A_figure_after_a_closed_code_block_is_a_figure()
    {
        var markdown = "~~~\ncode\n~~~\n\n" + FigureMarkup.Write("inv", null, FigureMarkup.Id(1));

        FigureMarkup.Contains(markdown).ShouldBeTrue();
        FigureMarkup.OpensFigureAt(markdown, markdown.IndexOf("<figure", System.StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public void A_transcription_with_code_keeps_its_code_and_its_figure_closes()
    {
        // A closing tag inside the transcription's code block is code: written as is, and the figure ends at its own.
        var written = FigureMarkup.Write("```\n</figure>\n```\nafter", null);

        written.ShouldBe("<figure>\n\n```\n</figure>\n```\nafter\n\n</figure>");
        FigureMarkup.Split(written).Single().Text.ShouldBe("```\n</figure>\n```\nafter");
    }

    [Fact]
    public void A_code_block_the_transcription_leaves_open_is_closed_inside_the_figure()
    {
        // A truncated transcription would otherwise hold the closing tag, and everything after it, as code.
        var written = FigureMarkup.Write("```js\nlet a", null);

        written.ShouldBe("<figure>\n\n```js\nlet a\n```\n\n</figure>");
        FigureMarkup.Split(written + "\n\nafter").First().ShouldBe(new FigureMarkupPart(true, "```js\nlet a\n```", null));
    }
}

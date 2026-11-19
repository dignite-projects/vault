using System.Collections.Generic;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <see cref="FieldSourceLocator"/> places the passages field extraction quoted in the Markdown it read: verbatim
/// first, then with what a model does not copy faithfully (width, case, spacing, Markdown and table markup, entities)
/// ignored on both sides, always as a range of the original text.
/// </summary>
public class FieldSourceLocator_Tests
{
    private const string Markdown =
        "# 請求書\n\n請求日：２０２６年１１月１日\n\n**お支払期限** 2026年12月31日\n\n" +
        "| 品名 | 金額 |\n| --- | --- |\n| システム保守 | 12,000 |\n| 合計 | 13,200 |\n\n" +
        "<table>\n<tr><td colspan=\"2\">A &amp; B 株式会社</td></tr>\n</table>\n\n合計 13,200 円";

    [Fact]
    public void A_verbatim_passage_is_found_where_it_is()
    {
        Located("請求日：２０２６年１１月１日").ShouldBe("請求日：２０２６年１１月１日");
    }

    [Fact]
    public void Full_width_digits_spacing_and_emphasis_do_not_prevent_a_match()
    {
        Located("請求日: 2026年11月1日").ShouldBe("請求日：２０２６年１１月１日");
        Located("お支払期限 2026年12月31日").ShouldBe("お支払期限** 2026年12月31日");
    }

    [Fact]
    public void A_table_row_quoted_without_its_pipes_is_found_across_the_cells()
    {
        Located("システム保守 12,000").ShouldBe("システム保守 | 12,000");
    }

    [Fact]
    public void Html_table_markup_and_entities_are_read_through()
    {
        Located("A & B 株式会社").ShouldBe("A &amp; B 株式会社");
    }

    [Fact]
    public void A_passage_found_more_than_once_gives_no_source()
    {
        // "13,200" is the table's total and the closing line's: nothing tells which one the model read.
        FieldSourceLocator.Locate(Markdown, Quotes("13,200")).ShouldBeEmpty();
        // Not verbatim, and twice once normalized: "１１月" in full-width digits and "11月" are the same key.
        FieldSourceLocator.Locate(Markdown + "\n\n11月分", Quotes("11 月")).ShouldBeEmpty();
    }

    [Fact]
    public void The_words_around_a_repeated_value_make_it_unique()
    {
        Located("| 合計 | 13,200 |").ShouldBe("| 合計 | 13,200 |");
    }

    [Fact]
    public void A_passage_that_is_not_in_the_text_gives_no_source()
    {
        FieldSourceLocator.Locate(Markdown, Quotes("15,000", "  ")).ShouldBeEmpty();
    }

    [Fact]
    public void Several_passages_give_several_ranges_in_text_order_without_duplicates()
    {
        var spans = FieldSourceLocator.Locate(Markdown, Quotes("合計 13,200 円", "| 合計 | 13,200 |", "合計 13,200 円"));

        spans["field"].Count.ShouldBe(2);
        spans["field"][0].Offset.ShouldBeLessThan(spans["field"][1].Offset);
    }

    private static string Located(string passage)
    {
        var span = FieldSourceLocator.Locate(Markdown, Quotes(passage))["field"].ShouldHaveSingleItem();
        return Markdown.Substring(span.Offset, span.Length);
    }

    private static Dictionary<string, IReadOnlyList<string>> Quotes(params string[] passages)
        => new() { ["field"] = passages };
}

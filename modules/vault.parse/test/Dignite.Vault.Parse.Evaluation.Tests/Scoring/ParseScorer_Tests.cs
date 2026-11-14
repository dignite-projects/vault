using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse.Evaluation.Scoring;

public class ParseScorer_Tests
{
    private static readonly CorpusDocument Truth = new()
    {
        Id = "sample",
        File = "sample.pdf",
        Pages = 2,
        Noise = ["社外秘 / Confidential"],
        PageNumbers = true,
        Elements =
        [
            new TruthElement { Type = "heading", Level = 1, Text = "業務委託契約書" },
            new TruthElement { Type = "paragraph", Text = "甲と乙は、次のとおり業務委託契約を締結する。" },
            new TruthElement { Type = "heading", Level = 2, Text = "第1条（目的）" },
            new TruthElement { Type = "list", Items = ["システム保守", "障害対応（２４時間）"] },
            new TruthElement { Type = "heading", Level = 2, Text = "第2条（委託料）" },
            new TruthElement
            {
                Type = "table",
                Rows =
                [
                    ["項目", "金額"],
                    ["月額保守料", "¥120,000"],
                    ["合計", null]
                ]
            }
        ]
    };

    private const string Perfect = """
        # 業務委託契約書

        甲と乙は、次のとおり業務委託契約を締結する。

        ## 第1条（目的）

        - システム保守
        - 障害対応（24時間）

        ## 第2条（委託料）

        | 項目 | 金額 |
        |---|---|
        | 月額保守料 | ¥120,000 |
        | 合計 |  |
        """;

    [Fact]
    public void A_faithful_parse_scores_one_on_every_metric()
    {
        var score = ParseScorer.Score(Truth, Perfect);

        score.TextSimilarity.ShouldBe(1d);
        score.BlockRecall.ShouldBe(1d);
        score.ReadingOrder.ShouldBe(1d);
        score.TableCellRecall.ShouldBe(1d);
        score.TableCellPrecision.ShouldBe(1d);
        score.CellHeaderRecall.ShouldBe(1d);
        score.HeadingRecall.ShouldBe(1d);
        score.HeadingLevelAgreement.ShouldBe(1d);
        score.NoiseFree.ShouldBe(1d);
    }

    [Fact]
    public void A_lost_paragraph_lowers_block_recall_and_text_similarity()
    {
        var score = ParseScorer.Score(Truth, Perfect.Replace("甲と乙は、次のとおり業務委託契約を締結する。", string.Empty));

        score.BlockRecall.ShouldBe(5d / 6);
        score.TextSimilarity.ShouldBeLessThan(0.8);
    }

    [Fact]
    public void A_document_without_tables_read_without_tables_has_no_spurious_cells()
    {
        var document = new CorpusDocument
        {
            Id = "prose",
            File = "prose.pdf",
            Elements = [new TruthElement { Type = "paragraph", Text = "Only prose here." }]
        };

        var score = ParseScorer.Score(document, "Only prose here.");

        score.TableCellRecall.ShouldBeNull();
        score.TableCellPrecision.ShouldBe(1d);
        score.CellHeaderRecall.ShouldBeNull();
    }

    [Fact]
    public void Blocks_out_of_order_lower_reading_order_only()
    {
        // The second column read before the first: the article headings swap places with their content.
        var swapped = """
            # 業務委託契約書

            ## 第2条（委託料）

            甲と乙は、次のとおり業務委託契約を締結する。

            ## 第1条（目的）

            - システム保守
            - 障害対応（24時間）

            | 項目 | 金額 |
            |---|---|
            | 月額保守料 | ¥120,000 |
            | 合計 |  |
            """;

        var score = ParseScorer.Score(Truth, swapped);

        score.BlockRecall.ShouldBe(1d);
        score.ReadingOrder.ShouldBe(5d / 6);
    }

    [Fact]
    public void Table_cells_flattened_into_paragraphs_are_not_table_cells()
    {
        var flattened = Perfect[..Perfect.IndexOf('|')] + "項目 金額\n\n月額保守料 ¥120,000\n\n合計";

        var score = ParseScorer.Score(Truth, flattened);

        score.TableCellRecall.ShouldBe(0d);
        score.TableCellPrecision.ShouldBeNull();
        score.CellHeaderRecall.ShouldBe(0d);
        score.TextSimilarity.ShouldBe(1d);
    }

    [Fact]
    public void Columns_of_text_read_as_a_table_are_not_table_cells()
    {
        var document = new CorpusDocument
        {
            Id = "columns",
            File = "columns.pdf",
            Elements =
            [
                new TruthElement { Type = "paragraph", Text = "Left column text." },
                new TruthElement { Type = "paragraph", Text = "Right column text." }
            ]
        };

        var score = ParseScorer.Score(document, "| Left column text. | Right column text. |\n|---|---|");

        score.TableCellRecall.ShouldBeNull();
        score.TableCellPrecision.ShouldBe(0d);
        score.BlockRecall.ShouldBe(1d);
    }

    [Fact]
    public void A_table_continued_without_its_header_loses_the_header_of_its_cells()
    {
        // The last row moved to a second table, as a parser does at a page break: its cells are still found, but no
        // longer under 項目 and 金額.
        var split = Perfect.Replace("| 合計 |  |", string.Empty) + "\n\n| 合計 |  |\n|---|---|\n";

        var score = ParseScorer.Score(Truth, split);

        score.TableCellRecall.ShouldBe(1d);
        score.CellHeaderRecall.ShouldBe(2d / 3);
    }

    [Fact]
    public void Shifted_columns_put_cells_under_the_wrong_header()
    {
        var shifted = Perfect.Replace("| 月額保守料 | ¥120,000 |", "|  | 月額保守料 | ¥120,000 |");

        var score = ParseScorer.Score(Truth, shifted);

        score.TableCellRecall.ShouldBe(1d);
        score.CellHeaderRecall.ShouldBe(1d / 3);
    }

    [Fact]
    public void An_html_table_with_spans_counts_its_cells()
    {
        var html = Perfect[..Perfect.IndexOf('|')] +
            "<table><tr><th>項目</th><th>金額</th></tr><tr><td>月額保守料</td><td>&yen;120,000</td></tr><tr><td colspan=\"2\">合計</td></tr></table>";

        ParseScorer.Score(Truth, html).TableCellRecall.ShouldBe(1d);
    }

    [Fact]
    public void Headings_written_as_paragraphs_are_not_recalled()
    {
        var score = ParseScorer.Score(Truth, Perfect.Replace("## ", string.Empty));

        score.HeadingRecall.ShouldBe(1d / 3);
        score.HeadingLevelAgreement.ShouldBeNull();
        score.BlockRecall.ShouldBe(1d);
    }

    [Fact]
    public void A_provider_label_before_a_heading_is_not_its_text()
    {
        ParseScorer.Score(Truth, Perfect.Replace("# 業務委託契約書", "# Sheet: 業務委託契約書")).HeadingRecall.ShouldBe(1d);
    }

    [Fact]
    public void Flattened_heading_levels_disagree()
    {
        var score = ParseScorer.Score(Truth, Perfect.Replace("## ", "# "));

        score.HeadingRecall.ShouldBe(1d);
        score.HeadingLevelAgreement.ShouldBe(0.5);
    }

    [Theory]
    [InlineData("- 1 -")]
    [InlineData("\\- 1 -")]
    [InlineData("— 12 —")]
    public void A_page_number_line_is_a_leak(string line)
    {
        var score = ParseScorer.Score(Truth, Perfect + "\n\n" + line + "\n");

        score.NoiseLeaks.ShouldBe(1);
        score.NoiseFree.ShouldBe(1d - 1d / 4);
    }

    [Fact]
    public void A_running_header_on_every_page_is_two_leaks()
    {
        var score = ParseScorer.Score(Truth, "社外秘 / Confidential\n\n" + Perfect + "\n\n社外秘 / Confidential\n");

        score.NoiseLeaks.ShouldBe(2);
        score.NoiseFree.ShouldBe(0.5);
    }

    [Fact]
    public void Ocr_provenance_markers_are_not_text()
    {
        var marked = "*[Image OCR p:1]*\n\n" + Perfect + "\n\n*[End OCR]*";

        ParseScorer.Score(Truth, marked).TextSimilarity.ShouldBe(1d);
    }

    [Fact]
    public void Normalize_folds_width_and_case_and_drops_whitespace()
    {
        ParseText.Normalize("ＡＢＣ　１２３\n Def").ShouldBe("abc123def");
    }

    [Fact]
    public void Best_occurrence_finds_a_pattern_anywhere_in_the_text()
    {
        EditDistance.Levenshtein("kitten", "sitting").ShouldBe(3);
        EditDistance.BestOccurrence("契約書", "業務委託契約書を締結").ShouldBe((0, 7));
        EditDistance.BestOccurrence("契約所", "業務委託契約書を締結").Distance.ShouldBe(1);
    }

    [Fact]
    public void Html_table_spans_leave_covered_cells_empty()
    {
        var rows = ParseText.Tables("<table><tr><td rowspan=\"2\">A</td><td>B</td></tr><tr><td>C</td></tr></table>").Single();

        rows.Select(r => string.Join(',', r)).ShouldBe(["a,b", ",c"]);
    }
}

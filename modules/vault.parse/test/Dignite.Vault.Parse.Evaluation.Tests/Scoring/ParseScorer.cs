using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dignite.Vault.Parse.Evaluation.Scoring;

/// <summary>
/// The scores of one parse against its ground truth, each in [0, 1]; <c>null</c> where the document has nothing to
/// measure (no table, fewer than two headings, nothing printed on every page).
/// </summary>
/// <param name="TextSimilarity">One minus the character edit distance between all of the truth text and all of the
/// output text, over the length of the longer. Lost, garbled, duplicated and leaked text all lower it.</param>
/// <param name="BlockRecall">The share of the truth's headings, paragraphs and list items found in the output, each
/// within 10% character edits.</param>
/// <param name="ReadingOrder">Of the blocks found, the share already in truth order (the longest in-order run).</param>
/// <param name="TableCellRecall">The share of truth table cells found in a table row of the output.</param>
/// <param name="TableCellPrecision">The share of output table cells that are truth table cells: text laid out in columns
/// (a two-column page) read as a table, or a paragraph pulled into one, lowers it. One when neither has a table; none
/// when only the truth has one, which recall already counts.</param>
/// <param name="CellHeaderRecall">The share of truth data cells found in an output table under their own column header
/// (the first row of every truth table is its header): a table split at a page break without its header, or columns
/// shifted, lowers it.</param>
/// <param name="HeadingRecall">The share of truth headings the output marks as headings.</param>
/// <param name="HeadingLevelAgreement">Of consecutive found headings, the share whose levels move the same way.</param>
/// <param name="NoiseFree">One minus the share of running headers and page numbers that reached the output.</param>
public sealed record ParseScore(
    double TextSimilarity,
    double BlockRecall,
    double? ReadingOrder,
    double? TableCellRecall,
    double? TableCellPrecision,
    double? CellHeaderRecall,
    double? HeadingRecall,
    double? HeadingLevelAgreement,
    double? NoiseFree,
    int NoiseLeaks);

public static partial class ParseScorer
{
    private const double BlockTolerance = 0.1;
    private const double CellTolerance = 0.15;
    private const double HeadingTolerance = 0.2;

    public static ParseScore Score(CorpusDocument truth, string markdown)
    {
        var plain = ParseText.Normalize(ParseText.StripMarkdown(markdown));
        var (blockRecall, readingOrder) = Blocks(truth, plain);
        var (noiseFree, leaks) = Noise(truth, markdown);
        var (headingRecall, levelAgreement) = Headings(truth, markdown);
        var outputTables = ParseText.Tables(markdown);
        var (cellRecall, cellPrecision) = TableCells(truth, outputTables);
        return new ParseScore(
            EditDistance.Similarity(TruthText(truth), plain),
            blockRecall,
            readingOrder,
            cellRecall,
            cellPrecision,
            CellHeaders(truth, outputTables),
            headingRecall,
            levelAgreement,
            noiseFree,
            leaks);
    }

    /// <summary>All of the truth text in reading order, table cells row by row.</summary>
    public static string TruthText(CorpusDocument truth)
    {
        var builder = new StringBuilder();
        foreach (var element in truth.Elements)
        {
            switch (element.Type)
            {
                case "heading" or "paragraph":
                    builder.Append(ParseText.Normalize(element.Text));
                    break;
                case "list":
                    foreach (var item in element.Items)
                    {
                        builder.Append(ParseText.Normalize(item));
                    }

                    break;
                case "table":
                    foreach (var cell in element.Rows.SelectMany(r => r).Where(c => c is not null))
                    {
                        builder.Append(ParseText.Normalize(cell));
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> BlockTexts(CorpusDocument truth)
        => truth.Elements.SelectMany(e => e.Type switch
        {
            "heading" or "paragraph" => [e.Text ?? string.Empty],
            "list" => e.Items,
            _ => []
        }).Select(ParseText.Normalize).Where(t => t.Length > 0);

    private static (double Recall, double? Order) Blocks(CorpusDocument truth, string plain)
    {
        var blocks = BlockTexts(truth).ToList();
        if (blocks.Count == 0)
        {
            return (1d, null);
        }

        var positions = new List<int>();
        foreach (var block in blocks)
        {
            var (distance, end) = EditDistance.BestOccurrence(block, plain);
            if (distance <= Math.Max(1, (int)Math.Floor(block.Length * BlockTolerance)) || (block.Length <= 2 && distance == 0))
            {
                positions.Add(end);
            }
        }

        return ((double)positions.Count / blocks.Count, positions.Count == 0 ? null : (double)LongestNonDecreasingRun(positions) / positions.Count);
    }

    private static int LongestNonDecreasingRun(IReadOnlyList<int> values)
    {
        // Patience sorting over "non-decreasing": the length of the longest subsequence kept in order.
        var tails = new List<int>();
        foreach (var value in values)
        {
            var low = 0;
            var high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (tails[middle] <= value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            if (low == tails.Count)
            {
                tails.Add(value);
            }
            else
            {
                tails[low] = value;
            }
        }

        return tails.Count;
    }

    private static (double? Recall, double? Precision) TableCells(
        CorpusDocument truth, IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> outputTables)
    {
        // Every output table row is a candidate for every truth row, each used once: a table the parser split at a
        // page break still counts, and the rows of two tables never share a match.
        var outputRows = outputTables.SelectMany(t => t).Select(r => r.ToList()).ToList();
        var outputCells = outputRows.Sum(r => r.Count(c => c.Length > 0));
        var truthTables = truth.Elements.Where(e => e.Type == "table").ToList();
        if (truthTables.Count == 0)
        {
            return (null, outputCells == 0 ? 1d : 0d);
        }

        var used = new bool[outputRows.Count];
        var total = 0;
        var found = 0;
        foreach (var row in truthTables.SelectMany(t => t.Rows))
        {
            var cells = row.Where(c => c is not null).Select(ParseText.Normalize).Where(c => c.Length > 0).ToList();
            total += cells.Count;
            var bestIndex = -1;
            var best = 0;
            for (var i = 0; i < outputRows.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var matched = MatchedCells(cells, outputRows[i]);
                if (matched > best)
                {
                    best = matched;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
            {
                used[bestIndex] = true;
                found += best;
            }
        }

        return (total == 0 ? null : (double)found / total, outputCells == 0 ? null : (double)found / outputCells);
    }

    private static double? CellHeaders(CorpusDocument truth, IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> outputTables)
    {
        var truthPairs = truth.Elements
            .Where(e => e.Type == "table" && e.Rows.Count > 1)
            .SelectMany(e => HeaderPairs(e.Rows.Select(r => (IReadOnlyList<string>)r.Select(ParseText.Normalize).ToList()).ToList()))
            .ToList();
        if (truthPairs.Count == 0)
        {
            return null;
        }

        var outputPairs = outputTables.Where(t => t.Count > 1).SelectMany(HeaderPairs).ToList();
        var used = new bool[outputPairs.Count];
        var found = 0;
        foreach (var (header, cell) in truthPairs)
        {
            var index = -1;
            for (var i = 0; i < outputPairs.Count && index < 0; i++)
            {
                if (!used[i] && outputPairs[i].Header == header && outputPairs[i].Cell == cell)
                {
                    index = i;
                }
            }

            for (var i = 0; i < outputPairs.Count && index < 0; i++)
            {
                if (!used[i] && CellsMatch(outputPairs[i].Header, header) && CellsMatch(outputPairs[i].Cell, cell))
                {
                    index = i;
                }
            }

            if (index >= 0)
            {
                used[index] = true;
                found++;
            }
        }

        return (double)found / truthPairs.Count;
    }

    /// <summary>
    /// Each non-empty data cell with the header of its column, the first row's cell there; a header cell covered by a
    /// span (empty) takes the text of the nearest header cell to its left, and a cell beyond the header has none.
    /// </summary>
    private static IEnumerable<(string Header, string Cell)> HeaderPairs(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var header = rows[0];
        for (var r = 1; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (rows[r][c].Length == 0)
                {
                    continue;
                }

                if (c >= header.Count)
                {
                    // Beyond the header's last column: the cell has no header.
                    yield return (string.Empty, rows[r][c]);
                    continue;
                }

                var h = c;
                while (h > 0 && header[h].Length == 0)
                {
                    h--;
                }

                yield return (header[h], rows[r][c]);
            }
        }
    }

    private static bool CellsMatch(string output, string truth)
        => output == truth || (truth.Length >= 4 && output.Length > 0 && 1d - EditDistance.Similarity(output, truth) <= CellTolerance);

    private static int MatchedCells(IReadOnlyList<string> truthCells, IReadOnlyList<string> outputCells)
    {
        var available = outputCells.ToList();
        var matched = 0;
        foreach (var cell in truthCells)
        {
            var index = available.FindIndex(c => c == cell);
            if (index < 0)
            {
                index = available.FindIndex(c => CellsMatch(c, cell));
            }

            if (index >= 0)
            {
                matched++;
                available.RemoveAt(index);
            }
        }

        return matched;
    }

    private static (double? Recall, double? LevelAgreement) Headings(CorpusDocument truth, string markdown)
    {
        var truthHeadings = truth.Elements.Where(e => e.Type == "heading").Select(e => (e.Level, Text: ParseText.Normalize(e.Text))).ToList();
        if (truthHeadings.Count == 0)
        {
            return (null, null);
        }

        var output = ParseText.Headings(markdown).ToList();
        var used = new bool[output.Count];
        var pairs = new List<(int Truth, int Output)>();
        foreach (var (level, text) in truthHeadings)
        {
            var index = -1;
            for (var i = 0; i < output.Count && index < 0; i++)
            {
                if (!used[i] && output[i].Text == text)
                {
                    index = i;
                }
            }

            for (var i = 0; i < output.Count && index < 0; i++)
            {
                if (!used[i] && 1d - EditDistance.Similarity(output[i].Text, text) <= HeadingTolerance)
                {
                    index = i;
                }
            }

            // A provider's label before the heading ("Sheet: 売上", "Slide 2: Goals") is not the heading's text.
            for (var i = 0; i < output.Count && index < 0; i++)
            {
                if (!used[i] && HeadingLabel().Match(output[i].Text) is { Success: true } label && label.Groups[1].Value == text)
                {
                    index = i;
                }
            }

            if (index >= 0)
            {
                used[index] = true;
                pairs.Add((level, output[index].Level));
            }
        }

        double? agreement = null;
        if (pairs.Count >= 2)
        {
            var agreeing = 0;
            for (var i = 1; i < pairs.Count; i++)
            {
                if (Math.Sign(pairs[i].Truth - pairs[i - 1].Truth) == Math.Sign(pairs[i].Output - pairs[i - 1].Output))
                {
                    agreeing++;
                }
            }

            agreement = (double)agreeing / (pairs.Count - 1);
        }

        return ((double)pairs.Count / truthHeadings.Count, agreement);
    }

    private static (double? NoiseFree, int Leaks) Noise(CorpusDocument truth, string markdown)
    {
        var pages = Math.Max(1, truth.Pages ?? 1);
        var slots = pages * (truth.Noise.Count + (truth.PageNumbers ? 1 : 0));
        if (slots == 0)
        {
            return (null, 0);
        }

        var plain = ParseText.Normalize(ParseText.StripMarkdown(markdown));
        var leaks = 0;
        foreach (var noise in truth.Noise.Select(ParseText.Normalize).Where(n => n.Length > 0))
        {
            for (var index = plain.IndexOf(noise, StringComparison.Ordinal); index >= 0; index = plain.IndexOf(noise, index + noise.Length, StringComparison.Ordinal))
            {
                leaks++;
            }
        }

        if (truth.PageNumbers)
        {
            leaks += markdown.Replace("\r\n", "\n").Split('\n').Count(line => PageNumberLine().IsMatch(line));
        }

        return (1d - (double)Math.Min(leaks, slots) / slots, leaks);
    }

    [GeneratedRegex(@"^\s*\\?[-–—]\s*\d{1,3}\s*\\?[-–—]\s*$")]
    private static partial Regex PageNumberLine();

    [GeneratedRegex(@"^[^:]{1,12}:(.+)$")]
    private static partial Regex HeadingLabel();
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// Joins a table that a page break cut in two: when a page ends with a table and the next page starts with one of the
/// same columns, the second is the first's continuation and its rows are appended to it. The rules follow RAGFlow's
/// cross-page table merge (<c>internal/deepdoc/parser/pdf/table/table_merge.go</c>, Apache-2.0): consecutive pages,
/// overlapping horizontally, a header that agrees (here read from the rows, see <see cref="HasHeader"/>), a repeated header dropped, columns mapped by position. Where RAGFlow
/// measures the gap to the page edges, this reads the page's blocks: the table must be the last thing on its page and
/// the continuation the first on the next (running headers and footers are already removed).
/// </summary>
internal static class PdfTableContinuation
{
    /// <summary>A page's blocks in reading order; a joined continuation is removed from its page.</summary>
    public sealed record Page(int Number, List<PdfBlock> Blocks);

    /// <summary>Appends every continuation to the table it continues, across as many pages as the table runs.</summary>
    public static void Join(IReadOnlyList<Page> pages)
    {
        // The page holding the table that may continue onto the next page, and the last page the table reaches.
        List<PdfBlock>? owner = null;
        var reached = 0;
        foreach (var page in pages)
        {
            if (owner is not null
                && page.Number == reached + 1
                && page.Blocks.Count > 0
                && page.Blocks[0] is PdfTableBlock next
                && owner[^1] is PdfTableBlock table
                && Continues(table, next))
            {
                owner[^1] = Append(table, next, page.Number);
                page.Blocks.RemoveAt(0);
                reached = page.Number;
                if (page.Blocks.Count == 0)
                {
                    // The whole page was the table: it may run on to the page after.
                    continue;
                }
            }

            owner = page.Blocks.Count > 0 && page.Blocks[^1] is PdfTableBlock ? page.Blocks : null;
            reached = page.Number;
        }
    }

    /// <summary>Whether <paramref name="next"/> reads as the continuation of <paramref name="table"/>.</summary>
    private static bool Continues(PdfTableBlock table, PdfTableBlock next)
    {
        if (table.Table.ColumnCount != next.Table.ColumnCount)
        {
            return false;
        }

        var left = Math.Max(Math.Min(table.Bounds.Left, table.Bounds.Right), Math.Min(next.Bounds.Left, next.Bounds.Right));
        var right = Math.Min(Math.Max(table.Bounds.Left, table.Bounds.Right), Math.Max(next.Bounds.Left, next.Bounds.Right));
        if (right <= left)
        {
            return false;
        }

        // RAGFlow keeps tables apart whose detected headers disagree. Two tables that each open on a header, and not the
        // same one, are two tables; a continuation opening on data, or on the header repeated, joins. Where no header can
        // be told from the rows (a table of text only), it joins: a table cut by a page break is the common case.
        return !(HasHeader(table.Table) && HasHeader(next.Table) && !RowText(table.Table, 0).SequenceEqual(RowText(next.Table, 0)));
    }

    /// <summary>
    /// Whether <paramref name="table"/> opens on a header, decided as Python's <c>csv.Sniffer.has_header</c> decides it
    /// (CPython <c>Lib/csv.py</c>, PSF-2.0): a header differs from the rows under it. Each column whose cells below the
    /// first row (up to twenty) agree on a kind, all numbers or all text of one length, votes: a first cell of another
    /// kind for a header, one of the same kind against. A column whose cells disagree, or a table without rows under the
    /// first, does not vote, and a tie is no header. So a row of text over rows of text of varying length is not a
    /// header: nothing tells it from the data under it.
    /// </summary>
    private static bool HasHeader(PdfTable table)
    {
        var rows = Enumerable.Range(0, Math.Min(table.RowCount, SniffedRows + 1)).Select(row => RowText(table, row)).ToList();
        var votes = 0;
        for (var column = 0; column < table.ColumnCount; column++)
        {
            var first = rows[0][column];
            var kinds = rows.Skip(1).Select(row => row[column]).Where(text => text.Length > 0).Select(KindOf).Distinct().ToList();
            if (first.Length == 0 || kinds.Count != 1)
            {
                continue;
            }

            votes += KindOf(first) == kinds[0] ? -1 : 1;
        }

        return votes > 0;
    }

    /// <summary>How many rows under the first <see cref="HasHeader"/> reads, as <c>csv.Sniffer</c> does.</summary>
    private const int SniffedRows = 20;

    /// <summary>A cell's kind: a number (any text with a digit: an amount, a date, a code) or text of its length.</summary>
    private static (bool Number, int Length) KindOf(string text)
        => text.Any(char.IsDigit) ? (true, 0) : (false, text.Length);

    /// <summary>
    /// <paramref name="table"/> with the rows of <paramref name="next"/> (found on page <paramref name="pageNumber"/>)
    /// after its own, less a first row that repeats the header.
    /// </summary>
    private static PdfTableBlock Append(PdfTableBlock table, PdfTableBlock next, int pageNumber)
    {
        var repeatsHeader = RowText(table.Table, 0).SequenceEqual(RowText(next.Table, 0));
        var skip = repeatsHeader ? 1 : 0;
        var offset = table.Table.RowCount - skip;
        var joined = new PdfTable(table.Table.RowCount + next.Table.RowCount - skip, table.Table.ColumnCount);
        joined.Cells.AddRange(table.Table.Cells);
        foreach (var cell in next.Table.Cells.Where(cell => cell.Row >= skip))
        {
            joined.Cells.Add(cell with { Row = cell.Row + offset, PageNumber = cell.PageNumber ?? pageNumber });
        }

        return new PdfTableBlock(table.Bounds, joined)
        {
            Continuations = [.. table.Continuations, new PdfContinuation(pageNumber, next.Bounds), .. next.Continuations]
        };
    }

    /// <summary>The normalized text of row <paramref name="row"/>, cell by cell (a covered position reads empty).</summary>
    private static IReadOnlyList<string> RowText(PdfTable table, int row)
    {
        var texts = new string[table.ColumnCount];
        Array.Fill(texts, string.Empty);
        foreach (var cell in table.Cells.Where(cell => cell.Row == row))
        {
            texts[cell.Column] = string.Concat(cell.Text.Where(c => !char.IsWhiteSpace(c)));
        }

        return texts;
    }
}

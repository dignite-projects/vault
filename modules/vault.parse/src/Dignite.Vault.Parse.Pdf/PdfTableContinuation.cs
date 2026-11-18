using System;
using System.Collections.Generic;
using System.Linq;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// Joins a table that a page break cut in two: when a page ends with a table and the next page starts with one of the
/// same columns, the second is the first's continuation and its rows are appended to it. The rules follow RAGFlow's
/// cross-page table merge (<c>internal/deepdoc/parser/pdf/table/table_merge.go</c>, Apache-2.0): consecutive pages,
/// overlapping horizontally, a header that agrees (here: not a different row that reads as a header, since no header is detected), a repeated header dropped, columns mapped by position. Where RAGFlow
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

        // RAGFlow keeps tables apart whose detected headers disagree. Nothing here detects a header, so a first row
        // counts as one when it reads like one: two or more labels and no number. Two such rows that differ are two
        // tables; a continuation opening on data, or on the header repeated, joins.
        var header = RowText(table.Table, 0);
        var opening = RowText(next.Table, 0);
        return !(ReadsAsHeader(header) && ReadsAsHeader(opening) && !header.SequenceEqual(opening));
    }

    private static bool ReadsAsHeader(IReadOnlyList<string> row)
        => row.Count(text => text.Length > 0) >= 2 && !row.Any(text => text.Any(char.IsDigit));

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

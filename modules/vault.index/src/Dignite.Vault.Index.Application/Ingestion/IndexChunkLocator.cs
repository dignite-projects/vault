using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Integration;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Finds where each chunk came from in the document's Markdown, as a range of that Markdown, so a chunk can be placed on
/// the pages its text was read from.
/// <para>
/// DataIngestion does not say which part of the input a chunk holds (dotnet/extensions#7904 adds that, unreleased), and
/// a chunk is not a substring of the Markdown: the chunker re-renders each element, starts the chunk with its heading
/// path, repeats a split table's header row and may overlap the previous chunk. So the chunk is found line by line,
/// compared as <see cref="NormalizedMarkdown"/> keys, in document order: each line is searched from the previous found
/// line onward, after the previous chunk's range first and inside it only for text the two share (an overlap, a
/// repeated header row). The range runs from the first found line to the last, and starts at the first line found
/// after the previous chunk when there is one. A line too short to place reliably, a table rule or the heading path line is not searched,
/// and a line found farther ahead than the chunk could reach is taken for a coincidence.
/// </para>
/// </summary>
public static class IndexChunkLocator
{
    /// <summary>Shorter keys match by chance too easily; such lines only ride along inside the range.</summary>
    public const int MinKeyLength = 4;

    /// <summary>
    /// The pages each chunk (heading path and text, in document order) was read from, as first and last page; both
    /// <c>null</c> for a chunk that was not found or whose range no page's text overlaps, and for every chunk when
    /// <paramref name="pages"/> is empty.
    /// </summary>
    public static IReadOnlyList<(int? First, int? Last)> Pages(
        string markdown,
        IReadOnlyList<DocumentPageSpansDto>? pages,
        IReadOnlyList<(string? HeadingPath, string Text)> chunks)
    {
        if (pages is not { Count: > 0 })
        {
            return chunks.Select(_ => ((int?)null, (int?)null)).ToList();
        }

        return Pages(Locate(markdown, chunks), pages);
    }

    /// <summary>
    /// The pages each located range (<see cref="Locate"/>) was read from, as first and last page; both <c>null</c> for a
    /// range that is <c>null</c> or that no page's text overlaps, and for every range when <paramref name="pages"/> is
    /// empty.
    /// </summary>
    public static IReadOnlyList<(int? First, int? Last)> Pages(
        IReadOnlyList<(int Offset, int Length)?> ranges,
        IReadOnlyList<DocumentPageSpansDto>? pages)
    {
        if (pages is not { Count: > 0 })
        {
            return ranges.Select(_ => ((int?)null, (int?)null)).ToList();
        }

        return ranges
            .Select(range =>
            {
                if (range is not { } r)
                {
                    return ((int?)null, (int?)null);
                }

                var end = r.Offset + r.Length;
                var on = pages
                    .Where(page => page.Spans.Any(span => span.Offset < end && r.Offset < span.Offset + span.Length))
                    .Select(page => page.PageNumber)
                    .ToList();
                return on.Count == 0 ? ((int?)null, (int?)null) : (on.Min(), on.Max());
            })
            .ToList();
    }

    /// <summary>The range of <paramref name="markdown"/> each chunk holds, in chunk order; <c>null</c> for a chunk none of whose lines was found.</summary>
    public static IReadOnlyList<(int Offset, int Length)?> Locate(string markdown, IReadOnlyList<(string? HeadingPath, string Text)> chunks)
    {
        var ranges = new List<(int Offset, int Length)?>(chunks.Count);
        if (string.IsNullOrEmpty(markdown))
        {
            ranges.AddRange(chunks.Select(_ => ((int, int)?)null));
            return ranges;
        }

        var text = NormalizedMarkdown.Of(markdown);
        int cursor = 0, previousEnd = 0, previousLength = 0;
        var previousKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            var lines = Lines(chunk);
            var length = lines.Sum(l => l.Key.Length);
            var reach = 2 * (previousLength + length) + 1000;
            int start = -1, ownStart = -1, end = -1, position = cursor;

            // A line the previous chunk holds too (a split table's repeated header row, an overlap, a sentence two
            // sections share) is not searched ahead: the next table's identical header would match it. It is placed after
            // this chunk's own lines, only between the previous chunk and them.
            var shared = lines.Where(line => previousKeys.Contains(line.Key)).ToList();
            var own = shared.Count < lines.Count ? lines.Where(line => !previousKeys.Contains(line.Key)).ToList() : lines;
            foreach (var line in own)
            {
                // A table row whose merged cells the chunker repeated is not in the Markdown whole (an HTML row spanned
                // by a rowspan cell lacks that cell): its own cells, in order, still are.
                var hit = Place(text, line.Key, position, previousEnd, reach)
                          ?? PlaceCells(text, line.Cells, position, previousEnd, reach);
                if (hit is not { } found)
                {
                    continue;
                }

                start = start < 0 ? found.Start : Math.Min(start, found.Start);
                if (found.Own)
                {
                    ownStart = ownStart < 0 ? found.Start : Math.Min(ownStart, found.Start);
                }

                end = Math.Max(end, found.End);
                position = found.End;
            }

            if (start < 0)
            {
                ranges.Add(null);
                continue;
            }

            // Text the previous chunk already holds does not move this one's start back when it has text of its own:
            // the second piece of a split table starts at its rows, not at the header row it repeats.
            if (ownStart >= 0)
            {
                start = ownStart;
            }

            // A shared line this chunk's own copy of sits right before its other lines (the same sentence opening two
            // sections) extends the range back to it.
            if (!ReferenceEquals(own, lines))
            {
                foreach (var line in shared)
                {
                    var index = start > previousEnd ? text.IndexOf(line.Key, previousEnd, start - previousEnd) : -1;
                    if (index >= 0)
                    {
                        start = Math.Min(start, index);
                    }
                }
            }

            ranges.Add(text.ToSource(start, end));
            cursor = start;
            previousEnd = end;
            previousLength = length;
            previousKeys = lines.Select(line => line.Key).ToHashSet(StringComparer.Ordinal);
        }

        return ranges;
    }

    /// <summary>
    /// Finds <paramref name="key"/> after the previous chunk first, so text repeated in both lands in this one; then inside
    /// it, for an overlap or a split table's repeated header row. <c>Own</c> says it was found after the previous chunk.
    /// </summary>
    private static (int Start, int End, bool Own)? Place(NormalizedMarkdown text, string key, int position, int previousEnd, int reach)
    {
        var index = Find(text, key, Math.Max(position, previousEnd), reach);
        if (index >= 0)
        {
            return (index, index + key.Length, true);
        }

        index = position < previousEnd ? Find(text, key, position, reach) : -1;
        return index < 0 ? null : (index, index + key.Length, false);
    }

    /// <summary>
    /// A table row found cell by cell: the first cell placed like a line, each next one close after the one before. Null
    /// when no cell is found, or when the row has a single cell, which the whole-line search already tried.
    /// </summary>
    private static (int Start, int End, bool Own)? PlaceCells(
        NormalizedMarkdown text, IReadOnlyList<string> cells, int position, int previousEnd, int reach)
    {
        (int Start, int End, bool Own)? row = null;
        foreach (var cell in cells.Count > 1 ? cells : [])
        {
            if (row is not { } placed)
            {
                row = Place(text, cell, position, previousEnd, reach);
                continue;
            }

            // The cells of one row sit within a few hundred characters of each other, whatever lies between them.
            var index = Find(text, cell, placed.End, CellReach);
            if (index >= 0)
            {
                row = (placed.Start, index + cell.Length, placed.Own);
            }
        }

        return row;
    }

    /// <summary>How far after one cell of a row the next may be: the markup and the cells too short to search between them.</summary>
    private const int CellReach = 300;

    /// <summary>The first occurrence of <paramref name="key"/> starting within <paramref name="reach"/> of <paramref name="from"/>.</summary>
    private static int Find(NormalizedMarkdown text, string key, int from, int reach)
        => text.IndexOf(key, from, reach + key.Length);

    /// <summary>
    /// The searchable lines of a chunk: each line's key, and for a table row the keys of its cells long enough to place
    /// on their own.
    /// </summary>
    private static List<(string Key, string[] Cells)> Lines((string? HeadingPath, string Text) chunk)
    {
        var lines = chunk.Text.Split('\n');
        var headingKey = chunk.HeadingPath is null ? null : NormalizedMarkdown.Key(chunk.HeadingPath);
        var keys = new List<(string Key, string[] Cells)>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var key = NormalizedMarkdown.Key(lines[i]);
            if (key.Length < MinKeyLength || key.All(c => c is '-' or ':' or '='))
            {
                continue;
            }

            if (i == 0 && headingKey is not null && key.StartsWith(headingKey, StringComparison.Ordinal))
            {
                continue;
            }

            var cells = lines[i].TrimStart().StartsWith('|')
                ? TableCellSeparator.Split(lines[i]).Select(NormalizedMarkdown.Key).Where(c => c.Length >= MinKeyLength).ToArray()
                : [];
            keys.Add((key, cells));
        }

        return keys;
    }

    /// <summary>A pipe that is not escaped.</summary>
    private static readonly Regex TableCellSeparator = new(@"(?<!\\)\|", RegexOptions.CultureInvariant);
}

using System;
using System.Collections.Generic;
using System.Linq;
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

        return Locate(markdown, chunks)
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
        foreach (var chunk in chunks)
        {
            var keys = Keys(chunk);
            var length = keys.Sum(k => k.Length);
            var reach = 2 * (previousLength + length) + 1000;
            int start = -1, ownStart = -1, end = -1, position = cursor;
            foreach (var key in keys)
            {
                // After the previous chunk first, so text repeated in both lands in this one; then inside it, for an
                // overlap or a split table's repeated header row.
                var index = Find(text, key, Math.Max(position, previousEnd), reach);
                var own = index >= 0;
                if (index < 0 && position < previousEnd)
                {
                    index = Find(text, key, position, reach);
                }

                if (index < 0)
                {
                    continue;
                }

                start = start < 0 ? index : Math.Min(start, index);
                if (own)
                {
                    ownStart = ownStart < 0 ? index : Math.Min(ownStart, index);
                }

                end = Math.Max(end, index + key.Length);
                position = index + key.Length;
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

            ranges.Add(text.ToSource(start, end));
            cursor = start;
            previousEnd = end;
            previousLength = length;
        }

        return ranges;
    }

    private static int Find(NormalizedMarkdown text, string key, int from, int reach)
    {
        var index = text.IndexOf(key, from);
        return index < 0 || index - from > reach ? -1 : index;
    }

    private static List<string> Keys((string? HeadingPath, string Text) chunk)
    {
        var lines = chunk.Text.Split('\n');
        var headingKey = chunk.HeadingPath is null ? null : NormalizedMarkdown.Key(chunk.HeadingPath);
        var keys = new List<string>(lines.Length);
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

            keys.Add(key);
        }

        return keys;
    }
}

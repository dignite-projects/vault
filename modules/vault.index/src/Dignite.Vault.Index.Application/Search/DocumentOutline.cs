using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Dignite.Vault.Index.Search;

/// <summary>A chunk as the outline needs it: where it is and which headings are above it.</summary>
public sealed record OutlineChunk(int Ordinal, string? HeadingPath);

/// <summary>
/// One heading of a document's tree. <see cref="FirstOrdinal"/> to <see cref="LastOrdinal"/> covers the node and its
/// subsections: chunks are cut in document order, so they are contiguous.
/// </summary>
public sealed class OutlineNode
{
    public required IReadOnlyList<int> Path { get; init; }

    public required int Level { get; init; }

    public required string Title { get; init; }

    /// <summary>The heading path of the node itself, in the chunks' own notation.</summary>
    public required string HeadingPath { get; init; }

    public int FirstOrdinal { get; internal set; }

    public int LastOrdinal { get; internal set; }

    public int ChunkCount { get; internal set; }

    public int Depth => Path.Count;
}

/// <summary>
/// The heading tree of a document, derived from its chunks' heading paths. It is not stored: it follows the chunks
/// exactly, and a node pointer (its 1-based position) is valid as long as they are, until the document is indexed
/// again. The heading paths come from DataIngestion's <c>HeaderChunker</c>, which writes the headings above a chunk
/// as their Markdown markers joined by spaces (<c># 契約書 ## 第1条</c>).
/// </summary>
public sealed class DocumentOutline
{
    private static readonly Regex HeadingMarker = new(@"(?:^|\s)(#{1,6})\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<OutlineNode> Nodes { get; }

    private DocumentOutline(IReadOnlyList<OutlineNode> nodes)
    {
        Nodes = nodes;
    }

    /// <summary>Builds the tree from the chunks, in any order.</summary>
    public static DocumentOutline Build(IEnumerable<OutlineChunk> chunks)
    {
        var nodes = new List<OutlineNode>();
        var stack = new List<OutlineNode>();
        var childCounts = new Dictionary<OutlineNode, int>();
        var rootCount = 0;

        foreach (var chunk in chunks.OrderBy(c => c.Ordinal))
        {
            var headings = ParseHeadingPath(chunk.HeadingPath);

            // Keep the open nodes the chunk shares with the previous one; the rest of its path opens new nodes.
            var shared = 0;
            while (shared < stack.Count && shared < headings.Count
                   && stack[shared].Level == headings[shared].Level
                   && stack[shared].Title == headings[shared].Title)
            {
                shared++;
            }

            stack.RemoveRange(shared, stack.Count - shared);
            for (var i = shared; i < headings.Count; i++)
            {
                var parent = i == 0 ? null : stack[i - 1];
                int position;
                if (parent is null)
                {
                    position = ++rootCount;
                }
                else
                {
                    childCounts.TryGetValue(parent, out var count);
                    position = count + 1;
                    childCounts[parent] = position;
                }

                var node = new OutlineNode
                {
                    Path = parent is null ? [position] : [.. parent.Path, position],
                    Level = headings[i].Level,
                    Title = headings[i].Title,
                    HeadingPath = string.Join(' ', headings.Take(i + 1).Select(h => new string('#', h.Level) + " " + h.Title)),
                    FirstOrdinal = chunk.Ordinal
                };
                nodes.Add(node);
                stack.Add(node);
            }

            foreach (var open in stack)
            {
                open.LastOrdinal = chunk.Ordinal;
                open.ChunkCount++;
            }
        }

        return new DocumentOutline(nodes);
    }

    public OutlineNode? Find(IReadOnlyList<int> path)
        => Nodes.FirstOrDefault(n => n.Path.SequenceEqual(path));

    /// <summary>The headings in a heading path, outermost first; empty for <c>null</c> or text with no marker.</summary>
    public static IReadOnlyList<(int Level, string Title)> ParseHeadingPath(string? headingPath)
    {
        if (string.IsNullOrWhiteSpace(headingPath))
        {
            return [];
        }

        var markers = HeadingMarker.Matches(headingPath);
        var headings = new List<(int, string)>(markers.Count);
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : headingPath.Length;
            var title = headingPath[start..end].Trim();
            if (title.Length > 0)
            {
                headings.Add((markers[i].Groups[1].Length, title));
            }
        }

        return headings;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Indexing;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Outlines a chunk on its pages: the page regions of the elements whose Markdown its range overlaps
/// (<see cref="DocumentIntegrationDto.Regions"/>, a table cell rather than its table), joined into as few boxes as
/// still follow the text. RAGFlow keeps one box per source item on a chunk (<c>position_int</c>); an element here can be a
/// single table cell, so neighbours are joined: boxes on one line (the cells of a row), and a box under the previous one
/// in the same column (the rows of a table, a paragraph's next paragraph). Two columns of text stay two boxes. Past
/// <see cref="IndexingConsts.MaxChunkRegions"/> boxes, one box per page around everything the chunk was read from there.
/// </summary>
public static class IndexChunkRegions
{
    /// <summary>
    /// How far apart two boxes may be and still be joined, as a fraction of the page: about one and a half lines of
    /// body text on A4, so the space between two paragraphs or table rows is crossed and the gutter between two columns
    /// (or a gap of several lines) is not.
    /// </summary>
    public const double JoinGap = 0.02;

    /// <summary>The regions of <c>[offset, offset + length)</c> in Markdown order, joined; <c>null</c> when none overlaps it.</summary>
    public static IReadOnlyList<ChunkRegion>? Of((int Offset, int Length)? range, IReadOnlyList<DocumentPageRegionDto>? regions)
    {
        if (range is not { } r || regions is not { Count: > 0 })
        {
            return null;
        }

        var end = r.Offset + r.Length;
        var boxes = new List<ChunkRegion>();
        foreach (var region in regions)
        {
            if (region.Offset >= end || region.Offset + region.Length <= r.Offset || region.Width <= 0 || region.Height <= 0)
            {
                continue;
            }

            var box = new ChunkRegion(region.PageNumber, region.Left, region.Top, region.Width, region.Height);
            if (boxes.Count > 0 && Joins(boxes[^1], box))
            {
                boxes[^1] = Union(boxes[^1], box);
            }
            else if (!boxes.Contains(box))
            {
                boxes.Add(box);
            }
        }

        if (boxes.Count > IndexingConsts.MaxChunkRegions)
        {
            boxes = boxes.GroupBy(box => box.Page).Select(page => page.Aggregate(Union)).Take(IndexingConsts.MaxChunkRegions).ToList();
        }

        return boxes.Count == 0 ? null : boxes;
    }

    /// <summary>
    /// Whether <paramref name="next"/> continues <paramref name="previous"/>: on the same page, and either on the same
    /// line (overlapping vertically by half the lower box, close beside it) or below it in the same column (overlapping
    /// horizontally by half the narrower box, close under it).
    /// </summary>
    private static bool Joins(ChunkRegion previous, ChunkRegion next)
    {
        if (previous.Page != next.Page)
        {
            return false;
        }

        var vertical = Overlap(previous.Top, previous.Height, next.Top, next.Height);
        var horizontal = Overlap(previous.Left, previous.Width, next.Left, next.Width);
        var sameLine = vertical >= 0.5 * Math.Min(previous.Height, next.Height) && -horizontal <= JoinGap;
        var sameColumn = horizontal >= 0.5 * Math.Min(previous.Width, next.Width) && -vertical <= JoinGap;
        return sameLine || sameColumn;
    }

    /// <summary>How much two extents overlap; negative for the gap between them.</summary>
    private static double Overlap(double start1, double length1, double start2, double length2)
        => Math.Min(start1 + length1, start2 + length2) - Math.Max(start1, start2);

    private static ChunkRegion Union(ChunkRegion a, ChunkRegion b)
    {
        var left = Math.Min(a.Left, b.Left);
        var top = Math.Min(a.Top, b.Top);
        var right = Math.Max(a.Left + a.Width, b.Left + b.Width);
        var bottom = Math.Max(a.Top + a.Height, b.Top + b.Height);
        return new ChunkRegion(a.Page, left, top, Math.Round(right - left, 4), Math.Round(bottom - top, 4));
    }
}

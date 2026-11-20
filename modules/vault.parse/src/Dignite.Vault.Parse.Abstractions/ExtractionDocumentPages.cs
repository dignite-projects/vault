using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Documents;

namespace Dignite.Vault.Parse;

/// <summary>Page ranges of a document tree.</summary>
public static class ExtractionDocumentPages
{
    /// <summary>
    /// Pages <paramref name="firstPage"/> to <paramref name="lastPage"/> of <paramref name="document"/> as a document of
    /// their own, as if those pages had been parsed alone: the pages renumbered from 1 with their size and frame, the
    /// sections read from them (a section belongs to the page its first source names), every element with its regions.
    /// A region on a page outside the range is dropped. The metadata is kept.
    /// </summary>
    public static ExtractionDocument Select(ExtractionDocument document, int firstPage, int lastPage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastPage, firstPage);

        var root = ExtractionDocumentJson.Write(document);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var pages = new JsonArray();
        foreach (var node in root["pages"]?.AsArray() ?? [])
        {
            if (node?["number"]?.GetValue<int>() is not { } number || number < firstPage || number > lastPage)
            {
                continue;
            }

            var page = node.DeepClone().AsObject();
            page["number"] = number - firstPage + 1;
            kept.Add(page["id"]!.GetValue<string>());
            pages.Add(page);
        }

        var sections = new JsonArray();
        foreach (var node in root["sections"]?.AsArray() ?? [])
        {
            var pageId = node?["sources"]?.AsArray().FirstOrDefault()?["page"]?.GetValue<string>();
            if (pageId is not null && kept.Contains(pageId))
            {
                sections.Add(node!.DeepClone());
            }
        }

        root["pages"] = pages;
        root["sections"] = sections;
        return ExtractionDocumentJson.Read(root);
    }

    /// <summary>
    /// How much of each page's text each range of the rendered Markdown holds: by <paramref name="ranges"/>' index, the
    /// characters of the range per page number, counted through the elements rendered into it
    /// (<see cref="ExtractionDocumentMarkdown.Map"/>; a table cell counts, not its table again). An element read from
    /// several pages counts on each. Empty for a range no rendered element with a page overlaps.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<int, int>> TextPerPage(
        ExtractionDocumentMarkdownMap map,
        IReadOnlyList<(int Start, int End)> ranges)
    {
        var leaves = Leaves(map);

        var result = new List<IReadOnlyDictionary<int, int>>(ranges.Count);
        foreach (var (start, end) in ranges)
        {
            var perPage = new Dictionary<int, int>();
            foreach (var span in leaves)
            {
                var overlap = Math.Min(end, span.Offset + span.Length) - Math.Max(start, span.Offset);
                if (overlap <= 0)
                {
                    continue;
                }

                foreach (var number in span.Sources.Select(source => source.Page.PageNumber).OfType<int>().Distinct())
                {
                    perPage[number] = perPage.GetValueOrDefault(number) + overlap;
                }
            }

            result.Add(perPage);
        }

        return result;
    }

    /// <summary>
    /// The ranges of the rendered Markdown read from each page, by page number, in Markdown order: the ranges of the
    /// elements read from it (<see cref="ExtractionDocumentMarkdown.Map"/>; a table cell, not its whole table), with
    /// neighbours joined when only markup or whitespace lies between them. An element read from several pages is in
    /// each page's ranges. The shape of Azure Document Intelligence's <c>pages[].spans</c>.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<(int Offset, int Length)>> Spans(ExtractionDocumentMarkdownMap map)
    {
        var result = new SortedDictionary<int, IReadOnlyList<(int Offset, int Length)>>();
        var byPage = new Dictionary<int, List<(int Start, int End)>>();
        foreach (var span in Leaves(map))
        {
            foreach (var number in span.Sources.Select(source => source.Page.PageNumber).OfType<int>().Distinct())
            {
                if (!byPage.TryGetValue(number, out var runs))
                {
                    byPage[number] = runs = [];
                }

                var end = span.Offset + span.Length;
                if (runs.Count > 0 && runs[^1].End <= span.Offset && OnlyMarkup(map.Markdown, runs[^1].End, span.Offset))
                {
                    runs[^1] = (runs[^1].Start, end);
                }
                else if (runs.Count > 0 && runs[^1].End >= span.Offset)
                {
                    runs[^1] = (runs[^1].Start, Math.Max(runs[^1].End, end));
                }
                else
                {
                    runs.Add((span.Offset, end));
                }
            }
        }

        foreach (var (number, runs) in byPage)
        {
            result[number] = runs.Select(run => (run.Start, run.End - run.Start)).ToList();
        }

        return result;
    }

    /// <summary>
    /// Where on its page each element of the rendered Markdown was read, as a box in fractions of the page: one entry per
    /// element and page (<see cref="ExtractionDocumentMarkdown.Map"/>; a table cell, not its whole table), in Markdown
    /// order. Only a region that can be placed counts: a polygon, on a page with a number, a known coordinate origin and
    /// a size (or normalized coordinates). An OCR page that names only the page, or an engine that reports no page size,
    /// yields none; the page is still in <see cref="Spans"/>.
    /// <para>
    /// The box is the polygon's bounding box with its origin at the page's top-left corner, each coordinate a fraction of
    /// the page's width or height: the shape of Amazon Textract's <c>BoundingBox</c> and Google Document AI's normalized
    /// vertices. It needs no unit, so a consumer draws it on the page at whatever size it renders it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ExtractionDocumentPageRegion> Regions(ExtractionDocumentMarkdownMap map)
    {
        var result = new List<ExtractionDocumentPageRegion>();
        foreach (var span in Leaves(map))
        {
            foreach (var source in span.Sources)
            {
                if (Box(source) is { } box && box with { Offset = span.Offset, Length = span.Length } is var region
                    && !result.Contains(region))
                {
                    result.Add(region);
                }
            }
        }

        return result;
    }

    private static ExtractionDocumentPageRegion? Box(ExtractionDocumentSourceReference source)
    {
        var page = source.Page;
        if (page.PageNumber is not { } number || page.CoordinateOrigin is not { } origin
            || source.Polygon is not { Count: > 0 } polygon)
        {
            return null;
        }

        // Normalized coordinates are fractions already; any other unit needs the page's size in that unit.
        var (width, height) = page.CoordinateUnit == ExtractionDocumentCoordinateUnit.Normalized
            ? (1d, 1d)
            : page.Dimensions is { Width: > 0, Height: > 0 } size ? (size.Width, size.Height) : (0d, 0d);
        if (width <= 0)
        {
            return null;
        }

        var left = Fraction(polygon.Min(point => point.X), width);
        var right = Fraction(polygon.Max(point => point.X), width);
        var low = Fraction(polygon.Min(point => point.Y), height);
        var high = Fraction(polygon.Max(point => point.Y), height);
        double top, bottom;
        switch (origin)
        {
            case ExtractionDocumentCoordinateOrigin.TopLeft:
                (top, bottom) = (low, high);
                break;
            case ExtractionDocumentCoordinateOrigin.BottomLeft:
                (top, bottom) = (1 - high, 1 - low);
                break;
            default:
                return null;
        }

        return right > left && bottom > top
            ? new ExtractionDocumentPageRegion(0, 0, number, Round(left), Round(top), Round(right - left), Round(bottom - top))
            : null;
    }

    /// <summary>A coordinate as a fraction of the page's extent, kept on the page.</summary>
    private static double Fraction(double value, double extent) => Math.Clamp(value / extent, 0, 1);

    /// <summary>Four places: a tenth of a point on A4, and no binary noise in what is handed out.</summary>
    private static double Round(double value) => Math.Round(value, 4);

    /// <summary>
    /// The spans that contain no other: a table's span contains its cells' spans. In offset order (longest first at a
    /// tie), a span that contains others is directly followed by one of them.
    /// </summary>
    private static List<ExtractionDocumentMarkdownSpan> Leaves(ExtractionDocumentMarkdownMap map)
    {
        var ordered = map.Spans.OrderBy(span => span.Offset).ThenByDescending(span => span.Length).ToList();
        return ordered
            .Where((span, i) => i + 1 == ordered.Count || ordered[i + 1].Offset >= span.Offset + span.Length)
            .ToList();
    }

    /// <summary>Whether the text between two elements is only whitespace, Markdown punctuation and HTML tags (a table's).</summary>
    private static bool OnlyMarkup(string markdown, int start, int end)
    {
        var inTag = false;
        for (var i = start; i < end; i++)
        {
            var c = markdown[i];
            if (inTag)
            {
                inTag = c != '>';
            }
            else if (c == '<')
            {
                inTag = true;
            }
            else if (char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Where the Markdown range <c>[Offset, Offset + Length)</c> was read on page <see cref="PageNumber"/>: a box whose
/// origin is the page's top-left corner, each value a fraction of the page's width (<see cref="Left"/>,
/// <see cref="Width"/>) or height (<see cref="Top"/>, <see cref="Height"/>).
/// </summary>
public sealed record ExtractionDocumentPageRegion(
    int Offset,
    int Length,
    int PageNumber,
    double Left,
    double Top,
    double Width,
    double Height);

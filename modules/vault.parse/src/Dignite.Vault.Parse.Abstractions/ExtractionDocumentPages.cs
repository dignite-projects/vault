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

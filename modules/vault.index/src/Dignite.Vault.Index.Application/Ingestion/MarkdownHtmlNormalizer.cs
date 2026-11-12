using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Markdig;
using Markdig.Syntax;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Rewrites the HTML blocks of a document's Markdown that DataIngestion's <c>MarkdownReader</c> would drop, into
/// Markdown it reads. The reader turns a GitHub-flavored table into a table element, which <c>HeaderChunker</c> keeps
/// whole with the text around it, or splits by rows repeating the header row when it is too large; but it drops an
/// HTML block entirely, text and all. Azure Document Intelligence's layout model writes every table as HTML (for
/// merged cells), so without this every table of such a document would be missing from the index.
/// <list type="bullet">
/// <item>A table becomes a GFM table: the first row is the header row, a merged cell's text is repeated into every
/// cell it spans, and the caption becomes a paragraph above it.</item>
/// <item>A figure becomes its text: the caption, and any table inside it as a table.</item>
/// </list>
/// Other HTML blocks are left as they are. The result is stable: normalizing it again changes nothing.
/// </summary>
public static class MarkdownHtmlNormalizer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.IndexOf('<') < 0)
        {
            return markdown;
        }

        // Markdig finds the HTML blocks as CommonMark defines them, so a "<table>" inside a code block or inline in a
        // sentence is not touched.
        var blocks = Markdown.Parse(markdown, Pipeline)
            .Descendants<HtmlBlock>()
            .Where(b => b.Type == HtmlBlockType.InterruptingBlock)
            .OrderByDescending(b => b.Span.Start)
            .ToList();
        if (blocks.Count == 0)
        {
            return markdown;
        }

        var result = new StringBuilder(markdown);
        foreach (var block in blocks)
        {
            var html = markdown.Substring(block.Span.Start, block.Span.Length);
            var converted = Convert(html);
            if (converted is not null)
            {
                // Blank lines around it: an HTML block may follow a paragraph line directly, a GFM table may not.
                result.Remove(block.Span.Start, block.Span.Length).Insert(block.Span.Start, "\n\n" + converted + "\n\n");
            }
        }

        return result.ToString();
    }

    /// <summary>The block as Markdown, or <c>null</c> when it holds no table or figure and stays as it is.</summary>
    private static string? Convert(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var root = document.DocumentNode;
        if (root.SelectSingleNode("//table|//figure") is null)
        {
            return null;
        }

        var parts = new List<string>();
        AppendNodes(root.ChildNodes, parts);
        return string.Join("\n\n", parts);
    }

    private static void AppendNodes(IEnumerable<HtmlNode> nodes, List<string> parts)
    {
        foreach (var node in nodes)
        {
            switch (node.NodeType)
            {
                case HtmlNodeType.Comment:
                    continue;
                case HtmlNodeType.Text:
                    AddText(parts, node.InnerText);
                    continue;
            }

            switch (node.Name)
            {
                case "table":
                    AppendTable(node, parts);
                    break;
                case "figure":
                case "div":
                case "section":
                    // Containers: their own text and their tables, in order.
                    AppendNodes(node.ChildNodes, parts);
                    break;
                default:
                    if (node.SelectSingleNode(".//table") is not null)
                    {
                        AppendNodes(node.ChildNodes, parts);
                    }
                    else
                    {
                        AddText(parts, TextOf(node));
                    }

                    break;
            }
        }
    }

    private static void AppendTable(HtmlNode table, List<string> parts)
    {
        var caption = table.SelectSingleNode("./caption");
        if (caption is not null)
        {
            AddText(parts, TextOf(caption));
        }

        var grid = BuildGrid(table);
        if (grid.Count == 0)
        {
            return;
        }

        var width = grid.Max(r => r.Count);
        var lines = new List<string>(grid.Count + 1);
        for (var i = 0; i < grid.Count; i++)
        {
            var cells = Enumerable.Range(0, width).Select(c => c < grid[i].Count ? grid[i][c] ?? string.Empty : string.Empty);
            lines.Add("| " + string.Join(" | ", cells) + " |");
            if (i == 0)
            {
                lines.Add("|" + string.Concat(Enumerable.Repeat(" --- |", width)));
            }
        }

        parts.Add(string.Join("\n", lines));
    }

    /// <summary>The table's rows (not those of a nested table) with every merged cell's text in each cell it spans.</summary>
    private static List<List<string?>> BuildGrid(HtmlNode table)
    {
        var rows = table.SelectNodes("./tr|./thead/tr|./tbody/tr|./tfoot/tr")?.ToList() ?? [];
        var grid = new List<List<string?>>();
        for (var r = 0; r < rows.Count; r++)
        {
            while (grid.Count <= r)
            {
                grid.Add([]);
            }

            var column = 0;
            foreach (var cell in rows[r].ChildNodes.Where(n => n.Name is "td" or "th"))
            {
                while (column < grid[r].Count && grid[r][column] is not null)
                {
                    column++;
                }

                var text = EscapeCell(TextOf(cell));
                var rowSpan = Math.Clamp(cell.GetAttributeValue("rowspan", 1), 1, rows.Count - r);
                var colSpan = Math.Clamp(cell.GetAttributeValue("colspan", 1), 1, 1000);
                for (var dr = 0; dr < rowSpan; dr++)
                {
                    while (grid.Count <= r + dr)
                    {
                        grid.Add([]);
                    }

                    var target = grid[r + dr];
                    for (var dc = 0; dc < colSpan; dc++)
                    {
                        while (target.Count <= column + dc)
                        {
                            target.Add(null);
                        }

                        target[column + dc] = text;
                    }
                }

                column += colSpan;
            }
        }

        return grid.Where(r => r.Count > 0).ToList();
    }

    /// <summary>A node's visible text on one line: line breaks and runs of whitespace become one space.</summary>
    private static string TextOf(HtmlNode node)
    {
        foreach (var lineBreak in node.SelectNodes(".//br")?.ToList() ?? [])
        {
            lineBreak.ParentNode.ReplaceChild(node.OwnerDocument.CreateTextNode(" "), lineBreak);
        }

        return Whitespace.Replace(HtmlEntity.DeEntitize(node.InnerText), " ").Trim();
    }

    private static string EscapeCell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static void AddText(List<string> parts, string text)
    {
        var trimmed = Whitespace.Replace(HtmlEntity.DeEntitize(text), " ").Trim();
        if (trimmed.Length > 0)
        {
            parts.Add(trimmed);
        }
    }
}

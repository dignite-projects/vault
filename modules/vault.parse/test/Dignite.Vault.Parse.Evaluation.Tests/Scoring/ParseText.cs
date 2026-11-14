using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Dignite.Vault.Parse.Evaluation.Scoring;

/// <summary>
/// Reads a parser's Markdown the way the scorer compares it: plain text without markup, and the tables and headings it
/// declares (Markdown pipe tables and headings, and the HTML tables some providers write).
/// </summary>
public static partial class ParseText
{
    /// <summary>
    /// The comparison form of a text: NFKC (full-width punctuation and digits become their ASCII forms), lower case, and
    /// no whitespace at all. Whitespace carries no content here and a parser inserts it at every line wrap, which in
    /// Japanese has no word boundary to fall on.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>The text of Markdown (with any embedded HTML) without its markup, one line per block or cell.</summary>
    public static string StripMarkdown(string markdown)
    {
        var text = HtmlComment().Replace(markdown, "\n");
        text = HtmlTag().Replace(text, "\n");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("*[Image OCR]*", "\n").Replace("*[End OCR]*", "\n");
        text = OcrPageMarker().Replace(text, "\n");

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (TableSeparator().IsMatch(raw) || Rule().IsMatch(raw))
            {
                continue;
            }

            var line = HeadingPrefix().Replace(raw, string.Empty);
            line = ListPrefix().Replace(line, string.Empty);
            line = QuotePrefix().Replace(line, string.Empty);
            line = line.Replace("\\|", "\u0001").Replace("|", " ").Replace("\u0001", "|");
            line = line.Replace("**", string.Empty).Replace("`", string.Empty);
            line = Emphasis().Replace(line, "$1");
            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    /// <summary>Headings, from Markdown <c>#</c> lines and HTML <c>h1</c>–<c>h6</c>, in document order.</summary>
    public static IReadOnlyList<(int Level, string Text)> Headings(string markdown)
    {
        var headings = new List<(int Level, string Text)>();
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var match = MarkdownHeading().Match(line);
            if (match.Success)
            {
                headings.Add((match.Groups[1].Length, Normalize(StripInline(match.Groups[2].Value))));
            }
        }

        if (markdown.Contains("<h", StringComparison.OrdinalIgnoreCase))
        {
            var document = new HtmlDocument();
            document.LoadHtml(markdown);
            foreach (var node in document.DocumentNode.Descendants().Where(n => n.Name.Length == 2 && n.Name[0] == 'h' && char.IsDigit(n.Name[1])))
            {
                headings.Add((node.Name[1] - '0', Normalize(WebUtility.HtmlDecode(node.InnerText))));
            }
        }

        return headings;
    }

    /// <summary>
    /// The rows of every table the Markdown declares, as normalized cell texts: Markdown pipe tables and HTML tables
    /// (whose spans are expanded, a covered cell left empty as a pipe table writes it).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> Tables(string markdown)
    {
        var tables = new List<IReadOnlyList<IReadOnlyList<string>>>();
        List<IReadOnlyList<string>>? current = null;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('|') && trimmed.Count(c => c == '|') >= 2)
            {
                if (TableSeparator().IsMatch(trimmed))
                {
                    continue;
                }

                current ??= [];
                current.Add(SplitPipeRow(trimmed));
                continue;
            }

            if (current is { Count: > 0 })
            {
                tables.Add(current);
            }

            current = null;
        }

        if (current is { Count: > 0 })
        {
            tables.Add(current);
        }

        if (markdown.Contains("<table", StringComparison.OrdinalIgnoreCase))
        {
            var document = new HtmlDocument();
            document.LoadHtml(markdown);
            foreach (var table in document.DocumentNode.Descendants("table"))
            {
                tables.Add(HtmlTableRows(table));
            }
        }

        return tables;
    }

    private static List<string> SplitPipeRow(string row)
    {
        var inner = row.Trim().Trim('|');
        return inner.Replace("\\|", "\u0001")
            .Split('|')
            .Select(cell => Normalize(StripInline(WebUtility.HtmlDecode(HtmlTag().Replace(cell.Replace("\u0001", "|"), " ")))))
            .ToList();
    }

    private static List<IReadOnlyList<string>> HtmlTableRows(HtmlNode table)
    {
        var grid = new List<string?[]>();
        var rowIndex = 0;
        foreach (var tr in table.Descendants("tr").Where(tr => tr.Ancestors("table").First() == table))
        {
            while (grid.Count <= rowIndex)
            {
                grid.Add([]);
            }

            var column = 0;
            foreach (var cell in tr.Elements("td").Concat(tr.Elements("th")).OrderBy(n => n.StreamPosition))
            {
                while (column < grid[rowIndex].Length && grid[rowIndex][column] is not null)
                {
                    column++;
                }

                var rowSpan = Math.Max(1, cell.GetAttributeValue("rowspan", 1));
                var colSpan = Math.Max(1, cell.GetAttributeValue("colspan", 1));
                var text = Normalize(WebUtility.HtmlDecode(cell.InnerText));
                for (var r = rowIndex; r < rowIndex + rowSpan; r++)
                {
                    while (grid.Count <= r)
                    {
                        grid.Add([]);
                    }

                    for (var c = column; c < column + colSpan; c++)
                    {
                        if (grid[r].Length <= c)
                        {
                            var widened = new string?[c + 1];
                            grid[r].CopyTo(widened, 0);
                            grid[r] = widened;
                        }

                        grid[r][c] = r == rowIndex && c == column ? text : string.Empty;
                    }
                }

                column += colSpan;
            }

            rowIndex++;
        }

        return grid.Select(row => (IReadOnlyList<string>)row.Select(c => c ?? string.Empty).ToList()).ToList();
    }

    private static string StripInline(string text)
        => Emphasis().Replace(text.Replace("**", string.Empty).Replace("`", string.Empty), "$1");

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\*\[Image OCR p:[^\]]*\]\*")]
    private static partial Regex OcrPageMarker();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^\s*([-*+•]|\d+[.)])\s+")]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"^\s*>\s?")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"(?<![\w*])\*([^*\s][^*]*?)\*(?![\w*])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex MarkdownHeading();
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace Dignite.Vault.Parse;

/// <summary>
/// The page metadata Azure Document Intelligence's layout model writes into its Markdown output as HTML comments
/// (https://learn.microsoft.com/azure/ai-services/document-intelligence/concept/markdown-elements#pagebreak): a
/// <c>&lt;!-- PageBreak --&gt;</c> line between two pages, and <c>&lt;!-- PageHeader="…" --&gt;</c>,
/// <c>&lt;!-- PageFooter="…" --&gt;</c> and <c>&lt;!-- PageNumber="…" --&gt;</c> for a page's furniture.
/// <para>
/// Vault reads it from Markdown a client produced from a document itself, so the pages the client marks reach the
/// document tree as they would from an engine that knows pages, and the furniture stays out of the text. Vault does
/// not write it: the Markdown it stores has no page metadata, the pages live in the document tree.
/// </para>
/// <para>
/// A comment counts only on a line of its own, indented by at most three spaces and outside a fenced code block, as
/// CommonMark reads an HTML block; anywhere else it is text.
/// </para>
/// </summary>
public static partial class PageMarkup
{
    /// <summary>The line between two pages.</summary>
    public const string PageBreak = "<!-- PageBreak -->";

    /// <summary>Whether <paramref name="markdown"/> marks a page break anywhere.</summary>
    public static bool HasPageBreak(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || markdown.IndexOf("PageBreak", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        var lines = Lines(markdown);
        var code = FigureMarkup.CodeLines(lines);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!code[i] && BreakLine().IsMatch(lines[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pages of <paramref name="markdown"/>, numbered from 1: one per run between page breaks, or a single page
    /// when it marks none. Each page's text is its lines without the page metadata; its headers and footers are
    /// returned beside it, and page numbers are dropped (the page's position says it).
    /// </summary>
    public static IReadOnlyList<PageMarkupPage> Split(string markdown)
    {
        var lines = Lines(markdown);
        var code = FigureMarkup.CodeLines(lines);
        var pages = new List<PageMarkupPage>();
        var text = new List<string>();
        var headers = new List<string>();
        var footers = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (code[i])
            {
                text.Add(lines[i]);
                continue;
            }

            if (BreakLine().IsMatch(lines[i]))
            {
                pages.Add(new PageMarkupPage(pages.Count + 1, string.Join('\n', text), headers.ToArray(), footers.ToArray()));
                text.Clear();
                headers.Clear();
                footers.Clear();
                continue;
            }

            var furniture = FurnitureLine().Match(lines[i]);
            if (furniture.Success)
            {
                var value = WebUtility.HtmlDecode(furniture.Groups["value"].Value).Trim();
                switch (furniture.Groups["kind"].Value)
                {
                    case "Header" when value.Length > 0:
                        headers.Add(value);
                        break;
                    case "Footer" when value.Length > 0:
                        footers.Add(value);
                        break;
                }

                continue;
            }

            text.Add(lines[i]);
        }

        pages.Add(new PageMarkupPage(pages.Count + 1, string.Join('\n', text), headers.ToArray(), footers.ToArray()));
        return pages;
    }

    private static string[] Lines(string markdown) => markdown.Replace("\r\n", "\n").Split('\n');

    [GeneratedRegex(@"^ {0,3}<!--\s*PageBreak\s*-->\s*$")]
    private static partial Regex BreakLine();

    [GeneratedRegex(@"^ {0,3}<!--\s*Page(?<kind>Header|Footer|Number)\s*=\s*""(?<value>.*)""\s*-->\s*$")]
    private static partial Regex FurnitureLine();
}

/// <summary>
/// A page <see cref="PageMarkup.Split"/> found: its 1-based <see cref="Number"/>, its Markdown without page metadata,
/// and the running headers and footers marked on it.
/// </summary>
public sealed record PageMarkupPage(int Number, string Text, IReadOnlyList<string> Headers, IReadOnlyList<string> Footers);

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;

namespace Dignite.Vault.Parse;

/// <summary>
/// Reads Markdown into the elements of an <see cref="ExtractionDocument"/>: what an engine that answers in Markdown (a
/// vision model, MarkItDown, PP-StructureV3) knows of a document's structure. The inverse of
/// <see cref="ExtractionDocumentMarkdown"/>, so reading and rendering again gives the same Markdown up to blank lines.
/// <para>
/// Headings keep their level; a paragraph, a list item and a table cell keep their inline Markdown as written; a list
/// item records its depth and marker; a pipe table and an HTML table become a table, the HTML one with its spans; an
/// <see cref="ImageOcrMarkup"/> span becomes an image whose text is the transcription; a fenced block is code. Anything
/// else (raw HTML, a rule, an indented block) is kept as written under <see cref="ParseElementRoles.Markdown"/>. An
/// indented block in engine output is rarely code: it is the continuation of a construct CommonMark does not know, such
/// as the second paragraph of a footnote definition, and read as code it would come back fenced.
/// </para>
/// </summary>
public static partial class MarkdownDocumentReader
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UsePreciseSourceLocation()
        .Build();

    /// <summary>A document of one section holding <paramref name="markdown"/>.</summary>
    public static ExtractionDocument Read(string markdown, string identifier)
    {
        var document = new ExtractionDocument(identifier);
        var section = new ExtractionDocumentSection();
        document.Sections.Add(section);
        ReadInto(document, section, markdown);
        return document;
    }

    /// <summary>
    /// Appends the blocks of <paramref name="markdown"/> to <paramref name="section"/>, each referring to
    /// <paramref name="page"/> when one is given. An image whose markup names a page without one given refers to that
    /// page, added to the document's source pages.
    /// </summary>
    public static void ReadInto(
        ExtractionDocument document,
        ExtractionDocumentSection section,
        string markdown,
        ExtractionDocumentSourcePage? page = null)
    {
        foreach (var part in SplitFigures(markdown.Replace("\r\n", "\n")))
        {
            if (part.IsFigure)
            {
                var image = new ExtractionDocumentImageElement(text: part.Text);
                if ((page ?? PageNumbered(document, part.FigurePage)) is { } figurePage)
                {
                    image.Sources.Add(new ExtractionDocumentSourceReference(figurePage));
                }

                section.Elements.Add(image);
                continue;
            }

            var parsed = Markdown.Parse(part.Text, Pipeline);
            foreach (var block in parsed)
            {
                AddBlock(section.Elements, block, part.Text, page, level: 0);
            }
        }
    }

    private static void AddBlock(List<ExtractionDocumentElement> into, Block block, string source, ExtractionDocumentSourcePage? page, int level)
    {
        switch (block)
        {
            case BlankLineBlock:
                return;

            case HeadingBlock heading:
                into.Add(Sourced(new ExtractionDocumentTextElement(new TextContent(HeadingText(heading, Raw(source, heading))), ParseElementRoles.Heading)
                {
                    HeadingLevel = heading.Level
                }, page));
                return;

            case ParagraphBlock paragraph:
                into.Add(Sourced(new ExtractionDocumentTextElement(new TextContent(Raw(source, paragraph))), page));
                return;

            case ListBlock list:
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var marker = list.IsOrdered
                        ? item.Order.ToString(CultureInfo.InvariantCulture) + list.OrderedDelimiter
                        : list.BulletType.ToString();
                    var text = string.Join("\n\n", item.OfType<ParagraphBlock>().Select(p => Dedent(Raw(source, p))));
                    var content = new TextContent(text)
                    {
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            [ParseContentProperties.ListLevel] = level,
                            [ParseContentProperties.ListMarker] = marker
                        }
                    };
                    into.Add(Sourced(new ExtractionDocumentTextElement(content, ParseElementRoles.ListItem), page));
                    foreach (var child in item.Where(child => child is not ParagraphBlock))
                    {
                        AddBlock(into, child, source, page, child is ListBlock ? level + 1 : level);
                    }
                }

                return;

            case Table table:
                into.Add(Sourced(ReadPipeTable(table, source), page));
                return;

            case HtmlBlock html when ReadHtmlTable(Raw(source, html)) is { } htmlTable:
                into.Add(Sourced(htmlTable, page));
                return;

            case FencedCodeBlock fenced:
                into.Add(Sourced(Code(fenced.Lines.ToString(), fenced.Info), page));
                return;

            case QuoteBlock quote:
                into.Add(Sourced(new ExtractionDocumentTextElement(
                    new TextContent(string.Join('\n', Raw(source, quote).Split('\n').Select(line => QuotePrefix().Replace(line, string.Empty)))),
                    ParseElementRoles.Quote), page));
                return;

            default:
                if (RawLines(source, block) is { Length: > 0 } raw)
                {
                    into.Add(Sourced(new ExtractionDocumentTextElement(new TextContent(raw), ParseElementRoles.Markdown), page));
                }

                return;
        }
    }

    private static ExtractionDocumentTextElement Code(string text, string? language)
    {
        var content = new TextContent(text.TrimEnd('\n'));
        if (!string.IsNullOrWhiteSpace(language))
        {
            content.AdditionalProperties = new AdditionalPropertiesDictionary { [ParseContentProperties.CodeLanguage] = language.Trim() };
        }

        return new ExtractionDocumentTextElement(content, ParseElementRoles.Code);
    }

    private static ExtractionDocumentTableElement ReadPipeTable(Table table, string source)
    {
        // A pipe table's cells come in column order and span nothing; Markdig sets TableCell.ColumnIndex only for grid tables.
        var rows = table.OfType<TableRow>().ToList();
        var columns = Math.Max(1, rows.Count == 0 ? 0 : rows.Max(row => row.OfType<TableCell>().Count()));
        var element = new ExtractionDocumentTableElement(null, rows.Count, columns);
        for (var r = 0; r < rows.Count; r++)
        {
            var c = 0;
            foreach (var cell in rows[r].OfType<TableCell>())
            {
                var text = BreakTag().Replace(Raw(source, cell).Trim().Replace("\\|", "|"), "\n");
                element.Cells[r, c++] = Cell(text, 1, 1, rows[r].IsHeader ? ParseElementRoles.ColumnHeader : null);
            }
        }

        return element;
    }

    /// <summary>A table when <paramref name="html"/> is one HTML table and nothing else; <c>null</c> otherwise.</summary>
    private static ExtractionDocumentTableElement? ReadHtmlTable(string html)
    {
        var trimmed = html.Trim();
        if (!trimmed.StartsWith("<table", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith("</table>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parsed = new HtmlDocument();
        parsed.LoadHtml(trimmed);
        var tables = parsed.DocumentNode.Descendants("table").ToList();
        if (tables.Count != 1)
        {
            return null;
        }

        // Lay the anchors out on a grid first: a span pushes the cells after it right and down.
        var anchors = new List<(int Row, int Column, int RowSpan, int ColumnSpan, string Text, bool Header)>();
        var covered = new HashSet<(int, int)>();
        var rowIndex = 0;
        foreach (var tr in tables[0].Descendants("tr"))
        {
            var column = 0;
            foreach (var td in tr.Elements("td").Concat(tr.Elements("th")).OrderBy(node => node.StreamPosition))
            {
                while (covered.Contains((rowIndex, column)))
                {
                    column++;
                }

                var rowSpan = Math.Max(1, td.GetAttributeValue("rowspan", 1));
                var columnSpan = Math.Max(1, td.GetAttributeValue("colspan", 1));
                for (var r = rowIndex; r < rowIndex + rowSpan; r++)
                {
                    for (var c = column; c < column + columnSpan; c++)
                    {
                        covered.Add((r, c));
                    }
                }

                var text = WebUtility.HtmlDecode(BreakTag().Replace(td.InnerHtml, "\n").Trim());
                anchors.Add((rowIndex, column, rowSpan, columnSpan, HtmlTag().Replace(text, string.Empty).Trim(), td.Name == "th"));
                column += columnSpan;
            }

            rowIndex++;
        }

        if (anchors.Count == 0)
        {
            return null;
        }

        var rowCount = anchors.Max(a => a.Row + a.RowSpan);
        var columnCount = anchors.Max(a => a.Column + a.ColumnSpan);
        var element = new ExtractionDocumentTableElement(null, rowCount, columnCount);
        foreach (var anchor in anchors)
        {
            element.Cells[anchor.Row, anchor.Column] = Cell(anchor.Text, anchor.RowSpan, anchor.ColumnSpan, anchor.Header ? ParseElementRoles.ColumnHeader : null);
        }

        return element;
    }

    private static ExtractionDocumentTableCell Cell(string text, int rowSpan, int columnSpan, string? role)
        => text.Length == 0
            ? new ExtractionDocumentTableCell(rowSpan, columnSpan, role)
            : new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent(text)), rowSpan, columnSpan, role);

    private static T Sourced<T>(T element, ExtractionDocumentSourcePage? page)
        where T : ExtractionDocumentElement
    {
        if (page is not null)
        {
            element.Sources.Add(new ExtractionDocumentSourceReference(page));
        }

        return element;
    }

    private static string Raw(string source, MarkdownObject block)
        => block.Span.IsEmpty || block.Span.Start < 0 ? string.Empty : source.Substring(block.Span.Start, Math.Min(block.Span.Length, source.Length - block.Span.Start)).TrimEnd();

    /// <summary>The block's text from the start of its first line, so an indented block keeps its indentation.</summary>
    private static string RawLines(string source, MarkdownObject block)
    {
        if (block.Span.IsEmpty || block.Span.Start < 0 || block.Span.Start > source.Length)
        {
            return string.Empty;
        }

        var lineStart = block.Span.Start == 0 ? 0 : source.LastIndexOf('\n', block.Span.Start - 1) + 1;
        var end = Math.Min(block.Span.End + 1, source.Length);
        return source[lineStart..end].TrimEnd();
    }

    private static string HeadingText(HeadingBlock heading, string raw)
        => heading.IsSetext
            ? string.Join(' ', raw.Split('\n').SkipLast(1).Select(line => line.Trim()))
            : AtxClosing().Replace(AtxOpening().Replace(raw, string.Empty), string.Empty).Trim();

    /// <summary>A list item's continuation lines, without the indentation that put them under its marker.</summary>
    private static string Dedent(string raw)
        => string.Join('\n', raw.Split('\n').Select((line, index) => index == 0 ? line : line.TrimStart()));

    private static ExtractionDocumentSourcePage? PageNumbered(ExtractionDocument document, int? number)
    {
        if (number is not { } n)
        {
            return null;
        }

        var existing = document.SourcePages.FirstOrDefault(p => p.PageNumber == n);
        if (existing is not null)
        {
            return existing;
        }

        var page = new ExtractionDocumentSourcePage("page-" + n.ToString(CultureInfo.InvariantCulture), n);
        document.SourcePages.Add(page);
        return page;
    }

    /// <summary>The Markdown between figure transcriptions, and each transcription; an open marker never closed is text.</summary>
    private static IEnumerable<(bool IsFigure, string Text, int? FigurePage)> SplitFigures(string markdown)
    {
        var lines = markdown.Split('\n');
        var text = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (ImageOcrMarkup.IsOpenLine(lines[i]))
            {
                var close = Array.FindIndex(lines, i + 1, line => ImageOcrMarkup.IsCloseLine(line));
                if (close > 0)
                {
                    if (text.Count > 0)
                    {
                        yield return (false, string.Join('\n', text), null);
                        text.Clear();
                    }

                    yield return (true, string.Join('\n', lines[(i + 1)..close]).Trim('\n'), ImageOcrMarkup.TryParsePage(lines[i]));
                    i = close;
                    continue;
                }
            }

            text.Add(lines[i]);
        }

        if (text.Count > 0)
        {
            yield return (false, string.Join('\n', text), null);
        }
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}[ \t]*")]
    private static partial Regex AtxOpening();

    [GeneratedRegex(@"[ \t]+#+[ \t]*$|^#+[ \t]*$")]
    private static partial Regex AtxClosing();

    [GeneratedRegex(@"^\s{0,3}>\s?")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();
}

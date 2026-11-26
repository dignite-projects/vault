using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;

namespace Dignite.Vault.Parse;

/// <summary>
/// Renders an <see cref="ExtractionDocument"/> as the Markdown Vault stores and hands out (<c>Document.Markdown</c>).
/// <para>
/// One block per element, a blank line between blocks: a heading at its level, a paragraph's text as written (it may
/// carry inline Markdown), consecutive list items as one list, a table as a pipe table, or as an HTML table when a cell
/// spans rows or columns (a pipe table cannot say so), and an image as a figure (<see cref="FigureMarkup"/>): its OCR
/// text inside, its alternative text as the caption. A line of text that would read as a figure's opening or closing
/// tag is escaped (<see cref="FigureMarkup.Escape"/>).
/// Page headers and footers stay in the tree and out of the Markdown. Sections only group; a section's own text payload
/// is a paragraph.
/// </para>
/// </summary>
public static class ExtractionDocumentMarkdown
{
    public static string Render(ExtractionDocument document) => Map(document).Markdown;

    /// <summary>
    /// Renders <paramref name="document"/> and records where each element landed: one span per paragraph, heading, list
    /// item, image and table, and one per table cell, each with the element's (or cell's) source references. A range of
    /// the Markdown leads back to the page regions it was read from through the spans that overlap it.
    /// </summary>
    public static ExtractionDocumentMarkdownMap Map(ExtractionDocument document)
    {
        var writer = new Writer();
        foreach (var section in document.Sections)
        {
            Collect(section, writer);
        }

        return new ExtractionDocumentMarkdownMap(writer.Text.ToString(), writer.Spans);
    }

    private static void Collect(ExtractionDocumentElement element, Writer writer)
    {
        switch (element)
        {
            case ExtractionDocumentSection section:
                if (section.Content is TextContent { Text.Length: > 0 } payload)
                {
                    writer.Block(FigureMarkup.Escape(payload.Text.TrimEnd()), section.Sources);
                }

                var items = new List<ExtractionDocumentTextElement>();
                foreach (var child in section.Elements)
                {
                    if (child is ExtractionDocumentTextElement { Role: ParseElementRoles.ListItem } item)
                    {
                        items.Add(item);
                        continue;
                    }

                    if (items.Count > 0)
                    {
                        WriteList(items, writer);
                        items.Clear();
                    }

                    Collect(child, writer);
                }

                if (items.Count > 0)
                {
                    WriteList(items, writer);
                }

                break;

            case ExtractionDocumentTextElement text:
                if (RenderText(text) is { Length: > 0 } rendered)
                {
                    writer.Block(FigureMarkup.Escape(rendered), text.Sources);
                }

                break;

            case ExtractionDocumentTableElement table when table.Cells.RowCount > 0 && table.Cells.ColumnCount > 0:
                writer.BeginBlock();
                var tableStart = writer.Position;
                var cells = table.Cells;
                if (cells.Any(cell => cell is not null && (cell.RowSpan > 1 || cell.ColumnSpan > 1)))
                {
                    WriteHtmlTable(table, writer);
                }
                else
                {
                    WritePipeTable(table, writer);
                }

                writer.Span(tableStart, table.Sources);
                break;

            case ExtractionDocumentImageElement image
                when !string.IsNullOrWhiteSpace(image.Text) || !string.IsNullOrWhiteSpace(image.AlternativeText):
                // Numbered in render order, so each figure's opening line is unique in the document.
                writer.Block(FigureMarkup.Write(image.Text, image.AlternativeText, FigureMarkup.Id(++writer.Figures)), image.Sources);
                break;
        }
    }

    private static string? RenderText(ExtractionDocumentTextElement element)
    {
        var text = ((TextContent)element.Content!).Text.TrimEnd();
        switch (element.Role)
        {
            case ParseElementRoles.PageHeader or ParseElementRoles.PageFooter:
                return null;

            case ParseElementRoles.Heading:
                var level = Math.Clamp(element.HeadingLevel ?? 1, 1, 6);
                return new string('#', level) + " " + string.Join(' ', text.Split('\n', StringSplitOptions.TrimEntries));

            case ParseElementRoles.Code:
                var language = Property<string>(element, ParseContentProperties.CodeLanguage);
                var fence = CodeFence(text);
                return fence + language + "\n" + text + "\n" + fence;

            case ParseElementRoles.Quote:
                return string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line));

            default:
                return text;
        }
    }

    /// <summary>
    /// A backtick fence longer than any run of backticks that starts a line of <paramref name="code"/>, so that no line of
    /// it closes the block early (CommonMark closes a fence with a run of the same character at least as long).
    /// </summary>
    private static string CodeFence(string code)
    {
        var longest = 0;
        foreach (var line in code.Split('\n'))
        {
            var start = 0;
            while (start < line.Length && start < 3 && line[start] == ' ')
            {
                start++;
            }

            var end = start;
            while (end < line.Length && line[end] == '`')
            {
                end++;
            }

            longest = Math.Max(longest, end - start);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    /// <summary>
    /// Consecutive list items as one list. A nested item is indented to its parent's text, which depends on the parent's
    /// marker: <c>- </c> indents two columns, <c>10. </c> four.
    /// </summary>
    private static void WriteList(List<ExtractionDocumentTextElement> items, Writer writer)
    {
        writer.BeginBlock();
        var parents = new Stack<(int Level, int ContentColumn)>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var level = Math.Max(0, Property<int?>(item, ParseContentProperties.ListLevel) ?? 0);
            var marker = Property<string>(item, ParseContentProperties.ListMarker) is { Length: > 0 } written ? written : "-";
            while (parents.Count > 0 && parents.Peek().Level >= level)
            {
                parents.Pop();
            }

            var indent = parents.Count > 0 ? parents.Peek().ContentColumn : 0;
            var contentColumn = indent + marker.Length + 1;
            var text = FigureMarkup.Escape(((TextContent)item.Content!).Text.TrimEnd()).Split('\n');
            if (index > 0)
            {
                writer.Append("\n");
            }

            var start = writer.Position;
            writer.Append(new string(' ', indent) + marker + " " + text[0]);
            foreach (var line in text.Skip(1))
            {
                writer.Append("\n" + (line.Length == 0 ? string.Empty : new string(' ', contentColumn) + line));
            }

            writer.Span(start, item.Sources);
            parents.Push((level, contentColumn));
        }
    }

    private static void WritePipeTable(ExtractionDocumentTableElement table, Writer writer)
    {
        var cells = table.Cells;
        for (var row = 0; row < cells.RowCount; row++)
        {
            if (row > 0)
            {
                writer.Append("\n");
            }

            writer.Append("| ");
            for (var column = 0; column < cells.ColumnCount; column++)
            {
                if (column > 0)
                {
                    writer.Append(" | ");
                }

                var cell = cells[row, column];
                var start = writer.Position;
                writer.Append(CellText(cell).Replace("|", "\\|").Replace("\n", "<br>"));
                writer.Span(start, CellSources(cell));
            }

            writer.Append(" |");
            if (row == 0)
            {
                writer.Append("\n|" + string.Concat(Enumerable.Repeat(" --- |", cells.ColumnCount)));
            }
        }
    }

    private static void WriteHtmlTable(ExtractionDocumentTableElement table, Writer writer)
    {
        var cells = table.Cells;
        writer.Append("<table>\n");
        for (var row = 0; row < cells.RowCount; row++)
        {
            writer.Append("<tr>");
            for (var column = 0; column < cells.ColumnCount; column++)
            {
                if (cells[row, column] is not { } cell)
                {
                    continue;
                }

                var tag = cell.Role == ParseElementRoles.ColumnHeader ? "th" : "td";
                var open = new StringBuilder("<").Append(tag);
                if (cell.RowSpan > 1)
                {
                    open.Append(" rowspan=\"").Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                if (cell.ColumnSpan > 1)
                {
                    open.Append(" colspan=\"").Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                writer.Append(open.Append('>').ToString());
                var start = writer.Position;
                writer.Append(WebUtility.HtmlEncode(CellText(cell)).Replace("\n", "<br>"));
                writer.Span(start, CellSources(cell));
                writer.Append("</" + tag + ">");
            }

            writer.Append("</tr>\n");
        }

        writer.Append("</table>");
    }

    /// <summary>A cell's own region, or its content's when the engine put the region there.</summary>
    private static IEnumerable<ExtractionDocumentSourceReference> CellSources(ExtractionDocumentTableCell? cell)
        => cell is null ? [] : cell.Sources.Any() ? cell.Sources : cell.Content?.Sources ?? [];

    private static string CellText(ExtractionDocumentTableCell? cell) => cell?.Content switch
    {
        null => string.Empty,
        ExtractionDocumentTextElement text => ((TextContent)text.Content!).Text.Trim(),
        ExtractionDocumentImageElement image => image.Text?.Trim() ?? string.Empty,
        ExtractionDocumentSection section => string.Join('\n', section.Elements.Select(child => child switch
        {
            ExtractionDocumentTextElement text => ((TextContent)text.Content!).Text.Trim(),
            ExtractionDocumentImageElement image => image.Text?.Trim() ?? string.Empty,
            _ => string.Empty
        }).Where(line => line.Length > 0)),
        _ => string.Empty
    };

    /// <summary>A property of an element's text payload, read back whether it was set in memory or deserialized.</summary>
    internal static T? Property<T>(ExtractionDocumentTextElement element, string key)
    {
        if (element.Content?.AdditionalProperties?.TryGetValue(key, out var value) != true || value is null)
        {
            return default;
        }

        return value switch
        {
            T typed => typed,
            JsonElement json => json.Deserialize<T>(),
            IConvertible convertible when Nullable.GetUnderlyingType(typeof(T)) is { } underlying
                => (T)Convert.ChangeType(convertible, underlying, CultureInfo.InvariantCulture),
            _ => default
        };
    }

    /// <summary>Appends blocks a blank line apart and records element spans as it goes.</summary>
    private sealed class Writer
    {
        private bool _started;

        public StringBuilder Text { get; } = new();

        public List<ExtractionDocumentMarkdownSpan> Spans { get; } = [];

        public int Position => Text.Length;

        /// <summary>How many figures have been written.</summary>
        public int Figures { get; set; }

        public void BeginBlock()
        {
            if (_started)
            {
                Text.Append("\n\n");
            }

            _started = true;
        }

        public void Block(string text, IEnumerable<ExtractionDocumentSourceReference> sources)
        {
            BeginBlock();
            var start = Position;
            Text.Append(text);
            Span(start, sources);
        }

        public void Append(string text) => Text.Append(text);

        /// <summary>Records <c>[start, Position)</c>; an empty range or one without a source leads nowhere and is skipped.</summary>
        public void Span(int start, IEnumerable<ExtractionDocumentSourceReference> sources)
        {
            var list = sources.ToList();
            if (Position > start && list.Count > 0)
            {
                Spans.Add(new ExtractionDocumentMarkdownSpan(start, Position - start, list));
            }
        }
    }
}

/// <summary>The Markdown <see cref="ExtractionDocumentMarkdown.Map"/> rendered and where each element landed in it.</summary>
public sealed class ExtractionDocumentMarkdownMap
{
    public ExtractionDocumentMarkdownMap(string markdown, IReadOnlyList<ExtractionDocumentMarkdownSpan> spans)
    {
        Markdown = markdown;
        Spans = spans;
    }

    public string Markdown { get; }

    /// <summary>In document order; a table's span contains its cells' spans.</summary>
    public IReadOnlyList<ExtractionDocumentMarkdownSpan> Spans { get; }

    /// <summary>
    /// The page regions <c>[offset, offset + length)</c> was read from: the sources of the narrowest spans that overlap
    /// it — a table cell rather than its table. Empty when no rendered element with a source overlaps it.
    /// </summary>
    public IReadOnlyList<ExtractionDocumentSourceReference> SourcesOf(int offset, int length)
    {
        var end = offset + Math.Max(length, 1);
        var overlapping = Spans.Where(span => span.Offset < end && offset < span.Offset + span.Length).ToList();

        // A span that contains another overlapping span (a table around its cells) is the coarser answer: drop it.
        var narrowest = overlapping.Where(outer => !overlapping.Any(inner =>
            inner.Length < outer.Length
            && inner.Offset >= outer.Offset
            && inner.Offset + inner.Length <= outer.Offset + outer.Length));

        return narrowest.SelectMany(span => span.Sources).Distinct().ToList();
    }
}

/// <summary>One element's (or table cell's) place in the rendered Markdown, with where on the page it was read.</summary>
public sealed record ExtractionDocumentMarkdownSpan(int Offset, int Length, IReadOnlyList<ExtractionDocumentSourceReference> Sources);

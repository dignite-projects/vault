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
/// spans rows or columns (a pipe table cannot say so), and an image's OCR text inside <see cref="ImageOcrMarkup"/>.
/// Page headers and footers stay in the tree and out of the Markdown. Sections only group; a section's own text payload
/// is a paragraph.
/// </para>
/// </summary>
public static class ExtractionDocumentMarkdown
{
    public static string Render(ExtractionDocument document)
    {
        var blocks = new List<string>();
        foreach (var section in document.Sections)
        {
            Collect(section, blocks);
        }

        return string.Join("\n\n", blocks);
    }

    private static void Collect(ExtractionDocumentElement element, List<string> blocks)
    {
        switch (element)
        {
            case ExtractionDocumentSection section:
                if (section.Content is TextContent { Text.Length: > 0 } payload)
                {
                    blocks.Add(payload.Text.TrimEnd());
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
                        blocks.Add(RenderList(items));
                        items.Clear();
                    }

                    Collect(child, blocks);
                }

                if (items.Count > 0)
                {
                    blocks.Add(RenderList(items));
                }

                break;

            case ExtractionDocumentTextElement text:
                if (RenderText(text) is { Length: > 0 } rendered)
                {
                    blocks.Add(rendered);
                }

                break;

            case ExtractionDocumentTableElement table when table.Cells.RowCount > 0 && table.Cells.ColumnCount > 0:
                blocks.Add(RenderTable(table));
                break;

            case ExtractionDocumentImageElement image when !string.IsNullOrWhiteSpace(image.Text):
                blocks.Add(ImageOcrMarkup.Wrap(image.Text.Trim('\n'), image.Sources.FirstOrDefault()?.Page.PageNumber));
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
                return "```" + language + "\n" + text + "\n```";

            case ParseElementRoles.Quote:
                return string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line));

            default:
                return text;
        }
    }

    /// <summary>
    /// Consecutive list items as one list. A nested item is indented to its parent's text, which depends on the parent's
    /// marker: <c>- </c> indents two columns, <c>10. </c> four.
    /// </summary>
    private static string RenderList(List<ExtractionDocumentTextElement> items)
    {
        var lines = new List<string>();
        var parents = new Stack<(int Level, int ContentColumn)>();
        foreach (var item in items)
        {
            var level = Math.Max(0, Property<int?>(item, ParseContentProperties.ListLevel) ?? 0);
            var marker = Property<string>(item, ParseContentProperties.ListMarker) is { Length: > 0 } written ? written : "-";
            while (parents.Count > 0 && parents.Peek().Level >= level)
            {
                parents.Pop();
            }

            var indent = parents.Count > 0 ? parents.Peek().ContentColumn : 0;
            var contentColumn = indent + marker.Length + 1;
            var text = ((TextContent)item.Content!).Text.TrimEnd().Split('\n');
            lines.Add(new string(' ', indent) + marker + " " + text[0]);
            lines.AddRange(text.Skip(1).Select(line => line.Length == 0 ? string.Empty : new string(' ', contentColumn) + line));
            parents.Push((level, contentColumn));
        }

        return string.Join('\n', lines);
    }

    private static string RenderTable(ExtractionDocumentTableElement table)
    {
        var cells = table.Cells;
        var spans = cells.Any(cell => cell is not null && (cell.RowSpan > 1 || cell.ColumnSpan > 1));
        return spans ? RenderHtmlTable(table) : RenderPipeTable(table);
    }

    private static string RenderPipeTable(ExtractionDocumentTableElement table)
    {
        var cells = table.Cells;
        var lines = new List<string>(cells.RowCount + 1);
        for (var row = 0; row < cells.RowCount; row++)
        {
            var values = new string[cells.ColumnCount];
            for (var column = 0; column < cells.ColumnCount; column++)
            {
                values[column] = CellText(cells[row, column]).Replace("|", "\\|").Replace("\n", "<br>");
            }

            lines.Add("| " + string.Join(" | ", values) + " |");
            if (row == 0)
            {
                lines.Add("|" + string.Concat(Enumerable.Repeat(" --- |", cells.ColumnCount)));
            }
        }

        return string.Join('\n', lines);
    }

    private static string RenderHtmlTable(ExtractionDocumentTableElement table)
    {
        var cells = table.Cells;
        var html = new StringBuilder("<table>\n");
        for (var row = 0; row < cells.RowCount; row++)
        {
            html.Append("<tr>");
            for (var column = 0; column < cells.ColumnCount; column++)
            {
                if (cells[row, column] is not { } cell)
                {
                    continue;
                }

                var tag = cell.Role == ParseElementRoles.ColumnHeader ? "th" : "td";
                html.Append('<').Append(tag);
                if (cell.RowSpan > 1)
                {
                    html.Append(" rowspan=\"").Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                if (cell.ColumnSpan > 1)
                {
                    html.Append(" colspan=\"").Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                html.Append('>')
                    .Append(WebUtility.HtmlEncode(CellText(cell)).Replace("\n", "<br>"))
                    .Append("</").Append(tag).Append('>');
            }

            html.Append("</tr>\n");
        }

        return html.Append("</table>").ToString();
    }

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
}

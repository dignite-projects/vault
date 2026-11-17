using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig.Core;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// What the PDF engine reads a page into, in reading order, before it becomes elements of the document tree. Every
/// block keeps its bounding box in PdfPig's frame (points, origin bottom-left), which becomes the element's polygon.
/// <para>
/// A paragraph's and a heading's text is inline Markdown already (escaped source text with its bold / italic runs); a
/// table's and a list's text is source text, escaped when it becomes an element.
/// </para>
/// </summary>
internal abstract record PdfBlock(PdfRectangle Bounds);

internal sealed record PdfParagraphBlock(PdfRectangle Bounds, string Markdown) : PdfBlock(Bounds);

internal sealed record PdfHeadingBlock(PdfRectangle Bounds, int Level, string Markdown) : PdfBlock(Bounds);

/// <summary>A bullet list: one source-text item per row of a marker column (the marker itself is dropped).</summary>
internal sealed record PdfListBlock(PdfRectangle Bounds, IReadOnlyList<string> Items) : PdfBlock(Bounds);

internal sealed record PdfTableBlock(PdfRectangle Bounds, PdfTable Table) : PdfBlock(Bounds);

/// <summary>An embedded image's OCR transcription (Markdown, as the OCR engine answered), with the caption bound to it.</summary>
internal sealed record PdfFigureBlock(PdfRectangle Bounds, string Transcription, string? Caption, int? PageNumber) : PdfBlock(Bounds);

/// <summary>
/// A reconstructed table: anchor cells on a <see cref="RowCount"/> × <see cref="ColumnCount"/> grid, the first row its
/// header. A cell covered by another's span has no anchor.
/// </summary>
internal sealed class PdfTable(int rowCount, int columnCount)
{
    public int RowCount { get; } = rowCount;

    public int ColumnCount { get; } = columnCount;

    public List<PdfTableCell> Cells { get; } = new();

    /// <summary>A table of plain rows: every cell spans one row and one column.</summary>
    public static PdfTable FromRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var table = new PdfTable(rows.Count, columns);
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                table.Cells.Add(new PdfTableCell(r, c, 1, 1, c < rows[r].Count ? rows[r][c] : string.Empty, null));
            }
        }

        return table;
    }
}

/// <summary>A table cell: its position and span, its source text, and its region on the page when known.</summary>
internal readonly record struct PdfTableCell(int Row, int Column, int RowSpan, int ColumnSpan, string Text, PdfRectangle? Bounds);

/// <summary>Turns <see cref="PdfBlock"/>s into elements of the document tree, and into Markdown the way Vault stores it.</summary>
internal static class PdfElements
{
    /// <summary>
    /// The elements of <paramref name="blocks"/>, each referring to its region of <paramref name="page"/>. Without a page,
    /// a figure still refers to the page it was found on, which the Markdown of an image names.
    /// </summary>
    public static IEnumerable<ExtractionDocumentElement> Create(IEnumerable<PdfBlock> blocks, ExtractionDocumentSourcePage? page)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case PdfHeadingBlock heading:
                    yield return Sourced(
                        new ExtractionDocumentTextElement(new TextContent(heading.Markdown), ParseElementRoles.Heading) { HeadingLevel = heading.Level },
                        page,
                        heading.Bounds);
                    break;

                case PdfParagraphBlock paragraph:
                    yield return Sourced(new ExtractionDocumentTextElement(new TextContent(paragraph.Markdown)), page, paragraph.Bounds);
                    break;

                case PdfListBlock list:
                    foreach (var item in list.Items)
                    {
                        var content = new TextContent(MarkdownText.EscapeInline(item))
                        {
                            AdditionalProperties = new AdditionalPropertiesDictionary
                            {
                                [ParseContentProperties.ListLevel] = 0,
                                [ParseContentProperties.ListMarker] = "-"
                            }
                        };
                        yield return Sourced(new ExtractionDocumentTextElement(content, ParseElementRoles.ListItem), page, list.Bounds);
                    }

                    break;

                case PdfTableBlock table:
                    yield return Sourced(Table(table.Table, page), page, table.Bounds);
                    break;

                case PdfFigureBlock figure:
                    if (figure.Caption is { Length: > 0 } caption)
                    {
                        yield return Sourced(new ExtractionDocumentTextElement(new TextContent(MarkdownText.EscapeBlockText(caption))), page, figure.Bounds);
                    }

                    var image = new ExtractionDocumentImageElement(text: figure.Transcription);
                    if (page is not null)
                    {
                        image.Sources.Add(new ExtractionDocumentSourceReference(page, Polygon(figure.Bounds)));
                    }
                    else if (figure.PageNumber is { } number)
                    {
                        image.Sources.Add(new ExtractionDocumentSourceReference(
                            new ExtractionDocumentSourcePage("page-" + number.ToString(CultureInfo.InvariantCulture), number)));
                    }

                    yield return image;
                    break;
            }
        }
    }

    /// <summary>The Markdown Vault stores for <paramref name="blocks"/>: the tree renderer's, so it cannot drift from it.</summary>
    public static string ToMarkdown(IEnumerable<PdfBlock> blocks)
    {
        var document = new ExtractionDocument("pdf");
        var section = new ExtractionDocumentSection();
        document.Sections.Add(section);
        section.Elements.AddRange(Create(blocks, page: null));
        return ExtractionDocumentMarkdown.Render(document);
    }

    /// <summary>A rectangle in PdfPig's frame as a polygon: top-left, top-right, bottom-right, bottom-left.</summary>
    public static IEnumerable<ExtractionDocumentPoint> Polygon(PdfRectangle bounds)
    {
        var left = Math.Min(bounds.Left, bounds.Right);
        var right = Math.Max(bounds.Left, bounds.Right);
        var bottom = Math.Min(bounds.Bottom, bounds.Top);
        var top = Math.Max(bounds.Bottom, bounds.Top);
        return
        [
            new ExtractionDocumentPoint(left, top),
            new ExtractionDocumentPoint(right, top),
            new ExtractionDocumentPoint(right, bottom),
            new ExtractionDocumentPoint(left, bottom)
        ];
    }

    private static ExtractionDocumentTableElement Table(PdfTable table, ExtractionDocumentSourcePage? page)
    {
        var element = new ExtractionDocumentTableElement(null, table.RowCount, table.ColumnCount);
        foreach (var cell in table.Cells)
        {
            // Source text, escaped as a paragraph's is; a newline would end a pipe-table row.
            var text = MarkdownText.EscapeInline(cell.Text).Replace("\r", " ").Replace("\n", " ").Trim();
            var role = cell.Row == 0 ? ParseElementRoles.ColumnHeader : null;
            var tableCell = text.Length == 0
                ? new ExtractionDocumentTableCell(cell.RowSpan, cell.ColumnSpan, role)
                : new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent(text)), cell.RowSpan, cell.ColumnSpan, role);
            if (page is not null && cell.Bounds is { } bounds)
            {
                tableCell.Sources.Add(new ExtractionDocumentSourceReference(page, Polygon(bounds)));
            }

            element.Cells[cell.Row, cell.Column] = tableCell;
        }

        return element;
    }

    private static T Sourced<T>(T element, ExtractionDocumentSourcePage? page, PdfRectangle bounds)
        where T : ExtractionDocumentElement
    {
        if (page is not null)
        {
            element.Sources.Add(new ExtractionDocumentSourceReference(page, Polygon(bounds)));
        }

        return element;
    }
}

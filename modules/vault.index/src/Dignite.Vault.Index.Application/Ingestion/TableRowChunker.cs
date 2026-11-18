using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// <see cref="ChunkingStrategy.TableRow"/>: every data row of a table becomes a chunk of its own, holding the heading
/// path, the table's header row and the row, and everything else is chunked by <see cref="HeaderChunker"/>.
/// <para>
/// The unit is RAGFlow's <c>table</c> method (infiniflow/ragflow, Apache-2.0,
/// <c>internal/ingestion/component/chunker/table.go</c>): one chunk per data row with the header repeated, a row with no
/// text skipped, and a table with no data row kept whole. RAGFlow renders the row as an HTML table; here it is a
/// Markdown table, the format of the chunks around it. The text between tables goes through DataIngestion's own
/// <see cref="HeaderChunker"/> with the headings above it, so it is cut exactly as the heading strategy cuts it.
/// </para>
/// </summary>
public sealed class TableRowChunker : IngestionChunker<string>
{
    private const int MaxHeaderLevel = 10;

    private readonly HeaderChunker _headerChunker;

    public TableRowChunker(IngestionChunkerOptions options)
    {
        _headerChunker = new HeaderChunker(options);
    }

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IngestionDocument document,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var headers = new IngestionDocumentHeader?[MaxHeaderLevel + 1];
        var headersBefore = new List<IngestionDocumentHeader>();
        var pending = new List<IngestionDocumentElement>();

        foreach (var element in document.EnumerateContent())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is IngestionDocumentHeader header)
            {
                var level = Math.Clamp(header.Level.GetValueOrDefault(), 0, MaxHeaderLevel);
                headers[level] = header;
                headers.AsSpan(level + 1).Clear();
                pending.Add(header);
                continue;
            }

            if (element is IngestionDocumentTable table && DataRows(table) is { Count: > 0 } rows)
            {
                await foreach (var chunk in FlushAsync(document, headersBefore, pending, cancellationToken))
                {
                    yield return chunk;
                }

                headersBefore = headers.OfType<IngestionDocumentHeader>().ToList();
                var context = string.Join(" ", headersBefore.Select(h => h.GetMarkdown()));
                var head = Row(table, 0) + "\n" + string.Concat(Enumerable.Repeat("| --- ", table.Cells.GetLength(1))) + "|";
                foreach (var row in rows)
                {
                    var content = (context.Length == 0 ? string.Empty : context + "\n") + head + "\n" + Row(table, row);
                    yield return new IngestionChunk<string>(content, document, context.Length == 0 ? null : context);
                }

                continue;
            }

            pending.Add(element);
        }

        await foreach (var chunk in FlushAsync(document, headersBefore, pending, cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Chunks the elements gathered since the last table with <see cref="HeaderChunker"/>, preceded by the headings that
    /// were in force before them, so their heading paths are what the heading strategy would give.
    /// </summary>
    private async IAsyncEnumerable<IngestionChunk<string>> FlushAsync(
        IngestionDocument document,
        List<IngestionDocumentHeader> headersBefore,
        List<IngestionDocumentElement> pending,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (pending.All(e => e is IngestionDocumentHeader))
        {
            pending.Clear();
            yield break;
        }

        var part = new IngestionDocument(document.Identifier);
        var section = new IngestionDocumentSection();
        foreach (var element in headersBefore.Concat(pending))
        {
            section.Elements.Add(element);
        }

        part.Sections.Add(section);
        pending.Clear();
        await foreach (var chunk in _headerChunker.ProcessAsync(part, cancellationToken))
        {
            yield return new IngestionChunk<string>(chunk.Content, document, chunk.Context);
        }
    }

    /// <summary>The indexes of the rows after the header row that hold any text.</summary>
    private static List<int> DataRows(IngestionDocumentTable table)
    {
        var rows = new List<int>();
        for (var row = 1; row < table.Cells.GetLength(0); row++)
        {
            for (var column = 0; column < table.Cells.GetLength(1); column++)
            {
                if (!string.IsNullOrWhiteSpace(CellText(table.Cells[row, column])?.Replace(IndexChunker.EmptyCell, string.Empty)))
                {
                    rows.Add(row);
                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>A row as a Markdown table row, cell by cell as DataIngestion's own chunkers write it.</summary>
    private static string Row(IngestionDocumentTable table, int row)
    {
        var text = new StringBuilder();
        for (var column = 0; column < table.Cells.GetLength(1); column++)
        {
            text.Append("| ").Append(CellText(table.Cells[row, column])?.Trim()).Append(' ');
        }

        return text.Append('|').ToString();
    }

    private static string? CellText(IngestionDocumentElement? cell) => cell switch
    {
        null => null,
        IngestionDocumentImage image => image.AlternativeText ?? image.Text,
        _ => cell.GetMarkdown()
    };
}

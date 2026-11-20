using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Index.Indexing;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// What a chunk holds besides running text (<see cref="ChunkContent"/>), read from the document's Markdown as it was
/// written, not from the chunk's text: the chunker rewrites an HTML table as a GFM table and a figure as its text
/// (<see cref="MarkdownHtmlNormalizer"/>), so a chunk no longer shows that it came from a figure.
/// <para>
/// The blocks are what CommonMark reads: a GFM table, an HTML block opening with <c>&lt;table</c>, and a figure from the
/// HTML block opening with <c>&lt;figure</c> to the one holding <c>&lt;/figure&gt;</c> (Azure Document Intelligence's
/// figure markup, which Extract writes: the transcription between them is Markdown of its own). Tags inside a code
/// block are text.
/// </para>
/// </summary>
public static class IndexChunkContent
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    /// <summary>The table and figure blocks of <paramref name="markdown"/>, as Markdown ranges.</summary>
    public static IReadOnlyList<(int Start, int End, ChunkContent Content)> Blocks(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || (markdown.IndexOf('|') < 0 && markdown.IndexOf('<') < 0))
        {
            return [];
        }

        var document = Markdown.Parse(markdown, Pipeline);
        var blocks = document.Descendants<Table>()
            .Select(table => (table.Span.Start, table.Span.End + 1, ChunkContent.Table))
            .ToList();

        int? figureStart = null;
        foreach (var html in document.Descendants<HtmlBlock>().OrderBy(block => block.Span.Start))
        {
            var text = html.Lines.ToString().TrimStart();
            if (text.StartsWith("<table", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add((html.Span.Start, html.Span.End + 1, ChunkContent.Table));
            }

            if (Opens(text, "<figure"))
            {
                figureStart ??= html.Span.Start;
            }

            if (figureStart is { } start && text.Contains("</figure>", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add((start, html.Span.End + 1, ChunkContent.Figure));
                figureStart = null;
            }
        }

        return blocks;
    }

    /// <summary>
    /// What the Markdown range a chunk came from holds: every kind of block it overlaps; <see cref="ChunkContent.Text"/>
    /// when none, <c>null</c> when the chunk was not found in the Markdown.
    /// </summary>
    public static ChunkContent? Of((int Offset, int Length)? range, IReadOnlyList<(int Start, int End, ChunkContent Content)> blocks)
    {
        if (range is not { } r)
        {
            return null;
        }

        var end = r.Offset + r.Length;
        return blocks
            .Where(block => block.Start < end && r.Offset < block.End)
            .Aggregate(ChunkContent.Text, (content, block) => content | block.Content);
    }

    /// <summary>Whether <paramref name="text"/> opens with <paramref name="tag"/> as a whole tag name.</summary>
    private static bool Opens(string text, string tag)
        => text.StartsWith(tag, StringComparison.OrdinalIgnoreCase)
           && (text.Length == tag.Length || text[tag.Length] is ' ' or '>' or '\t' or '\r' or '\n');
}

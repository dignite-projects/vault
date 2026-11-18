using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// One chunk before it is stored: its position, its heading path, its text, and what an enricher added (keywords
/// space-separated, questions one per line), when one ran.
/// </summary>
public sealed record IndexChunkDraft(int Ordinal, string? HeadingPath, string Text, string? Keywords = null, string? Questions = null);

/// <summary>
/// Splits a document's Markdown with DataIngestion: <see cref="MarkdownReader"/> builds the document tree, then the
/// profile's <see cref="ChunkingStrategy"/> cuts it to the token budget: <see cref="HeaderChunker"/> (the default:
/// chunks follow the heading tree and carry their heading path), <see cref="DocumentTokenChunker"/> (for text without
/// headings), the whole document as one chunk, <see cref="TableRowChunker"/> (a chunk per table row), or a chunk per
/// page of the file. <see cref="HeaderChunker"/> is table-aware: a table stays whole with the text around it, and a
/// table larger than the budget is split by rows, each piece repeating the header row. <see cref="Prepare"/> makes sure
/// every table reaches it as a table.
/// </summary>
public class IndexChunker : ITransientDependency
{
    /// <summary>
    /// Token counts only size the chunks, so any modern BPE vocabulary does; o200k_base handles Japanese and Chinese
    /// far better than the older ones. It is not tied to the embedding model, which may not be an OpenAI one.
    /// </summary>
    private static readonly Lazy<Tokenizer> SharedTokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

    /// <summary>
    /// Stands in an empty table cell while the reader parses: DataIngestion's Markdown reader (11.0.0-preview.1) throws on
    /// a cell with no text, so a single empty cell failed the whole document. A zero-width space is text to Markdig and
    /// whitespace to nobody, and the chunks drop it again. Remove once dotnet/extensions#7956 is fixed.
    /// </summary>
    internal const string EmptyCell = "\u200B";

    /// <summary>A cell with nothing but spaces between two pipes of a table row; the opening pipe is not escaped.</summary>
    private static readonly Regex EmptyTableCell = new(@"(?<=(?<!\\)\|)[ \t]*(?=\|)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The text that is chunked: the document's Markdown with its HTML tables and figures rewritten as Markdown
    /// (<see cref="MarkdownHtmlNormalizer"/>), which the reader would otherwise drop, and its empty table cells held
    /// open (<see cref="EmptyCell"/>). Its hash is what tells a rebuild whether the chunks can change.
    /// </summary>
    public virtual string Prepare(string markdown) => HoldEmptyCellsOpen(MarkdownHtmlNormalizer.Normalize(markdown));

    /// <summary>Puts <see cref="EmptyCell"/> in every empty cell of every line that starts as a table row.</summary>
    protected static string HoldEmptyCellsOpen(string markdown)
    {
        if (!markdown.Contains('|'))
        {
            return markdown;
        }

        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('|'))
            {
                lines[i] = EmptyTableCell.Replace(lines[i], " " + EmptyCell + " ");
            }
        }

        return string.Join('\n', lines);
    }

    /// <param name="markdown">The document's Markdown as Extract holds it; it is prepared here.</param>
    /// <param name="pages">
    /// The ranges of <paramref name="markdown"/> read from each page (Extract's layout), for
    /// <see cref="ChunkingStrategy.Page"/>; without them a page profile chunks by headings.
    /// </param>
    public virtual async Task<IReadOnlyList<IndexChunkDraft>> ChunkAsync(
        string markdown,
        string identifier,
        ResolvedIndexProfile profile,
        IngestionChunkProcessor<string>? enricher = null,
        IReadOnlyList<DocumentPageSpansDto>? pages = null,
        CancellationToken cancellationToken = default)
    {
        var options = new IngestionChunkerOptions(SharedTokenizer.Value)
        {
            MaxTokensPerChunk = profile.MaxTokensPerChunk,
            OverlapTokens = profile.OverlapTokens
        };

        // DataIngestion's order: the chunker, then the chunk processors over its output.
        var chunks = profile.Strategy == ChunkingStrategy.Page && pages is { Count: > 0 }
            ? PageChunksAsync(markdown, identifier, pages, options, cancellationToken)
            : DocumentChunksAsync(Prepare(markdown), identifier, profile.Strategy, options, cancellationToken);
        if (enricher is not null)
        {
            chunks = enricher.ProcessAsync(chunks, cancellationToken);
        }

        var drafts = new List<IndexChunkDraft>();
        await foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk.Content))
            {
                continue;
            }

            var headingPath = string.IsNullOrWhiteSpace(chunk.Context)
                ? null
                : TextTruncator.AtCharBoundary(chunk.Context, IndexingConsts.MaxHeadingPathLength);
            drafts.Add(new IndexChunkDraft(
                drafts.Count,
                headingPath,
                chunk.Content.Replace(EmptyCell, string.Empty),
                Join(chunk, IndexChunkEnricher.KeywordsKey, " ", IndexingConsts.MaxKeywordsLength),
                Join(chunk, IndexChunkEnricher.QuestionsKey, "\n", IndexingConsts.MaxQuestionsLength)));
        }

        return drafts;
    }

    /// <summary>The chunks of one prepared Markdown text, by <paramref name="strategy"/>.</summary>
    protected virtual async IAsyncEnumerable<IngestionChunk<string>> DocumentChunksAsync(
        string prepared,
        string identifier,
        ChunkingStrategy strategy,
        IngestionChunkerOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var document = await ReadAsync(prepared, identifier, cancellationToken);
        if (strategy == ChunkingStrategy.Whole && Fits(prepared, options))
        {
            yield return new IngestionChunk<string>(prepared.Trim(), document);
            yield break;
        }

        IngestionChunker<string> chunker = strategy switch
        {
            ChunkingStrategy.Token => new DocumentTokenChunker(options),
            ChunkingStrategy.TableRow => new TableRowChunker(options),
            _ => new HeaderChunker(options)
        };
        await foreach (var chunk in chunker.ProcessAsync(document, cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// A chunk per page: the Markdown is cut where each page's first range starts (what precedes the first page goes
    /// with it), and a page over the budget is chunked by headings. RAGFlow's <c>presentation</c> method keeps a page
    /// whole (infiniflow/ragflow, Apache-2.0, <c>internal/ingestion/component/chunker/page.go</c>); here a page still has
    /// to fit the embedding model, so the budget splits it.
    /// </summary>
    protected virtual async IAsyncEnumerable<IngestionChunk<string>> PageChunksAsync(
        string markdown,
        string identifier,
        IReadOnlyList<DocumentPageSpansDto> pages,
        IngestionChunkerOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var starts = pages
            .Where(p => p.Spans.Count > 0)
            .Select(p => Math.Clamp(p.Spans.Min(s => s.Offset), 0, markdown.Length))
            .Distinct()
            .Order()
            .ToList();
        if (starts.Count == 0)
        {
            starts.Add(0);
        }

        starts[0] = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : markdown.Length;
            var prepared = Prepare(markdown[starts[i]..end]);
            if (string.IsNullOrWhiteSpace(prepared))
            {
                continue;
            }

            if (Fits(prepared, options))
            {
                yield return new IngestionChunk<string>(prepared.Trim(), new IngestionDocument(identifier));
                continue;
            }

            await foreach (var chunk in DocumentChunksAsync(prepared, identifier, ChunkingStrategy.Heading, options, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private static async Task<IngestionDocument> ReadAsync(string prepared, string identifier, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(prepared));
        return await new MarkdownReader().ReadAsync(stream, identifier, "text/markdown", cancellationToken);
    }

    private static bool Fits(string text, IngestionChunkerOptions options)
        => SharedTokenizer.Value.CountTokens(text) <= options.MaxTokensPerChunk;

    private static string? Join(IngestionChunk<string> chunk, string key, string separator, int maxLength)
    {
        if (!chunk.HasMetadata || !chunk.Metadata.TryGetValue(key, out var value) || value is not IEnumerable<string> values)
        {
            return null;
        }

        var joined = string.Join(separator, values);
        return joined.Length == 0 ? null : TextTruncator.AtCharBoundary(joined, maxLength);
    }
}

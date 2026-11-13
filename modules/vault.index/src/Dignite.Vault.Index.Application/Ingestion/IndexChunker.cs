using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
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
/// Splits a document's Markdown with DataIngestion: <see cref="MarkdownReader"/> builds the document tree, then
/// <see cref="HeaderChunker"/> (the default: chunks follow the heading tree and carry their heading path) or
/// <see cref="DocumentTokenChunker"/> (for text without headings) cuts it to the profile's token budget.
/// <see cref="HeaderChunker"/> is table-aware: a table stays whole with the text around it, and a table larger than the
/// budget is split by rows, each piece repeating the header row. <see cref="Prepare"/> makes sure every table reaches
/// it as a table.
/// </summary>
public class IndexChunker : ITransientDependency
{
    /// <summary>
    /// Token counts only size the chunks, so any modern BPE vocabulary does; o200k_base handles Japanese and Chinese
    /// far better than the older ones. It is not tied to the embedding model, which may not be an OpenAI one.
    /// </summary>
    private static readonly Lazy<Tokenizer> SharedTokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

    /// <summary>
    /// The text that is chunked: the document's Markdown with its HTML tables and figures rewritten as Markdown
    /// (<see cref="MarkdownHtmlNormalizer"/>), which the reader would otherwise drop. Its hash is what tells a rebuild
    /// whether the chunks can change.
    /// </summary>
    public virtual string Prepare(string markdown) => MarkdownHtmlNormalizer.Normalize(markdown);

    public virtual async Task<IReadOnlyList<IndexChunkDraft>> ChunkAsync(
        string markdown,
        string identifier,
        ResolvedIndexProfile profile,
        IngestionChunkProcessor<string>? enricher = null,
        CancellationToken cancellationToken = default)
    {
        markdown = Prepare(markdown);
        var reader = new MarkdownReader();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        var document = await reader.ReadAsync(stream, identifier, "text/markdown", cancellationToken);

        var options = new IngestionChunkerOptions(SharedTokenizer.Value)
        {
            MaxTokensPerChunk = profile.MaxTokensPerChunk,
            OverlapTokens = profile.OverlapTokens
        };
        IngestionChunker<string> chunker = profile.Strategy == ChunkingStrategy.Token
            ? new DocumentTokenChunker(options)
            : new HeaderChunker(options);

        // DataIngestion's order: the chunker, then the chunk processors over its output.
        var chunks = chunker.ProcessAsync(document, cancellationToken);
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
                chunk.Content,
                Join(chunk, IndexChunkEnricher.KeywordsKey, " ", IndexingConsts.MaxKeywordsLength),
                Join(chunk, IndexChunkEnricher.QuestionsKey, "\n", IndexingConsts.MaxQuestionsLength)));
        }

        return drafts;
    }

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

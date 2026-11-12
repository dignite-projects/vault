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

/// <summary>One chunk before it is stored: its position, its heading path and its text.</summary>
public sealed record IndexChunkDraft(int Ordinal, string? HeadingPath, string Text);

/// <summary>
/// Splits a document's Markdown with DataIngestion: <see cref="MarkdownReader"/> builds the document tree, then
/// <see cref="HeaderChunker"/> (the default: chunks follow the heading tree and carry their heading path) or
/// <see cref="DocumentTokenChunker"/> (for text without headings) cuts it to the profile's token budget.
/// </summary>
public class IndexChunker : ITransientDependency
{
    /// <summary>
    /// Token counts only size the chunks, so any modern BPE vocabulary does; o200k_base handles Japanese and Chinese
    /// far better than the older ones. It is not tied to the embedding model, which may not be an OpenAI one.
    /// </summary>
    private static readonly Lazy<Tokenizer> SharedTokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

    public virtual async Task<IReadOnlyList<IndexChunkDraft>> ChunkAsync(
        string markdown,
        string identifier,
        ResolvedIndexProfile profile,
        CancellationToken cancellationToken = default)
    {
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

        var drafts = new List<IndexChunkDraft>();
        await foreach (var chunk in chunker.ProcessAsync(document, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chunk.Content))
            {
                continue;
            }

            var headingPath = string.IsNullOrWhiteSpace(chunk.Context)
                ? null
                : TextTruncator.AtCharBoundary(chunk.Context, IndexingConsts.MaxHeadingPathLength);
            drafts.Add(new IndexChunkDraft(drafts.Count, headingPath, chunk.Content));
        }

        return drafts;
    }
}

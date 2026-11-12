using System;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>Host configuration for ingestion, section <c>Vault:IndexIngestion</c>.</summary>
public class VaultIndexIngestionOptions
{
    /// <summary>
    /// The longest Markdown Index indexes, in characters. Above it the document ends in a terminal failure rather
    /// than being cut: a truncated index would answer for text it never saw.
    /// </summary>
    public int MaxMarkdownLength { get; set; } = 2_000_000;

    /// <summary>The most chunks one document may produce; above it the document ends in a terminal failure.</summary>
    public int MaxChunksPerDocument { get; set; } = 5_000;

    /// <summary>How many chunks go to the embedding model in one call.</summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>
    /// Transient failures (a model timeout, a database blip) allowed before a document ends in
    /// <c>IndexedDocumentStatus.Failed</c>. ABP keeps retrying a failing job with no count limit of its own.
    /// </summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>
    /// How long an index job's claim on a document holds. A claim older than this is treated as abandoned by a
    /// crashed process, so the document can be indexed again.
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How many document ids one backfill page enqueues (at most 1000, Extract's page cap).</summary>
    public int BackfillPageSize { get; set; } = 200;
}

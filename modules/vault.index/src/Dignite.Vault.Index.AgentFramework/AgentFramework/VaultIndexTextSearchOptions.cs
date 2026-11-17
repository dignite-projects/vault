using System;
using Dignite.Vault.Index.Search;

namespace Dignite.Vault.Index.AgentFramework;

/// <summary>What one <see cref="VaultIndexTextSearch"/> search asks Index for, and which hits it hands the agent.</summary>
public class VaultIndexTextSearchOptions
{
    /// <summary>Only documents of this type (Extract's type code); every type the caller may read when empty.</summary>
    public string? DocumentTypeCode { get; set; }

    /// <summary>How many passages, 1 to <see cref="IndexSearchConsts.MaxResultCount"/>; Index's default when empty.</summary>
    public int? MaxResultCount { get; set; }

    /// <summary>
    /// When the reranker ordered the hits, the lowest score a passage may have to reach the agent; <c>null</c> keeps
    /// every hit. Without a reranker, or when the rerank call failed, every hit is kept. With
    /// <c>BAAI/bge-reranker-v2-m3</c> 0.05 turned away 17 of 20 questions no document answers and dropped 6 of 127
    /// answerable ones, mostly asked in another language than the document's (see the abstention report in the Index
    /// evaluation). A turn that searches before every run ("thanks", "go on") then adds no passages.
    /// </summary>
    public double? MinRerankScore { get; set; }

    /// <summary>
    /// The link the agent cites for a passage, such as the consuming application's page for the document; none when
    /// empty. Index has no public document URL of its own.
    /// </summary>
    public Func<IndexSearchHitDto, string?>? SourceLink { get; set; }
}

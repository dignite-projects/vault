namespace Dignite.Vault.Index.Indexing;

/// <summary>How an index profile splits a document's Markdown into chunks.</summary>
public enum ChunkingStrategy
{
    /// <summary>By the heading tree; every chunk carries its full heading path. Long sections split by paragraph.</summary>
    Heading = 0,

    /// <summary>A target token count with overlap, split at paragraph and sentence boundaries, for text without headings.</summary>
    Token = 1
}

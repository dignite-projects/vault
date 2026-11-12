namespace Dignite.Vault.Index.Indexing;

/// <summary>What a compiled artifact is built from.</summary>
public enum ArtifactLevel
{
    /// <summary>One document (the navigation tree, section summaries, the document summary). Readable by whoever reads the document.</summary>
    Document = 0,

    /// <summary>All documents of one type (catalog, wiki, timeline). Readable only by whoever reads the whole type.</summary>
    DocumentType = 1
}

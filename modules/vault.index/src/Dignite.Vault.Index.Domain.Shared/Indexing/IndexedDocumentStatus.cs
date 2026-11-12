namespace Dignite.Vault.Index.Indexing;

/// <summary>Where an Extract document stands in Index.</summary>
public enum IndexedDocumentStatus
{
    /// <summary>Known to Index, not indexed yet.</summary>
    Pending = 0,

    /// <summary>An index job is running for it.</summary>
    Indexing = 1,

    /// <summary>Its chunks are searchable.</summary>
    Indexed = 2,

    /// <summary>The last job ended in a terminal failure; the run record says why.</summary>
    Failed = 3
}

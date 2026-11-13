namespace Dignite.Vault.Index.Indexing;

/// <summary>Where a document's compiled artifact stands. The values are serialized: never renumber them.</summary>
public enum IndexArtifactState
{
    /// <summary>The document's type compiles none, and there is none.</summary>
    None = 0,

    /// <summary>The type compiles it, and it has not been compiled yet.</summary>
    Pending = 1,

    /// <summary>Compiled from the document's chunks as they are now.</summary>
    Current = 2,

    /// <summary>
    /// Compiled from chunks that have changed since, or by an older template: not served until it is compiled again.
    /// </summary>
    Outdated = 3
}

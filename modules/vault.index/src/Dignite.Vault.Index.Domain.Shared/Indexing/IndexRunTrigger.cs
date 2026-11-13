namespace Dignite.Vault.Index.Indexing;

/// <summary>Why an index run started.</summary>
public enum IndexRunTrigger
{
    /// <summary>A <c>DocumentReadyEto</c> from Extract.</summary>
    DocumentReady = 0,

    /// <summary>The first backfill of a tenant's Ready documents.</summary>
    Backfill = 1,

    /// <summary>A rebuild after an index profile changed.</summary>
    Rebuild = 2,

    /// <summary>An operator asked for it.</summary>
    Manual = 3,

    /// <summary>The embedding model changed: the document's chunks, as they are, embedded into the new generation.</summary>
    ModelChange = 4
}

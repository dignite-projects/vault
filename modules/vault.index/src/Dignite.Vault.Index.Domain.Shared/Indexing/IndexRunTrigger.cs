namespace Dignite.Vault.Index.Indexing;

/// <summary>Why an index run started.</summary>
public enum IndexRunTrigger
{
    /// <summary>A <c>DocumentReadyEto</c> from Extract.</summary>
    DocumentReady = 0,

    /// <summary>The first backfill of a tenant's Ready documents.</summary>
    Backfill = 1,

    /// <summary>A rebuild after an index profile or the embedding model changed.</summary>
    Rebuild = 2,

    /// <summary>An operator asked for it.</summary>
    Manual = 3
}

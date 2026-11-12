namespace Dignite.Vault.Index.Indexing;

/// <summary>The life of one embedding generation: one model, one dimension.</summary>
public enum IndexGenerationStatus
{
    /// <summary>Vectors are being produced; search does not read it yet.</summary>
    Building = 0,

    /// <summary>The one generation search reads.</summary>
    Serving = 1,

    /// <summary>Replaced by a newer generation; its vectors can be deleted.</summary>
    Retired = 2
}

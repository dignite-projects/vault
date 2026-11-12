namespace Dignite.Vault.Index.Indexing;

/// <summary>The bounds of an index profile's chunking settings.</summary>
public static class IndexProfileConsts
{
    /// <summary>The smallest chunk a profile may ask for; below it a chunk is rarely a whole thought.</summary>
    public const int MinMaxTokensPerChunk = 100;

    /// <summary>The largest chunk a profile may ask for; embedding models read about 8,000 tokens at most.</summary>
    public const int MaxMaxTokensPerChunk = 4_000;
}

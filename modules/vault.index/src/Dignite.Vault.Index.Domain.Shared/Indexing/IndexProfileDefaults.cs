namespace Dignite.Vault.Index.Indexing;

/// <summary>
/// What a document type is chunked with when neither it nor its layer has an index profile. Provisional: the design
/// leaves chunk size to the evaluation set (implementation step 6), so these are a reasonable start, not a finding.
/// </summary>
public static class IndexProfileDefaults
{
    public const ChunkingStrategy Strategy = ChunkingStrategy.Heading;

    public const int MaxTokensPerChunk = 400;

    public const int OverlapTokens = 0;
}

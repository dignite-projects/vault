using Dignite.Vault.Index.Indexing;

namespace Dignite.Vault.Index.Profiles;

/// <summary>The chunking settings that apply to one document type, wherever they came from.</summary>
public sealed record ResolvedIndexProfile(ChunkingStrategy Strategy, int MaxTokensPerChunk, int OverlapTokens)
{
    public static ResolvedIndexProfile BuiltIn { get; } =
        new(IndexProfileDefaults.Strategy, IndexProfileDefaults.MaxTokensPerChunk, IndexProfileDefaults.OverlapTokens);

    /// <summary>A comparable stamp of the settings: equal stamps mean equal chunks for equal text.</summary>
    public string Stamp => $"{Strategy}:{MaxTokensPerChunk}:{OverlapTokens}";
}

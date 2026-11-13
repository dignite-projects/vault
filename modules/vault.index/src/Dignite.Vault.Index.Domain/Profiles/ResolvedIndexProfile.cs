using Dignite.Vault.Index.Indexing;

namespace Dignite.Vault.Index.Profiles;

/// <summary>
/// The indexing settings that apply to one document type, wherever they came from. <see cref="Compilation"/> is not in
/// the <see cref="Stamp"/>: the artifacts it asks for follow the chunks, and changing it re-chunks nothing.
/// </summary>
public sealed record ResolvedIndexProfile(
    ChunkingStrategy Strategy,
    int MaxTokensPerChunk,
    int OverlapTokens,
    IndexEnrichment Enrichment = IndexEnrichment.None,
    IndexCompilation Compilation = IndexCompilation.None)
{
    public static ResolvedIndexProfile BuiltIn { get; } =
        new(IndexProfileDefaults.Strategy, IndexProfileDefaults.MaxTokensPerChunk, IndexProfileDefaults.OverlapTokens);

    /// <summary>
    /// A comparable stamp of the settings: equal stamps mean equal chunks for equal text. Enrichment is named only when
    /// it is on, so the stamps of profiles without it are what they were before enrichment existed. Stamp the settings
    /// as applied: with no chat model, enrichment is off whatever the profile asks.
    /// </summary>
    public string Stamp => Enrichment == IndexEnrichment.None
        ? $"{Strategy}:{MaxTokensPerChunk}:{OverlapTokens}"
        : $"{Strategy}:{MaxTokensPerChunk}:{OverlapTokens}:{(int)Enrichment}";
}

namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// The text a model reads for a chunk: the document title, then the chunk text (which already starts with its heading
/// path). Ingestion embeds this, and search reranks it, so both models see a chunk the same way.
/// </summary>
public static class ChunkContext
{
    public static string Compose(string? title, string text)
        => string.IsNullOrWhiteSpace(title) ? text : title + "\n" + text;
}

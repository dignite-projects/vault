namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// The text a model reads for a chunk: the document title, then the chunk text (which already starts with its heading
/// path). Ingestion embeds this, and search reranks it, so both models see a chunk the same way.
/// </summary>
public static class ChunkContext
{
    public static string Compose(string? title, string text)
        => string.IsNullOrWhiteSpace(title) ? text : title + "\n" + text;

    /// <summary>
    /// What is embedded for a chunk: <see cref="Compose"/>, then the questions an LLM found it answers, when it was
    /// enriched. A question is phrased the way a searcher asks, so it pulls the vector toward such queries.
    /// </summary>
    public static string ComposeForEmbedding(string? title, string text, string? questions)
        => string.IsNullOrWhiteSpace(questions) ? Compose(title, text) : Compose(title, text) + "\n" + questions;
}

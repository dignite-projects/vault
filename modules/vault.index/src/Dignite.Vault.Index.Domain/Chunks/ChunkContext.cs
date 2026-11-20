namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// The text a model reads for a chunk: the document title, the document's field values (one <c>name: value</c> per
/// line, when it has any), then the chunk text (which already starts with its heading path). Ingestion embeds this, and
/// search reranks it, so both models see a chunk the same way. The fields say whose document a passage belongs to when
/// the passage does not: a lease's deposit clause never names the tenant.
/// </summary>
public static class ChunkContext
{
    public static string Compose(string? title, string? fields, string text)
    {
        var body = string.IsNullOrWhiteSpace(fields) ? text : fields + "\n" + text;
        return string.IsNullOrWhiteSpace(title) ? body : title + "\n" + body;
    }

    /// <summary>
    /// What is embedded for a chunk: <see cref="Compose"/>, then the questions an LLM found it answers, when it was
    /// enriched. A question is phrased the way a searcher asks, so it pulls the vector toward such queries.
    /// </summary>
    public static string ComposeForEmbedding(string? title, string? fields, string text, string? questions)
    {
        var composed = Compose(title, fields, text);
        return string.IsNullOrWhiteSpace(questions) ? composed : composed + "\n" + questions;
    }
}

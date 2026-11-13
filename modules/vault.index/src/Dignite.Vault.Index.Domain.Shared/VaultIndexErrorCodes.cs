namespace Dignite.Vault.Index;

/// <summary>
/// Error codes, namespaced <c>VaultIndex:</c> and localized from the <c>VaultIndex</c> resource. Serialized strings:
/// once released, a code is not renamed.
/// </summary>
public static class VaultIndexErrorCodes
{
    public const string Namespace = "VaultIndex";

    /// <summary>The document has no text to index.</summary>
    public const string EmptyDocument = Namespace + ":EmptyDocument";

    /// <summary>The Markdown is longer than <c>Vault:IndexIngestion:MaxMarkdownLength</c>.</summary>
    public const string DocumentTooLarge = Namespace + ":DocumentTooLarge";

    /// <summary>Chunking produced more chunks than <c>Vault:IndexIngestion:MaxChunksPerDocument</c>.</summary>
    public const string TooManyChunks = Namespace + ":TooManyChunks";

    /// <summary>The host registers no embedding generator under <see cref="VaultIndexConsts.EmbeddingGeneratorKey"/>.</summary>
    public const string EmbeddingGeneratorMissing = Namespace + ":EmbeddingGeneratorMissing";

    /// <summary>The configured embedding model is not the serving generation's; vectors from two models do not mix.</summary>
    public const string EmbeddingModelChanged = Namespace + ":EmbeddingModelChanged";

    /// <summary>The model returned vectors of a dimension the storage was not created for.</summary>
    public const string EmbeddingDimensionMismatch = Namespace + ":EmbeddingDimensionMismatch";

    /// <summary>
    /// Extract returned a read scope of a version this Index does not know. A newer rule read the old way could widen
    /// what a user sees, so search refuses instead.
    /// </summary>
    public const string UnsupportedReadScope = Namespace + ":UnsupportedReadScope";

    /// <summary>The pointer was not returned by search or outline: it is not a chunk or outline-node pointer.</summary>
    public const string InvalidPointer = Namespace + ":InvalidPointer";

    /// <summary>The field conditions matched more documents than one search narrows to; a narrower condition is needed.</summary>
    public const string FieldFilterTooBroad = Namespace + ":FieldFilterTooBroad";

    /// <summary>The host registers no chat model under <see cref="VaultIndexConsts.ChatClientKey"/>, so nothing is compiled.</summary>
    public const string ChatModelUnavailable = Namespace + ":ChatModelUnavailable";

    /// <summary>The document's type does not compile summaries; a recompile would only remove them.</summary>
    public const string SummariesOff = Namespace + ":SummariesOff";
}

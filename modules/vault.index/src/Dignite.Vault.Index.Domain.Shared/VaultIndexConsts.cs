namespace Dignite.Vault.Index;

public static class VaultIndexConsts
{
    /// <summary>
    /// The key a host registers Index's <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> under. Index reads the
    /// model id from the generator's metadata and records it on the embedding generation.
    /// </summary>
    public const string EmbeddingGeneratorKey = "VaultIndex.Embedding";
}

namespace Dignite.Vault.Index.EntityFrameworkCore;

public class VaultIndexSqlServerOptions
{
    public const int DefaultVectorDimensions = 1024;

    /// <summary>
    /// The dimension of the <c>vector(n)</c> column. Part of the schema: a host that changes it also adds a migration,
    /// and the embedding model must return vectors of this length (a model with a dimension parameter, such as OpenAI's
    /// text-embedding-3 family, is asked for it by the host). 1024 is what the storage PoC measured, and the native
    /// size of common multilingual models (BAAI/bge-m3, Qwen3-Embedding-0.6B).
    /// </summary>
    public int VectorDimensions { get; set; } = DefaultVectorDimensions;
}

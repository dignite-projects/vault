namespace Dignite.Vault.Index.Search;

/// <summary>
/// How search ranks, section <c>Vault:IndexSearch</c>. The defaults are the ones the evaluation set chose; change them
/// only with numbers from it. The caps on what search returns are not here: they are compile-time constants
/// (<see cref="IndexSearchConsts"/>).
/// </summary>
public class VaultIndexSearchOptions
{
    /// <summary>The keyword leg's weight in reciprocal rank fusion.</summary>
    public double KeywordWeight { get; set; } = 1;

    /// <summary>The vector leg's weight in reciprocal rank fusion.</summary>
    public double VectorWeight { get; set; } = 1;

    /// <summary>
    /// How many fused candidates the reranker reads, when the host registers one. More finds answers ranked lower by
    /// both legs, at the cost of a longer rerank call.
    /// </summary>
    public int RerankCandidateCount { get; set; } = 30;
}

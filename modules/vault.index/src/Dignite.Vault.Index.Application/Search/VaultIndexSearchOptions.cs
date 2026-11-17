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

    /// <summary>
    /// When the reranker ordered the hits, the lowest reranker score a hit may have; below it a hit is dropped, and a
    /// question no passage answers can return no hits. <c>null</c>, the default, keeps every hit. The score's scale is
    /// the rerank model's: the evaluation measured it for <c>BAAI/bge-reranker-v2-m3</c> (Evaluation/abstention report),
    /// where 0.05 turned away 17 of 20 unanswerable questions and also dropped 6 of 127 answerable ones, mostly
    /// questions in another language than the document's. No effect without a reranker.
    /// </summary>
    public double? MinRerankScore { get; set; }
}

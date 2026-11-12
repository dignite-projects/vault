namespace Dignite.Vault.Index.Search;

/// <summary>
/// The bounds of retrieval. They cap what an LLM-triggered call returns, so they are compile-time constants:
/// configuration cannot widen them.
/// </summary>
public static class IndexSearchConsts
{
    /// <summary>The longest query accepted, in characters.</summary>
    public const int MaxQueryLength = 1_000;

    /// <summary>Hits returned when the caller does not say how many.</summary>
    public const int DefaultMaxResultCount = 8;

    /// <summary>The most hits one search returns.</summary>
    public const int MaxResultCount = 20;

    /// <summary>
    /// Candidates each leg (keyword, vector) hands to the fusion. Larger than <see cref="MaxResultCount"/> so a chunk
    /// ranked modestly by both legs can still win.
    /// </summary>
    public const int CandidatesPerLeg = 50;

    /// <summary>
    /// The constant of reciprocal rank fusion, score = Σ 1 / (k + rank). 60 is the value of the original paper and of
    /// most engines that ship RRF; it damps the weight of the first few ranks.
    /// </summary>
    public const int RrfK = 60;

    /// <summary>The most characters of one hit's text a search returns; a longer chunk is cut and says so.</summary>
    public const int MaxHitTextLength = 2_000;

    /// <summary>The most characters one read returns; a longer section is cut and says so.</summary>
    public const int MaxReadTextLength = 40_000;

    /// <summary>The most outline nodes returned for one document; a deeper outline is cut and says so.</summary>
    public const int MaxOutlineNodes = 500;

    /// <summary>
    /// The number of chunks exact vector search was sized for after the read-scope filter (storage PoC: about 90 to
    /// 175 ms at 49,000 rows, 0.2 s at 200,000). The status page warns past it; it is not enforced.
    /// </summary>
    public const long ExactSearchChunkLimit = 50_000;
}

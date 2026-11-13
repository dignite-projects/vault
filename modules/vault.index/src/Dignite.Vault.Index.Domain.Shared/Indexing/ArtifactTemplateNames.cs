namespace Dignite.Vault.Index.Indexing;

/// <summary>The templates compiled artifacts are built by. Stored on every artifact row: never rename one.</summary>
public static class ArtifactTemplateNames
{
    /// <summary>
    /// A document's section summaries and its document summary, in one artifact: the document summary is the root of
    /// the same bottom-up pass, so the two are always from the same source.
    /// </summary>
    public const string Summaries = "summaries";
}

namespace Dignite.Vault.Index.Indexing;

public enum IndexRunStatus
{
    Running = 0,
    Succeeded = 1,

    /// <summary>Terminal: deterministic failures (empty or oversized text) and transient failures past the retry cap.</summary>
    Failed = 2,

    /// <summary>Nothing to do: neither the content nor the profile changed, or the document is no longer Ready.</summary>
    Skipped = 3
}

using System;

namespace Dignite.Vault.Index.Indexing;

/// <summary>
/// What an LLM adds to each chunk when a document type's profile asks for it. Off by default: it costs a model call
/// per batch of chunks. The values are stored and serialized: never renumber them.
/// </summary>
[Flags]
public enum IndexEnrichment
{
    None = 0,

    /// <summary>A few keywords per chunk, in the keyword index.</summary>
    Keywords = 1,

    /// <summary>A few questions each chunk answers, in the keyword index and in what is embedded.</summary>
    Questions = 2
}

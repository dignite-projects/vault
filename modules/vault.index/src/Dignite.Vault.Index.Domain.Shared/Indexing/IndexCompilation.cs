using System;

namespace Dignite.Vault.Index.Indexing;

/// <summary>
/// What an LLM compiles from each document when a document type's profile asks for it. Off by default: it costs model
/// calls per document. The values are stored and serialized: never renumber them.
/// </summary>
[Flags]
public enum IndexCompilation
{
    None = 0,

    /// <summary>
    /// A one-sentence summary of every section of the document's outline and a summary of the whole document, built
    /// bottom-up: each section from its own text and its subsections' summaries, the document from its top sections.
    /// </summary>
    Summaries = 1
}

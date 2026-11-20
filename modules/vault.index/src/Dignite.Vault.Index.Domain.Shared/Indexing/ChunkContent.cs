using System;

namespace Dignite.Vault.Index.Indexing;

/// <summary>
/// What a chunk holds besides running text, read from the part of the document's Markdown it came from: a table (a GFM
/// or HTML table) or a figure (a <c>&lt;figure&gt;</c> block, a transcribed image). <see cref="Text"/> is neither. A
/// chunk can hold both, such as a figure whose transcription is a table. Stored as its number: never renumber.
/// </summary>
[Flags]
public enum ChunkContent
{
    Text = 0,
    Table = 1,
    Figure = 2
}

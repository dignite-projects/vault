namespace Dignite.Vault.Index.Indexing;

/// <summary>
/// How an index profile splits a document's Markdown into chunks: RAGFlow's chunk methods, as far as a document's
/// Markdown and layout carry what they need. Stored as its number: never renumber.
/// </summary>
public enum ChunkingStrategy
{
    /// <summary>By the heading tree; every chunk carries its full heading path. Long sections split by paragraph.</summary>
    Heading = 0,

    /// <summary>A target token count with overlap, split at paragraph and sentence boundaries, for text without headings.</summary>
    Token = 1,

    /// <summary>
    /// The whole document as one chunk while it fits the profile's token budget, so a question that needs two parts
    /// of a short document (the customer at the top of an invoice, the bank account at the bottom) finds both in one
    /// hit; a longer document is chunked by headings. RAGFlow's <c>one</c>.
    /// </summary>
    Whole = 2,

    /// <summary>
    /// Every data row of a table as a chunk of its own, with the table's header row and the heading path, so a question
    /// about one item of a price list or one line of a ledger finds that row; the rest of the document is chunked by
    /// headings. RAGFlow's <c>table</c>.
    /// </summary>
    TableRow = 3,

    /// <summary>
    /// Every page of the document's file as a chunk, split by headings only when a page exceeds the token budget, for
    /// slides, forms and catalogues whose pages are the units. A document without pages is chunked by headings.
    /// RAGFlow's <c>presentation</c>.
    /// </summary>
    Page = 4
}

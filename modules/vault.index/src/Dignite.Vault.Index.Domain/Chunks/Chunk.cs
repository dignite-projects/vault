using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Search;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// The smallest unit of retrieval: a piece of one document's Markdown with its heading path. Its own aggregate rather
/// than a child of <see cref="Documents.IndexedDocument"/>, because a document's chunks are replaced as a set, in one
/// transaction, and read many at a time by retrieval; loading them through the document would not fit either.
/// <para>
/// The document's type and owner are repeated on every chunk so the read-scope filter needs no join. The vectors are
/// not here: they belong to an embedding generation and live in the storage provider's own table, so two generations
/// can coexist while a new model is rolled out.
/// </para>
/// </summary>
public class Chunk : BasicAggregateRoot<Guid>, IMultiTenant, IChunkScopeFields
{
    public virtual Guid? TenantId { get; protected set; }

    public virtual Guid DocumentId { get; protected set; }

    public virtual Guid DocumentTypeId { get; protected set; }

    public virtual Guid? OwnerId { get; protected set; }

    /// <summary>Position in the document, from 0.</summary>
    public virtual int Ordinal { get; protected set; }

    /// <summary>The headings above this chunk, outermost first; <c>null</c> for text with no heading.</summary>
    public virtual string? HeadingPath { get; protected set; }

    /// <summary>The chunk's text, as Index stores it: what retrieval cites and what <c>read</c> returns.</summary>
    public virtual string Text { get; protected set; } = default!;

    /// <summary>SHA-256 of <see cref="Text"/>, so a rebuild can tell which chunks are unchanged.</summary>
    public virtual string ContentHash { get; protected set; } = default!;

    /// <summary>
    /// The document is out of retrieval (recycle bin, or withheld after a reclassification). Copied from
    /// <see cref="Documents.IndexedDocument.IsHidden"/> like the type and owner, so retrieval needs no join.
    /// </summary>
    public virtual bool IsHidden { get; protected set; }

    /// <summary>
    /// An operator took this chunk out of retrieval (the chunk view page). Index's own overlay on Extract's text: a
    /// rebuild carries it to the new chunk with the same <see cref="ContentHash"/>, and it ends when the text changes.
    /// </summary>
    public virtual bool IsDisabled { get; protected set; }

    /// <summary>Keywords an LLM chose for this chunk, space-separated; in the keyword index, not in what search returns.</summary>
    public virtual string? Keywords { get; protected set; }

    /// <summary>
    /// Questions an LLM found this chunk answers, one per line; in the keyword index and in what is embedded, not in what
    /// search returns. Stored, so moving to another store replays them without calling the model again.
    /// </summary>
    public virtual string? Questions { get; protected set; }

    /// <summary>
    /// The first page of the document's file this chunk's text was read from; <c>null</c> when it is not known: the
    /// document has no pages or no current layout, or the chunk's text could not be found in the Markdown.
    /// </summary>
    public virtual int? FirstPage { get; protected set; }

    /// <summary>The last page this chunk's text was read from; equal to <see cref="FirstPage"/> for a chunk on one page.</summary>
    public virtual int? LastPage { get; protected set; }

    /// <summary>
    /// Where on <see cref="FirstPage"/> to <see cref="LastPage"/> the chunk's text was read, in reading order, at most
    /// <see cref="IndexingConsts.MaxChunkRegions"/>; <c>null</c> when it is not known: the pages are not, or the parse
    /// reported no regions (a page read by a vision model names only the page).
    /// </summary>
    public virtual IReadOnlyList<ChunkRegion>? Regions { get; protected set; }

    /// <summary>
    /// Whether the chunk holds a table or a figure, from the part of the Markdown it came from; <c>null</c> when that
    /// part could not be found (see <see cref="FirstPage"/>).
    /// </summary>
    public virtual ChunkContent? Content { get; protected set; }

    protected Chunk()
    {
    }

    public Chunk(
        Guid id,
        Guid? tenantId,
        Guid documentId,
        Guid documentTypeId,
        Guid? ownerId,
        int ordinal,
        string? headingPath,
        string text,
        string contentHash,
        bool isHidden = false,
        bool isDisabled = false,
        string? keywords = null,
        string? questions = null)
        : base(id)
    {
        TenantId = tenantId;
        DocumentId = documentId;
        DocumentTypeId = documentTypeId;
        OwnerId = ownerId;
        Ordinal = Check.Range(ordinal, nameof(ordinal), 0, int.MaxValue);
        HeadingPath = Check.Length(headingPath, nameof(headingPath), IndexingConsts.MaxHeadingPathLength);
        Text = Check.NotNullOrEmpty(text, nameof(text));
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash), IndexingConsts.ContentHashLength, IndexingConsts.ContentHashLength);
        IsHidden = isHidden;
        IsDisabled = isDisabled;
        Keywords = Check.Length(keywords, nameof(keywords), IndexingConsts.MaxKeywordsLength);
        Questions = Check.Length(questions, nameof(questions), IndexingConsts.MaxQuestionsLength);
    }

    public virtual void SetDisabled(bool isDisabled)
    {
        IsDisabled = isDisabled;
    }

    /// <summary>Places the chunk on pages <paramref name="firstPage"/> to <paramref name="lastPage"/>; both <c>null</c> when unknown.</summary>
    public virtual void SetPages(int? firstPage, int? lastPage)
    {
        if (firstPage is null != lastPage is null || firstPage < 1 || lastPage < firstPage)
        {
            throw new ArgumentException($"Invalid page range {firstPage}-{lastPage}.");
        }

        FirstPage = firstPage;
        LastPage = lastPage;

        // A region off the new pages no longer belongs to the chunk.
        if (Regions is not null && Regions.Any(region => firstPage is null || region.Page < firstPage || region.Page > lastPage))
        {
            Regions = null;
        }
    }

    /// <summary>
    /// Outlines the chunk on its pages; <c>null</c> or empty when unknown. Each region must be on the chunk's pages and on
    /// the page (fractions from 0 to 1). Returns whether anything changed.
    /// </summary>
    public virtual bool SetRegions(IReadOnlyList<ChunkRegion>? regions)
    {
        regions = regions is { Count: > 0 } ? regions : null;
        if (regions is not null)
        {
            Check.Range(regions.Count, nameof(regions), 1, IndexingConsts.MaxChunkRegions);
            foreach (var region in regions)
            {
                if (FirstPage is null || region.Page < FirstPage || region.Page > LastPage
                    || region.Left < 0 || region.Top < 0 || region.Width <= 0 || region.Height <= 0
                    || region.Left + region.Width > 1.0001 || region.Top + region.Height > 1.0001)
                {
                    throw new ArgumentException($"Region {region} is not on pages {FirstPage}-{LastPage}.", nameof(regions));
                }
            }
        }

        if (regions is null ? Regions is null : Regions is not null && Regions.SequenceEqual(regions))
        {
            return false;
        }

        Regions = regions?.ToList();
        return true;
    }

    /// <summary>Records what the chunk holds besides text; <c>null</c> when unknown. Returns whether it changed.</summary>
    public virtual bool SetContent(ChunkContent? content)
    {
        if (Content == content)
        {
            return false;
        }

        Content = content;
        return true;
    }
}

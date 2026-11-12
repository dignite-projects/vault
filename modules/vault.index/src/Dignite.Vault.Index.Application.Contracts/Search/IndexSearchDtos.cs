using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Index.Search;

public class IndexSearchInput
{
    /// <summary>What to look for, in natural language or keywords.</summary>
    [Required]
    [StringLength(IndexSearchConsts.MaxQueryLength)]
    public string Query { get; set; } = default!;

    /// <summary>Only documents of this type (Extract's type code); every readable type when empty.</summary>
    [StringLength(128)]
    public string? DocumentTypeCode { get; set; }

    /// <summary>How many hits, 1 to <see cref="IndexSearchConsts.MaxResultCount"/>; <see cref="IndexSearchConsts.DefaultMaxResultCount"/> when empty.</summary>
    [Range(1, IndexSearchConsts.MaxResultCount)]
    public int? MaxResultCount { get; set; }
}

public class IndexSearchResultDto
{
    /// <summary>Best first.</summary>
    public List<IndexSearchHitDto> Hits { get; set; } = new();
}

public class IndexSearchHitDto
{
    /// <summary>The chunk, for <c>read</c>.</summary>
    public string Pointer { get; set; } = default!;

    public Guid DocumentId { get; set; }

    public string? DocumentTitle { get; set; }

    public string? DocumentTypeCode { get; set; }

    /// <summary>The headings above the chunk, outermost first, as Markdown heading markers.</summary>
    public string? HeadingPath { get; set; }

    /// <summary>The chunk's text, cut at <see cref="IndexSearchConsts.MaxHitTextLength"/> characters.</summary>
    public string Text { get; set; } = default!;

    public bool TextTruncated { get; set; }

    public int TextTotalLength { get; set; }

    /// <summary>The reciprocal-rank-fusion score: only for ordering, not comparable across searches.</summary>
    public double Score { get; set; }

    /// <summary>The chunk's rank in the keyword leg, from 1; <c>null</c> when that leg did not return it.</summary>
    public int? KeywordRank { get; set; }

    /// <summary>The chunk's rank in the vector leg, from 1; <c>null</c> when that leg did not return it.</summary>
    public int? VectorRank { get; set; }
}

public class DocumentOutlineDto
{
    public Guid DocumentId { get; set; }

    public string? DocumentTitle { get; set; }

    public string? DocumentTypeCode { get; set; }

    /// <summary>The heading tree in document order, parents before children.</summary>
    public List<OutlineNodeDto> Nodes { get; set; } = new();

    /// <summary>The tree had more than <see cref="IndexSearchConsts.MaxOutlineNodes"/> nodes and was cut.</summary>
    public bool Truncated { get; set; }

    public int TotalNodeCount { get; set; }

    /// <summary>How many chunks the document has, including those under no heading.</summary>
    public int ChunkCount { get; set; }
}

public class OutlineNodeDto
{
    /// <summary>The node and everything under it, for <c>read</c>.</summary>
    public string Pointer { get; set; } = default!;

    public string Title { get; set; } = default!;

    /// <summary>The depth in the tree, from 1.</summary>
    public int Depth { get; set; }

    /// <summary>The Markdown heading level, 1 to 6.</summary>
    public int Level { get; set; }

    /// <summary>Chunks under the node, its subsections included.</summary>
    public int ChunkCount { get; set; }
}

public class IndexReadInput
{
    /// <summary>A pointer from search (<c>chunk:…</c>) or outline (<c>node:…</c>).</summary>
    [Required]
    [StringLength(128)]
    public string Pointer { get; set; } = default!;
}

public class IndexReadResultDto
{
    public string Pointer { get; set; } = default!;

    public Guid DocumentId { get; set; }

    public string? DocumentTitle { get; set; }

    public string? DocumentTypeCode { get; set; }

    /// <summary>The heading path of the chunk, or of the node's own heading.</summary>
    public string? HeadingPath { get; set; }

    /// <summary>The text Index stored, cut at <see cref="IndexSearchConsts.MaxReadTextLength"/> characters.</summary>
    public string Text { get; set; } = default!;

    public bool TextTruncated { get; set; }

    /// <summary>The full length of the text behind the pointer, before any cut.</summary>
    public int TextTotalLength { get; set; }

    /// <summary>The chunks read.</summary>
    public int ChunkCount { get; set; }

    /// <summary>The chunk before, for reading on; <c>null</c> at the start or for a node.</summary>
    public string? PreviousPointer { get; set; }

    /// <summary>The chunk after, for reading on; <c>null</c> at the end or for a node.</summary>
    public string? NextPointer { get; set; }
}

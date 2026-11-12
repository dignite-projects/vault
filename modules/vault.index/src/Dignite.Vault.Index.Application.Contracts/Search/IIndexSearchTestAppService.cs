using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The retrieval test: one search run as search runs it, optionally with other ranking settings, showing every
/// candidate with what each leg, the fusion and the reranker made of it. The settings apply to this run only.
/// </summary>
public interface IIndexSearchTestAppService : IApplicationService
{
    Task<IndexSearchTestResultDto> RunAsync(IndexSearchTestInput input);
}

/// <summary>A search's input plus ranking settings; an empty setting takes the host's value.</summary>
public class IndexSearchTestInput : IValidatableObject
{
    [Required]
    [StringLength(IndexSearchConsts.MaxQueryLength)]
    public string Query { get; set; } = default!;

    [StringLength(128)]
    public string? DocumentTypeCode { get; set; }

    [MaxLength(DocumentConsts.MaxSearchFieldFilters)]
    public List<DocumentFieldFilter>? FieldFilters { get; set; }

    [Range(0, IndexSearchConsts.MaxLegWeight)]
    public double? KeywordWeight { get; set; }

    [Range(0, IndexSearchConsts.MaxLegWeight)]
    public double? VectorWeight { get; set; }

    [Range(1, IndexSearchConsts.MaxCandidatesPerLeg)]
    public int? CandidatesPerLeg { get; set; }

    /// <summary>Whether to rerank, when the host has a rerank model; it does by default.</summary>
    public bool? Rerank { get; set; }

    [Range(1, IndexSearchConsts.MaxRerankCandidates)]
    public int? RerankCandidateCount { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FieldFilters is { Count: > 0 } && string.IsNullOrWhiteSpace(DocumentTypeCode))
        {
            yield return new ValidationResult(
                "DocumentTypeCode is required when field filters are specified.",
                new[] { nameof(DocumentTypeCode) });
        }
    }
}

public class IndexSearchTestResultDto
{
    /// <summary>The settings the run used, the host's where the input left them empty.</summary>
    public double KeywordWeight { get; set; }

    public double VectorWeight { get; set; }

    public int CandidatesPerLeg { get; set; }

    public int RerankCandidateCount { get; set; }

    /// <summary>The host has a rerank model.</summary>
    public bool RerankAvailable { get; set; }

    /// <summary>The reranker ordered the leading candidates; <c>false</c> when it was off, absent, or failed.</summary>
    public bool Reranked { get; set; }

    public string? RerankModelId { get; set; }

    public int KeywordCandidateCount { get; set; }

    public int VectorCandidateCount { get; set; }

    public long KeywordMilliseconds { get; set; }

    /// <summary>Including embedding the query.</summary>
    public long VectorMilliseconds { get; set; }

    public long RerankMilliseconds { get; set; }

    /// <summary>Every candidate either leg found, in the final order search would return them.</summary>
    public List<IndexSearchTestCandidateDto> Candidates { get; set; } = new();
}

public class IndexSearchTestCandidateDto
{
    /// <summary>Place in the final order, from 1: what search would return.</summary>
    public int Rank { get; set; }

    /// <summary>Place after fusion, before any rerank, from 1.</summary>
    public int FusedRank { get; set; }

    public double FusedScore { get; set; }

    public int? KeywordRank { get; set; }

    /// <summary>The keyword leg's own score (a full-text rank); comparable only within one leg of one run.</summary>
    public double? KeywordScore { get; set; }

    public int? VectorRank { get; set; }

    /// <summary>Cosine distance to the query: smaller is nearer.</summary>
    public double? VectorDistance { get; set; }

    /// <summary>The reranker's score; <c>null</c> for a candidate it did not read.</summary>
    public double? RerankScore { get; set; }

    public Guid ChunkId { get; set; }

    public string Pointer { get; set; } = default!;

    public Guid DocumentId { get; set; }

    public string? DocumentTitle { get; set; }

    public string? DocumentTypeCode { get; set; }

    public string? HeadingPath { get; set; }

    /// <summary>The start of the chunk text, cut at <see cref="IndexSearchConsts.MaxTestTextLength"/>.</summary>
    public string Text { get; set; } = default!;

    public bool TextTruncated { get; set; }
}

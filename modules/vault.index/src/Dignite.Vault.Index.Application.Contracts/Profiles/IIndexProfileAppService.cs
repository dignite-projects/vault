using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Dignite.Vault.Index.Indexing;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Index.Profiles;

/// <summary>
/// The index profile page: how each document type of the current layer is chunked, what a change would cost before it
/// is made, and the change itself. A change re-chunks and re-embeds the affected documents in the background, at low
/// priority.
/// </summary>
public interface IIndexProfileAppService : IApplicationService
{
    /// <summary>The layer default first, then every document type the caller can see, with the settings that apply to it.</summary>
    Task<IndexProfileListDto> GetListAsync();

    /// <summary>What saving <paramref name="input"/> would rebuild: documents, chunks, and the embedding tokens they last took.</summary>
    Task<IndexRebuildEstimateDto> GetRebuildEstimateAsync(IndexProfileInput input);

    /// <summary>Saves the layer default (no type) or one type's own profile, and queues the rebuild of what it changes.</summary>
    Task<IndexProfileDto> SetAsync(IndexProfileInput input);

    /// <summary>Removes a type's own profile, so the layer default applies again, and queues the rebuild of what that changes.</summary>
    Task ResetAsync(Guid documentTypeId);
}

/// <summary>Chunking settings for the layer default (<see cref="DocumentTypeId"/> empty) or one document type.</summary>
public class IndexProfileInput : IValidatableObject
{
    public Guid? DocumentTypeId { get; set; }

    public ChunkingStrategy ChunkingStrategy { get; set; } = IndexProfileDefaults.Strategy;

    [Range(IndexProfileConsts.MinMaxTokensPerChunk, IndexProfileConsts.MaxMaxTokensPerChunk)]
    public int MaxTokensPerChunk { get; set; } = IndexProfileDefaults.MaxTokensPerChunk;

    [Range(0, IndexProfileConsts.MaxMaxTokensPerChunk)]
    public int OverlapTokens { get; set; } = IndexProfileDefaults.OverlapTokens;

    /// <summary>What an LLM adds to each chunk; nothing by default.</summary>
    public IndexEnrichment Enrichment { get; set; }

    /// <summary>What an LLM compiles from each document; nothing by default.</summary>
    public IndexCompilation Compilation { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OverlapTokens >= MaxTokensPerChunk)
        {
            yield return new ValidationResult(
                "OverlapTokens must be smaller than MaxTokensPerChunk.",
                new[] { nameof(OverlapTokens) });
        }

        if (!Enum.IsDefined(ChunkingStrategy))
        {
            yield return new ValidationResult("Unknown chunking strategy.", new[] { nameof(ChunkingStrategy) });
        }

        if ((Enrichment & ~(IndexEnrichment.Keywords | IndexEnrichment.Questions)) != 0)
        {
            yield return new ValidationResult("Unknown enrichment.", new[] { nameof(Enrichment) });
        }

        if ((Compilation & ~IndexCompilation.Summaries) != 0)
        {
            yield return new ValidationResult("Unknown compilation.", new[] { nameof(Compilation) });
        }
    }
}

public class IndexProfileListDto : ListResultDto<IndexProfileDto>
{
    /// <summary>
    /// The host has Index's chat model. Without one, a profile's enrichment and compilation are saved but not applied,
    /// and saving them rebuilds and compiles nothing.
    /// </summary>
    public bool ChatModelAvailable { get; set; }
}

public class IndexProfileDto
{
    /// <summary>The document type; <c>null</c> for the layer default.</summary>
    public Guid? DocumentTypeId { get; set; }

    public string? DocumentTypeCode { get; set; }

    public string? DocumentTypeName { get; set; }

    /// <summary>The type has its own profile; otherwise the layer default (or the built-in one) applies.</summary>
    public bool HasOwnProfile { get; set; }

    public ChunkingStrategy ChunkingStrategy { get; set; }

    public int MaxTokensPerChunk { get; set; }

    public int OverlapTokens { get; set; }

    public IndexEnrichment Enrichment { get; set; }

    public IndexCompilation Compilation { get; set; }

    /// <summary>Documents of this type in the index; for the layer default, every document in the layer.</summary>
    public int IndexedDocumentCount { get; set; }
}

public class IndexRebuildEstimateDto
{
    /// <summary>Indexed documents whose chunks the change would rebuild.</summary>
    public int DocumentCount { get; set; }

    /// <summary>Their chunks today.</summary>
    public long ChunkCount { get; set; }

    /// <summary>
    /// The embedding tokens their last successful index runs took: about what re-embedding them costs, give or take
    /// what the new chunking adds or removes.
    /// </summary>
    public long EmbeddingTokenCount { get; set; }

    /// <summary>Of <see cref="DocumentCount"/>, those with no recorded token count (never indexed with success).</summary>
    public int DocumentsWithoutTokenCount { get; set; }

    /// <summary>
    /// The model calls enrichment would make, about one per batch of chunks of each document; 0 when the settings
    /// enrich nothing or there is no chat model.
    /// </summary>
    public int EnrichmentCallCount { get; set; }

    /// <summary>
    /// Indexed documents whose summaries would be compiled: those the settings apply to that have none yet or whose
    /// chunks the change rebuilds. 0 when the settings compile nothing or there is no chat model.
    /// </summary>
    public int SummaryDocumentCount { get; set; }

    /// <summary>
    /// About the model calls compiling them takes: per document, one per eight chunks and two for the upper sections
    /// and the document summary. On the evaluation set this came within one call of the calls made.
    /// </summary>
    public int SummaryCallCount { get; set; }
}

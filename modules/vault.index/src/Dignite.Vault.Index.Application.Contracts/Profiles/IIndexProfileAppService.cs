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
    Task<ListResultDto<IndexProfileDto>> GetListAsync();

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
    }
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
}

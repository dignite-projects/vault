using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Search;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// The chunk view: what one document was cut into, and taking a chunk out of retrieval or putting it back. The chunk
/// text is never edited here; a wrong text is corrected in Extract, so Index can always be rebuilt from Extract.
/// </summary>
public interface IIndexChunkAppService : IApplicationService
{
    /// <summary>A document's chunks in order, disabled ones included, if the caller's read scope reaches the document.</summary>
    Task<IndexChunkListDto> GetListAsync(GetIndexChunkListInput input);

    /// <summary>Takes a chunk out of search, outline and read. A rebuild keeps it out while its text is unchanged.</summary>
    Task DisableAsync(Guid id);

    /// <summary>Puts a disabled chunk back.</summary>
    Task EnableAsync(Guid id);
}

public class GetIndexChunkListInput
{
    [Required]
    public Guid DocumentId { get; set; }

    [Range(0, int.MaxValue)]
    public int SkipCount { get; set; }

    [Range(1, IndexSearchConsts.MaxChunkPageSize)]
    public int MaxResultCount { get; set; } = 50;
}

public class IndexChunkListDto : PagedResultDto<IndexChunkDto>
{
    public Guid DocumentId { get; set; }

    public string? DocumentTitle { get; set; }

    public string? DocumentTypeCode { get; set; }

    public IndexedDocumentStatus Status { get; set; }

    /// <summary>The whole document is out of retrieval: in Extract's recycle bin, or withheld after a reclassification.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Disabled chunks in the whole document, not only this page.</summary>
    public int DisabledCount { get; set; }
}

public class IndexChunkDto
{
    public Guid Id { get; set; }

    /// <summary>The chunk's pointer, as search returns it.</summary>
    public string Pointer { get; set; } = default!;

    /// <summary>Position in the document, from 0.</summary>
    public int Ordinal { get; set; }

    public string? HeadingPath { get; set; }

    public string Text { get; set; } = default!;

    public bool IsDisabled { get; set; }

    /// <summary>Keywords an LLM chose for the chunk, space-separated; <c>null</c> when it was not enriched.</summary>
    public string? Keywords { get; set; }

    /// <summary>Questions an LLM found the chunk answers, one per line; <c>null</c> when it was not enriched.</summary>
    public string? Questions { get; set; }
}

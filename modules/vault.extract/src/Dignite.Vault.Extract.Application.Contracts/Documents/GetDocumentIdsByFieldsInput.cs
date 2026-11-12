using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Input of <see cref="IDocumentAppService.GetIdsByFieldsAsync"/>: one document type and the extracted-field conditions
/// its documents must meet, ANDed, with the same rules as <see cref="GetDocumentListInput.FieldFilters"/>.
/// </summary>
public class GetDocumentIdsByFieldsInput
{
    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string DocumentTypeCode { get; set; } = default!;

    [Required]
    [MinLength(1)]
    [MaxLength(DocumentConsts.MaxSearchFieldFilters)]
    public List<DocumentFieldFilter> FieldFilters { get; set; } = new();

    /// <summary>How many ids to return at most; <see cref="DocumentIdListDto.HasMore"/> says when more matched.</summary>
    [Range(1, DocumentConsts.MaxFieldMatchedDocumentIds)]
    public int MaxResultCount { get; set; } = DocumentConsts.MaxFieldMatchedDocumentIds;
}

/// <summary>Document ids in ascending order, and whether more documents matched than were returned.</summary>
public class DocumentIdListDto
{
    public List<Guid> Ids { get; set; } = new();

    public bool HasMore { get; set; }
}

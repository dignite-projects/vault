using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents.Integration;

/// <summary>
/// A keyset page request for <see cref="IDocumentIntegrationService.GetReadyIdsAsync"/>. <see cref="AfterId"/> is the
/// last id of the previous page, <c>null</c> for the first. <see cref="LimitedResultRequestDto.MaxResultCount"/> is
/// the page size, bounded by <see cref="LimitedResultRequestDto.MaxMaxResultCount"/>.
/// </summary>
public class GetReadyDocumentIdsInput : LimitedResultRequestDto
{
    public Guid? AfterId { get; set; }
}

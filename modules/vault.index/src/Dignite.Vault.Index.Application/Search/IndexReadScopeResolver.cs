using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The caller's read scope and visible document types, from Extract, for every Index service that shows documents.
/// Extract refuses a caller without its <c>VaultExtract.Documents</c> entry; a scope version this Index does not know
/// is refused here, since reading a newer rule the old way could widen what a user sees.
/// </summary>
public class IndexReadScopeResolver : ITransientDependency
{
    /// <summary>The read-scope versions this Index understands; see <see cref="DocumentReadScopeDto.Version"/>.</summary>
    public const int SupportedReadScopeVersion = 1;

    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentTypeAppService _documentTypeAppService;

    public IndexReadScopeResolver(IDocumentAppService documentAppService, IDocumentTypeAppService documentTypeAppService)
    {
        _documentAppService = documentAppService;
        _documentTypeAppService = documentTypeAppService;
    }

    /// <summary>
    /// The scope, narrowed to <paramref name="documentTypeCode"/> when given (a code the caller cannot see narrows to
    /// nothing), and to the documents <paramref name="fieldFilters"/> match when given. Extract resolves the field
    /// conditions, on the caller's own read scope (<see cref="IDocumentAppService.GetIdsByFieldsAsync"/>), so Index keeps
    /// no copy of field values and a renamed field cannot go stale here.
    /// </summary>
    public virtual async Task<ChunkSearchScope> GetScopeAsync(
        string? documentTypeCode,
        IReadOnlyDictionary<Guid, string> visibleTypes,
        List<DocumentFieldFilter>? fieldFilters = null)
    {
        var readScope = await _documentAppService.GetReadScopeAsync();
        if (readScope.Version != SupportedReadScopeVersion)
        {
            throw new BusinessException(VaultIndexErrorCodes.UnsupportedReadScope).WithData("Version", readScope.Version);
        }

        List<Guid>? requested = null;
        if (!string.IsNullOrWhiteSpace(documentTypeCode))
        {
            requested = visibleTypes.Where(t => t.Value == documentTypeCode.Trim()).Select(t => t.Key).ToList();
        }

        List<Guid>? documentIds = null;
        if (fieldFilters is { Count: > 0 } && requested is { Count: > 0 })
        {
            var matched = await _documentAppService.GetIdsByFieldsAsync(new GetDocumentIdsByFieldsInput
            {
                DocumentTypeCode = documentTypeCode!.Trim(),
                FieldFilters = fieldFilters,
                MaxResultCount = IndexSearchConsts.MaxFieldFilterDocuments
            });
            if (matched.HasMore)
            {
                throw new BusinessException(VaultIndexErrorCodes.FieldFilterTooBroad).WithData("Max", IndexSearchConsts.MaxFieldFilterDocuments);
            }

            documentIds = matched.Ids;
        }

        return new ChunkSearchScope(readScope.IsUnrestricted, readScope.DocumentTypeIds, readScope.OwnerId, requested, documentIds);
    }

    /// <summary>The document types the caller can see, id to type code.</summary>
    public virtual async Task<IReadOnlyDictionary<Guid, string>> GetVisibleTypesAsync()
    {
        var types = await _documentTypeAppService.GetVisibleSummariesAsync();
        return types.ToDictionary(t => t.Id, t => t.TypeCode);
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Integration;

/// <summary>
/// <see cref="IDocumentIntegrationService"/>: the in-process pull for a module that derives data from Ready
/// documents. The interface states the boundary; two points about this class.
/// <para>
/// <b>Not a <see cref="VaultExtractAppService"/>.</b> That base carries the <c>VaultExtract.Enable</c> feature gate,
/// which is kept off every path with no signed-in user (see <c>VaultExtractFeatures.Enable</c>); this service is
/// called from a consumer's background job or event handler. <c>VaultExtractAppServiceFeature_Tests</c> exempts
/// integration services from the gate and pins that they do not carry it.
/// </para>
/// <para>
/// <b>No authorization check, by design.</b> There is no user to check. What keeps it safe is what it returns:
/// only documents Ready for consumers (<see cref="DocumentQueries.ReadyForConsumers"/>), under the ambient tenant
/// filter, never pierced here.
/// </para>
/// </summary>
public class DocumentIntegrationService : ApplicationService, IDocumentIntegrationService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentReferenceMaps _referenceMaps;

    public DocumentIntegrationService(
        IDocumentRepository documentRepository,
        DocumentReferenceMaps referenceMaps)
    {
        _documentRepository = documentRepository;
        _referenceMaps = referenceMaps;
    }

    public virtual async Task<DocumentIntegrationDto?> FindReadyAsync(Guid id)
    {
        var query = (await _documentRepository.GetQueryableAsync())
            .Where(d => d.Id == id)
            .Where(DocumentQueries.ReadyForConsumers);

        var document = await AsyncExecuter.FirstOrDefaultAsync(query);
        if (document == null)
        {
            return null;
        }

        var (_, fieldsByType) = await _referenceMaps.ResolveAsync(new[] { document });

        return new DocumentIntegrationDto
        {
            Id = document.Id,
            TenantId = document.TenantId,
            // ReadyForConsumers requires a type, so the value is there.
            DocumentTypeId = document.DocumentTypeId!.Value,
            OwnerId = document.CreatorId,
            Title = document.Title,
            Language = document.Language,
            Markdown = document.Markdown ?? string.Empty,
            ExtractedFields = _referenceMaps.AssembleExtractedFields(document, fieldsByType)
        };
    }

    public virtual async Task<ListResultDto<Guid>> GetReadyIdsAsync(GetReadyDocumentIdsInput input)
    {
        var ids = await _documentRepository.GetReadyIdsAsync(input.AfterId, input.MaxResultCount);
        return new ListResultDto<Guid>(ids);
    }
}

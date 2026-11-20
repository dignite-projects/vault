using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Layout;
using Dignite.Vault.Parse;
using Microsoft.Extensions.Logging;
using Volo.Abp.Application.Dtos;
using Volo.Abp;
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
    private readonly DocumentLayoutStore _layoutStore;
    private readonly IFieldRepository _fieldRepository;

    public DocumentIntegrationService(
        IDocumentRepository documentRepository,
        DocumentReferenceMaps referenceMaps,
        DocumentLayoutStore layoutStore,
        IFieldRepository fieldRepository)
    {
        _documentRepository = documentRepository;
        _referenceMaps = referenceMaps;
        _layoutStore = layoutStore;
        _fieldRepository = fieldRepository;
        ObjectMapperContext = typeof(VaultExtractApplicationModule);
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
        var layout = await GetLayoutAsync(document);

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
            ExtractedFields = _referenceMaps.AssembleExtractedFields(document, fieldsByType),
            Pages = layout?.Pages,
            Regions = layout?.Regions
        };
    }

    /// <summary>
    /// The Markdown's ranges per page and the page regions of its elements, from the layout the parse kept, while the
    /// Markdown is still the one it rendered. Fails open: a layout that cannot be read leaves both out and the document
    /// usable.
    /// </summary>
    protected virtual async Task<DocumentIntegrationLayout?> GetLayoutAsync(Document document)
    {
        try
        {
            var tree = await _layoutStore.GetCurrentTreeAsync(document.Id, document.Markdown);
            if (tree is null)
            {
                return null;
            }

            var map = ExtractionDocumentMarkdown.Map(tree);
            if (!string.Equals(map.Markdown, document.Markdown, StringComparison.Ordinal))
            {
                // The hash matched, so the renderer itself changed since the parse: its offsets no longer line up.
                return null;
            }

            var pages = ExtractionDocumentPages.Spans(map)
                .Select(page => new DocumentPageSpansDto
                {
                    PageNumber = page.Key,
                    Spans = page.Value.Select(span => new DocumentTextSpanDto { Offset = span.Offset, Length = span.Length }).ToList()
                })
                .ToList();
            if (pages.Count == 0)
            {
                return null;
            }

            var regions = ExtractionDocumentPages.Regions(map)
                .Select(region => new DocumentPageRegionDto
                {
                    Offset = region.Offset,
                    Length = region.Length,
                    PageNumber = region.PageNumber,
                    Left = region.Left,
                    Top = region.Top,
                    Width = region.Width,
                    Height = region.Height
                })
                .ToList();
            return new DocumentIntegrationLayout(pages, regions.Count == 0 ? null : regions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not read the layout of document {DocumentId}; its pages are left out.", document.Id);
            return null;
        }
    }

    public virtual async Task<ListResultDto<Guid>> GetReadyIdsAsync(GetReadyDocumentIdsInput input)
    {
        var ids = await _documentRepository.GetReadyIdsAsync(input.AfterId, input.MaxResultCount);
        return new ListResultDto<Guid>(ids);
    }

    public virtual async Task<ListResultDto<FieldDefinitionDto>> GetFieldDefinitionsAsync(Guid documentTypeId)
    {
        // Archived fields too, the same set FindReadyAsync resolves ExtractedFields against (DocumentReferenceMaps);
        // the ambient tenant filter keeps the read to one layer.
        List<Field> fields;
        using (DataFilter.Disable<ISoftDelete>())
        {
            fields = await _fieldRepository.GetListAsync(f => f.DocumentTypeId == documentTypeId);
        }

        var ordered = fields
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        return new ListResultDto<FieldDefinitionDto>(ObjectMapper.Map<List<Field>, List<FieldDefinitionDto>>(ordered));
    }
}

/// <summary>What a document's current layout places: its Markdown's ranges per page and its elements' page regions.</summary>
public record DocumentIntegrationLayout(List<DocumentPageSpansDto> Pages, List<DocumentPageRegionDto>? Regions);

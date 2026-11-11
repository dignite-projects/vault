using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.FlexFields;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Resolves what a batch of documents refers to — type codes and each type's field definitions — and renders a
/// document's value bag into the egress shape of <c>ExtractedFields</c>. Shared by <see cref="DocumentAppService"/>
/// and the integration service, so the REST / MCP egress and the in-process egress cannot render a field differently.
/// </summary>
public class DocumentReferenceMaps : ITransientDependency
{
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly IDataFilter _dataFilter;
    private readonly IVaultExtractFieldTypeRegistry _fieldTypeExtensionRegistry;

    public DocumentReferenceMaps(
        IDocumentTypeRepository documentTypeRepository,
        IFieldRepository fieldRepository,
        IDataFilter dataFilter,
        IVaultExtractFieldTypeRegistry fieldTypeExtensionRegistry)
    {
        _documentTypeRepository = documentTypeRepository;
        _fieldRepository = fieldRepository;
        _dataFilter = dataFilter;
        _fieldTypeExtensionRegistry = fieldTypeExtensionRegistry;
    }

    /// <summary>
    /// Resolves this document batch's DocumentTypeId -> TypeCode and DocumentTypeId -> its fields by name,
    /// in one pass. Traverses soft-delete so archived types and fields referenced by historical documents
    /// still resolve; <c>IMultiTenant</c> still isolates by ambient tenant, because a batch belongs to one
    /// layer.
    /// <para>
    /// Fields are grouped <b>by document type</b>, not by name alone. The value bag is keyed by field name,
    /// but a name is only unique within its type — <c>(TenantId, DocumentTypeId, Name)</c> — so a flat
    /// name map would let one type's <c>amount</c> render another type's <c>amount</c> with the wrong
    /// field type, which for a Date field is a differently-shaped string rather than an error.
    /// </para>
    /// </summary>
    public virtual async Task<(Dictionary<Guid, string> TypeCodes, Dictionary<Guid, Dictionary<string, Field>> FieldsByType)>
        ResolveAsync(IReadOnlyCollection<Document> documents)
    {
        var typeIds = documents
            .Where(d => d.DocumentTypeId.HasValue)
            .Select(d => d.DocumentTypeId!.Value)
            .Distinct()
            .ToList();

        var typeCodes = new Dictionary<Guid, string>();
        var fieldsByType = new Dictionary<Guid, Dictionary<string, Field>>();

        using (_dataFilter.Disable<Volo.Abp.ISoftDelete>())
        {
            if (typeIds.Count > 0)
            {
                foreach (var t in await _documentTypeRepository.GetListAsync(t => typeIds.Contains(t.Id)))
                {
                    typeCodes[t.Id] = t.TypeCode;
                }

                foreach (var f in await _fieldRepository.GetListAsync(f => typeIds.Contains(f.DocumentTypeId)))
                {
                    if (!fieldsByType.TryGetValue(f.DocumentTypeId, out var byName))
                    {
                        byName = new Dictionary<string, Field>(StringComparer.Ordinal);
                        fieldsByType[f.DocumentTypeId] = byName;
                    }

                    byName[f.Name] = f;
                }
            }
        }

        return (typeCodes, fieldsByType);
    }

    /// <summary>
    /// Assembles the <c>ExtractedFields</c> dictionary of the egress from a document's value bag.
    /// <para>
    /// The bag is already keyed by field name, so this is a rendering step rather than a join: each value
    /// goes through <see cref="FlexFieldValueJsonWriter"/>, which is what keeps the wire shapes identical
    /// to v2 — a Date field still emits <c>"2026-03-14"</c> rather than the midnight <c>DateTime</c> the
    /// bag actually holds.
    /// </para>
    /// <para>
    /// A bag key with no surviving field definition is skipped rather than emitted raw. It means the field
    /// was hard-deleted out from under its values, and a key whose type nothing can state would render by
    /// guesswork.
    /// </para>
    /// </summary>
    public virtual Dictionary<string, JsonElement>? AssembleExtractedFields(
        Document document,
        IReadOnlyDictionary<Guid, Dictionary<string, Field>> fieldsByType)
    {
        if (document.FlexFields.Count == 0 || document.DocumentTypeId == null)
        {
            return null;
        }

        if (!fieldsByType.TryGetValue(document.DocumentTypeId.Value, out var byName))
        {
            return null;
        }

        var dict = new Dictionary<string, JsonElement>(document.FlexFields.Count, StringComparer.Ordinal);
        foreach (var entry in document.FlexFields)
        {
            if (!byName.TryGetValue(entry.Key, out var field))
            {
                continue;
            }

            var rendered = FlexFieldValueJsonWriter.Write(entry.Value, field.FieldTypeName, field.Configuration, _fieldTypeExtensionRegistry);
            if (rendered.HasValue)
            {
                dict[entry.Key] = rendered.Value;
            }
        }

        return dict.Count > 0 ? dict : null;
    }
}

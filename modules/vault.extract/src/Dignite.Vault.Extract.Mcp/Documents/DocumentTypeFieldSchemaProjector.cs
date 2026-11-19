using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.FlexFields;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Projects field definitions into their LLM-facing schema shape. Shared by the document-type
/// <see cref="DocumentTypeResources">resource</see> and <see cref="DocumentTypeTools">tool</see> because
/// they describe the same fields to the same clients: when the two drift, a client that discovered a
/// field through one and filtered on it through the other gets a different answer about what the field
/// accepts.
/// </summary>
public static class DocumentTypeFieldSchemaProjector
{
    /// <summary>
    /// The first <paramref name="maxFields"/> fields in display order (then by name, so the cut is stable). The count of
    /// all of them is the caller's to report beside the list.
    /// </summary>
    public static List<DocumentTypeFieldSchema> Project(
        IEnumerable<FieldDefinitionDto> fields,
        int maxFields,
        IFieldTypeResolver fieldTypeResolver,
        IVaultExtractFieldTypeRegistry fieldTypeExtensionRegistry,
        bool includeExtractionSchema = false)
    {
        return fields
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Name, System.StringComparer.Ordinal)
            .Take(maxFields)
            .Select(f => Project(f, fieldTypeResolver, fieldTypeExtensionRegistry, includeExtractionSchema))
            .ToList();
    }

    /// <param name="includeExtractionSchema">
    /// Also describe how to extract the field (<see cref="DocumentTypeFieldSchema.ValueSchema"/> and
    /// <see cref="DocumentTypeFieldSchema.ExtractionPrompt"/>), for a client that submits field values it extracted
    /// itself. Left out otherwise: a client that only searches does not need it.
    /// </param>
    public static DocumentTypeFieldSchema Project(
        FieldDefinitionDto field,
        IFieldTypeResolver fieldTypeResolver,
        IVaultExtractFieldTypeRegistry fieldTypeExtensionRegistry,
        bool includeExtractionSchema = false)
    {
        // GetAll rather than Get: a stored field could name a type this deployment no longer registers
        // (a package downgrade, a renamed key), and Get throws on that. Describing such a field as
        // non-filterable is right — nothing can index it here — while throwing would take down the whole
        // type listing over one stale row.
        var fieldType = fieldTypeResolver.GetAll()
            .FirstOrDefault(t => string.Equals(t.Name, field.FieldTypeName, System.StringComparison.Ordinal));

        return new DocumentTypeFieldSchema
        {
            Name = field.Name,
            FieldType = field.FieldTypeName,
            IsMultiValue = fieldTypeExtensionRegistry.IsMultiValue(field.FieldTypeName, field.Configuration),
            IsFilterable = field.IsSearchable && fieldType?.IndexValueType != null,
            // DisplayName is admin-configured user-derived text, so PromptBoundary wrapping prevents
            // indirect prompt injection. Name and FieldType are system-controlled (allow-listed name
            // pattern / registration key), so they are emitted raw.
            DisplayName = PromptBoundary.WrapField(field.DisplayName),
            IsRequired = field.IsRequired,
            ValueSchema = includeExtractionSchema ? ValueSchema(field, fieldTypeExtensionRegistry) : null,
            // The extraction instruction is admin-configured text, wrapped for the same reason as DisplayName - the
            // server's own extraction prompt wraps it the same way. Empty means "infer from the name".
            ExtractionPrompt = includeExtractionSchema && !string.IsNullOrWhiteSpace(field.Description)
                ? PromptBoundary.WrapField(field.Description)
                : null
        };
    }

    /// <summary>
    /// The JSON schema a value of <paramref name="field"/> must satisfy: the fragment the server's own field extraction
    /// constrains its model with (a Select's options as an <c>enum</c>, a Table's columns under <c>values</c>, a date's
    /// format), so a client that extracts for itself is held to the same shape the submission is validated against.
    /// <c>null</c> for a field type this deployment does not register. Option values in an <c>enum</c> go out as written,
    /// as they do into the server's own response format: a value must match them exactly.
    /// </summary>
    private static JsonElement? ValueSchema(FieldDefinitionDto field, IVaultExtractFieldTypeRegistry registry)
    {
        if (!registry.TryGet(field.FieldTypeName, out var extension))
        {
            return null;
        }

        using var document = JsonDocument.Parse(extension!.BuildExtractionSchema(field.Configuration).ToJsonString());
        return document.RootElement.Clone();
    }
}

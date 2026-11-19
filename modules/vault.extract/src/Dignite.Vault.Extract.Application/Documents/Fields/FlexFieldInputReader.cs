using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Vault.Extract.FlexFields;
using Volo.Abp;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// The field values a caller wrote for a document, validated and converted in one step by
/// <see cref="FlexFieldValueReader"/>, the reader the extraction path uses too. Every key must name a field of the
/// document's type and every value must read as that field's type; anything else refuses the whole set with a
/// correctable error, before anything is written. Shared by the operator's field edit and a submission, so the two
/// accept exactly the same values.
/// </summary>
public static class FlexFieldInputReader
{
    /// <summary>The values to store, by field name; a value that reads as nothing (a JSON null) is left out.</summary>
    public static Dictionary<string, object?> Read(
        IReadOnlyDictionary<string, JsonElement> fields,
        IEnumerable<Field> definitions,
        string? documentTypeCode,
        IVaultExtractFieldTypeRegistry registry)
    {
        var definitionsByName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(fields.Count, StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (!definitionsByName.TryGetValue(key, out var definition))
            {
                throw new BusinessException(VaultExtractErrorCodes.ExtractedField.Unknown)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty);
            }

            if (!FlexFieldValueReader.TryRead(value, definition.FieldTypeName, definition.Configuration, registry, out var read))
            {
                throw new BusinessException(VaultExtractErrorCodes.ExtractedField.InvalidValue)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty)
                    .WithData("DataType", definition.FieldTypeName)
                    .WithData("AllowMultiple", bool.FalseString)
                    .WithData("JsonValueKind", value.ValueKind.ToString());
            }

            if (read != null)
            {
                values[key] = read;
            }
        }

        return values;
    }
}

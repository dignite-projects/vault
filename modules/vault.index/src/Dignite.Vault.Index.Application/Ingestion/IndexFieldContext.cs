using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dignite.Abp.FlexFields.Select;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// A document's field values as the lines put in front of each chunk's embedding text (<c>name: value</c>), the way
/// LlamaIndex embeds a node with its document's metadata. Read from the JSON kind of each value, as Extract's field
/// types render it: Text, DateTime (in its input mode's format) and CKEditor (Markdown or HTML, by its content format)
/// are strings, Number a number, Boolean true or false, Select its option value (an array of them when multiple), Tags
/// an array of strings, Table an array of row objects. Strings, numbers, booleans and arrays of strings or numbers are
/// kept, an array joined with commas; a Table is left out, and so is a value longer than <see cref="MaxValueLength"/>,
/// usually a CKEditor body. Capped at
/// <see cref="MaxLength"/>, so a document with many fields cannot push its chunk text past the embedding model's input.
/// <para>
/// With the type's field definitions (Extract's <c>GetFieldDefinitionsAsync</c>), a Select field's stored option value
/// is replaced by the option's label, which is what a question names ("approved", not <c>st_02</c>). A value with no
/// option, or an option without a label, stays as stored. Without definitions every value stays as stored.
/// </para>
/// </summary>
public static class IndexFieldContext
{
    /// <summary>The longest value kept; a longer one is a note or a description, not something a search names.</summary>
    public const int MaxValueLength = 100;

    /// <summary>The most characters of field lines in front of a chunk.</summary>
    public const int MaxLength = Indexing.IndexingConsts.MaxFieldContextLength;

    public static string? Describe(
        IReadOnlyDictionary<string, JsonElement>? fields,
        IReadOnlyDictionary<string, FieldDefinitionDto>? definitions = null)
    {
        if (fields is not { Count: > 0 })
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var (name, value) in fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var labels = definitions?.GetValueOrDefault(name) is { } definition ? SelectLabels(definition) : null;
            if (Text(value, labels) is not { Length: > 0 and <= MaxValueLength } text)
            {
                continue;
            }

            var line = name + ": " + text;
            if (builder.Length + line.Length + 1 > MaxLength)
            {
                break;
            }

            builder.Append(builder.Length == 0 ? "" : "\n").Append(line);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? Text(JsonElement value, IReadOnlyDictionary<string, string>? labels) => value.ValueKind switch
    {
        JsonValueKind.String => Label(value.GetString()?.Trim(), labels),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            => string.Join(", ", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? Label(item.GetString(), labels) : item.GetRawText())),
        _ => null
    };

    private static string? Label(string? value, IReadOnlyDictionary<string, string>? labels)
        => value is not null && labels?.GetValueOrDefault(value) is { } label ? label : value;

    /// <summary>A Select field's option labels by option value; <c>null</c> for any other field type.</summary>
    private static Dictionary<string, string>? SelectLabels(FieldDefinitionDto definition)
    {
        if (!string.Equals(definition.FieldTypeName, SelectFieldType.ControlName, StringComparison.Ordinal))
        {
            return null;
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in new SelectConfiguration(definition.Configuration).Options)
        {
            if (!string.IsNullOrWhiteSpace(option.Value) && !string.IsNullOrWhiteSpace(option.Text))
            {
                labels.TryAdd(option.Value, option.Text.Trim());
            }
        }

        return labels;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// A document's field values as the lines put in front of each chunk's embedding text (<c>name: value</c>), the way
/// LlamaIndex embeds a node with its document's metadata. Read from the JSON kind of each value, as Extract's field
/// types render it: Text, DateTime (in its input mode's format) and CKEditor are strings, Number a number, Boolean true
/// or false, Select its option value (an array of them when multiple), Tags an array of strings, Table an array of row
/// objects. Strings, numbers, booleans and arrays of strings or numbers are kept, an array joined with commas; a Table
/// is left out, and so is a value longer than <see cref="MaxValueLength"/>, usually a CKEditor body. Capped at
/// <see cref="MaxLength"/>, so a document with many fields cannot push its chunk text past the embedding model's input.
/// </summary>
public static class IndexFieldContext
{
    /// <summary>The longest value kept; a longer one is a note or a description, not something a search names.</summary>
    public const int MaxValueLength = 100;

    /// <summary>The most characters of field lines in front of a chunk.</summary>
    public const int MaxLength = Indexing.IndexingConsts.MaxFieldContextLength;

    public static string? Describe(IReadOnlyDictionary<string, JsonElement>? fields)
    {
        if (fields is not { Count: > 0 })
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var (name, value) in fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (Text(value) is not { Length: > 0 and <= MaxValueLength } text)
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

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Trim(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            => string.Join(", ", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())),
        _ => null
    };
}

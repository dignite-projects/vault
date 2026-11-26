using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// What survives of a JSON object answer the output limit cut off: each top-level member whose value closed, and, for
/// the object member the cut fell inside, those of its own members whose value closed. A value the cut fell inside is
/// left out whole: never a table's first rows, a list's first items or a string's first characters, because a partial
/// value stored as a value is a truncation nobody sees.
/// <para>
/// Read with <see cref="Utf8JsonReader"/> over a block that is not final, the reader's own mode for input that ends
/// early: a token that has not closed (an unterminated string, a number that might go on, an unfinished literal) is
/// reported as missing data rather than misread.
/// </para>
/// </summary>
public sealed class TruncatedJsonObject
{
    private TruncatedJsonObject(JsonObject members, IReadOnlySet<string> closedMembers)
    {
        Members = members;
        ClosedMembers = closedMembers;
    }

    /// <summary>The top-level members that survived, each either whole or (for the one cut inside) partial.</summary>
    public JsonObject Members { get; }

    /// <summary>The names of the top-level members whose value closed before the cut.</summary>
    public IReadOnlySet<string> ClosedMembers { get; }

    public static TruncatedJsonObject Read(string json)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        var members = new JsonObject();
        var closed = new HashSet<string>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(utf8, isFinalBlock: false, state: default);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return new TruncatedJsonObject(members, closed);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                if (!reader.Read())
                {
                    break;
                }

                if (TryReadClosedValue(ref reader, utf8, out var value))
                {
                    members[name] = value;
                    closed.Add(name);
                    continue;
                }

                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var partial = new JsonObject();
                    members[name] = partial;
                    ReadClosedMembers(ref reader, utf8, partial);
                }

                break;
            }
        }
        catch (JsonException)
        {
            // The text stopped being JSON before the cut (a model that ran off into prose, say): what closed before it stands.
        }

        return new TruncatedJsonObject(members, closed);
    }

    private static void ReadClosedMembers(ref Utf8JsonReader reader, byte[] utf8, JsonObject into)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            if (!reader.Read() || !TryReadClosedValue(ref reader, utf8, out var value))
            {
                return;
            }

            into[name] = value;
        }
    }

    /// <summary>
    /// The value starting at the reader's current token, when it closes within the data. A container is skipped on a
    /// copy of the reader, because <see cref="Utf8JsonReader.TrySkip"/> throws rather than returns <c>false</c> when the
    /// text turns invalid inside it, and the members before that point are still to be read.
    /// </summary>
    private static bool TryReadClosedValue(ref Utf8JsonReader reader, byte[] utf8, out JsonNode? value)
    {
        value = null;
        var start = (int)reader.TokenStartIndex;
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            var probe = reader;
            try
            {
                if (!probe.TrySkip())
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }

            reader = probe;
        }

        value = JsonNode.Parse(utf8.AsSpan(start, (int)reader.BytesConsumed - start));
        return true;
    }
}

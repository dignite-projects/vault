using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dignite.Vault.Index.Compilation;

/// <summary>
/// The content of a <see cref="Indexing.ArtifactTemplateNames.Summaries"/> artifact: the document summary and one
/// summary per outline section, keyed by the section's position (<c>1.2</c>, as in its node pointer).
/// </summary>
public sealed class DocumentSummaries
{
    /// <summary>
    /// Moves when compiling changes enough that summaries written before should not be served: outline and catalog
    /// serve only this version, and the next compilation redoes older ones. 2: document summaries no longer written from
    /// one sentence or the title alone, and summaries stating numbers their text does not are dropped.
    /// </summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Version { get; set; } = CurrentVersion;

    public string? Document { get; set; }

    public Dictionary<string, string> Sections { get; set; } = new(StringComparer.Ordinal);

    public static string Key(IReadOnlyList<int> path) => string.Join('.', path);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The summaries in <paramref name="json"/>; <c>null</c> when it is not this shape.</summary>
    public static DocumentSummaries? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DocumentSummaries>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What summaries are compiled from: the document's language and its enabled chunks, by position, heading path
    /// and text hash, as the outline sees them. Summaries are served only while this matches the hash they were built
    /// from, so a re-chunked document, a disabled chunk or another language never shows summaries of something else.
    /// </summary>
    public static string SourceHash(string? language, IEnumerable<(int Ordinal, string? HeadingPath, string ContentHash)> chunks)
    {
        var source = new StringBuilder();
        source.Append(language).Append('\n');
        foreach (var chunk in chunks.OrderBy(c => c.Ordinal))
        {
            source.Append(chunk.Ordinal).Append('\t').Append(chunk.HeadingPath).Append('\t').Append(chunk.ContentHash).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }
}

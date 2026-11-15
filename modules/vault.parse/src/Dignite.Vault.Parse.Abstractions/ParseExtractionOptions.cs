using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace Dignite.Vault.Parse;

/// <summary>
/// What a caller tells Parse about a file beyond its bytes and media type, in
/// <see cref="DocumentExtractionOptions.AdditionalProperties"/>: its name, from which engines are chosen by extension
/// (a browser sends <c>application/vnd.ms-excel</c> for a CSV), and the languages it is expected in.
/// </summary>
public static class ParseExtractionOptions
{
    /// <summary>The original file name, such as <c>請求書.pdf</c> (<see cref="string"/>).</summary>
    public const string FileName = "vault.fileName";

    /// <summary>Expected languages as BCP 47 tags, such as <c>ja</c> (<see cref="IReadOnlyList{T}"/> of <see cref="string"/>).</summary>
    public const string LanguageHints = "vault.languageHints";

    public static DocumentExtractionOptions Create(string? fileName, IEnumerable<string>? languageHints = null)
    {
        var options = new DocumentExtractionOptions();
        if (!string.IsNullOrEmpty(fileName))
        {
            options.AdditionalProperties[FileName] = fileName;
        }

        var hints = languageHints?.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        if (hints is { Count: > 0 })
        {
            options.AdditionalProperties[LanguageHints] = hints;
        }

        return options;
    }

    public static string? GetFileName(this DocumentExtractionOptions? options)
        => options?.AdditionalProperties.TryGetValue(FileName, out var value) == true ? value as string : null;

    /// <summary>The file's extension, lower case with its dot; empty when the name is unknown or has none.</summary>
    public static string GetFileExtension(this DocumentExtractionOptions? options)
        => options.GetFileName() is { Length: > 0 } name ? Path.GetExtension(name).ToLowerInvariant() : string.Empty;

    public static IReadOnlyList<string> GetLanguageHints(this DocumentExtractionOptions? options)
        => options?.AdditionalProperties.TryGetValue(LanguageHints, out var value) == true
            ? value switch
            {
                IEnumerable<string> hints => hints.ToList(),
                JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList(),
                _ => []
            }
            : [];
}

/// <summary>Reads and writes <see cref="ParseDocumentMetadata"/> on a document.</summary>
public static class ParseDocumentMetadataExtensions
{
    public static string? GetProvider(this ExtractionDocument document) => Text(document, ParseDocumentMetadata.Provider);

    public static string? GetLanguage(this ExtractionDocument document) => Text(document, ParseDocumentMetadata.Language);

    /// <summary>Why content is known to be missing; <c>null</c> when the engine believes it captured everything.</summary>
    public static string? GetIncompleteReason(this ExtractionDocument document) => Text(document, ParseDocumentMetadata.IncompleteReason);

    public static bool IsComplete(this ExtractionDocument document) => document.GetIncompleteReason() is null;

    public static ExtractionDocument SetProvider(this ExtractionDocument document, string? provider)
        => Set(document, ParseDocumentMetadata.Provider, provider);

    public static ExtractionDocument SetLanguage(this ExtractionDocument document, string? language)
        => Set(document, ParseDocumentMetadata.Language, language);

    /// <summary>Records why content is missing, after any reason already recorded.</summary>
    public static ExtractionDocument MarkIncomplete(this ExtractionDocument document, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return document;
        }

        var existing = document.GetIncompleteReason();
        return Set(document, ParseDocumentMetadata.IncompleteReason, existing is null ? reason.Trim() : existing + " " + reason.Trim());
    }

    private static string? Text(ExtractionDocument document, string key)
        => document.Metadata.TryGetValue(key, out var value) ? value as string ?? (value is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : null) : null;

    private static ExtractionDocument Set(ExtractionDocument document, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            document.Metadata.Remove(key);
        }
        else
        {
            document.Metadata[key] = value;
        }

        return document;
    }
}

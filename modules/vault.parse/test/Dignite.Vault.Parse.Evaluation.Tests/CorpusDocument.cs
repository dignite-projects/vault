using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dignite.Vault.Parse.Evaluation;

/// <summary>
/// One corpus document and its ground truth, as <c>etc/eval/parse_corpus.py</c> writes it: the elements in reading order,
/// the running header text and whether page numbers were printed (both must not reach the output).
/// </summary>
public sealed class CorpusDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string Id { get; init; } = default!;

    public string File { get; init; } = default!;

    public string MediaType { get; init; } = default!;

    public string Format { get; init; } = default!;

    public string Language { get; init; } = default!;

    /// <summary>The document has no text layer; parsing it needs OCR, and so a vision model.</summary>
    public bool Ocr { get; init; }

    public int? Pages { get; init; }

    /// <summary>Running header text printed on every page.</summary>
    public IReadOnlyList<string> Noise { get; init; } = [];

    /// <summary>A page number ("- n -") was printed at the foot of every page.</summary>
    public bool PageNumbers { get; init; }

    public IReadOnlyList<TruthElement> Elements { get; init; } = [];

    [JsonIgnore]
    public string Directory { get; private set; } = default!;

    public string Path => System.IO.Path.Combine(Directory, File);

    public string Extension => System.IO.Path.GetExtension(File);

    public static IReadOnlyList<CorpusDocument> LoadAll(string directory)
        => System.IO.Directory.GetFiles(directory, "*.json")
            .Select(path =>
            {
                var document = JsonSerializer.Deserialize<CorpusDocument>(System.IO.File.ReadAllText(path), JsonOptions)!;
                document.Directory = directory;
                return document;
            })
            .OrderBy(d => d.Id, System.StringComparer.Ordinal)
            .ToList();
}

public sealed class TruthElement
{
    /// <summary><c>heading</c>, <c>paragraph</c>, <c>list</c> or <c>table</c>.</summary>
    public string Type { get; init; } = default!;

    public int Level { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<string> Items { get; init; } = [];

    /// <summary>The table grid; a cell covered by another cell's span is <c>null</c>.</summary>
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; } = [];
}

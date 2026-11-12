using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dignite.Vault.Index.Evaluation;

/// <summary>
/// The evaluation set: a corpus of Markdown documents and the cases run against it. Cases come from two sources, as the
/// design names them: <c>field</c> cases are generated from each document's field values through per-type templates
/// (the "ask by a reviewed field" source), and <c>question</c> cases are written by hand.
/// </summary>
public sealed class EvaluationSet
{
    public int Version { get; init; }

    public string Description { get; init; } = default!;

    public List<EvaluationDocument> Documents { get; init; } = new();

    public Dictionary<string, List<string>> FieldTemplates { get; init; } = new();

    public List<EvaluationQuestion> Questions { get; init; } = new();

    public static EvaluationSet Load(string directory)
    {
        var json = File.ReadAllText(Path.Combine(directory, "evaluation-set.json"));
        var set = JsonSerializer.Deserialize<EvaluationSet>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        foreach (var document in set.Documents)
        {
            document.Markdown = File.ReadAllText(Path.Combine(directory, "Corpus", document.Id + ".md"));
        }

        return set;
    }

    /// <summary>Every case: the field cases expanded from the templates, then the written questions.</summary>
    public IReadOnlyList<EvaluationCase> Cases()
    {
        var cases = new List<EvaluationCase>();
        foreach (var document in Documents)
        {
            foreach (var template in FieldTemplates.GetValueOrDefault(document.Type) ?? [])
            {
                var query = Regex.Replace(template, @"\{(.+?)\}", m => document.Fields.GetValueOrDefault(m.Groups[1].Value) ?? m.Value);
                if (!query.Contains('{'))
                {
                    cases.Add(new EvaluationCase(query, document.Id, null, "ja", "field"));
                }
            }
        }

        cases.AddRange(Questions.Select(q => new EvaluationCase(q.Query, q.Document, q.Heading, q.Language, q.Source ?? "question")));
        return cases;
    }
}

public sealed class EvaluationDocument
{
    public string Id { get; init; } = default!;

    public string Type { get; init; } = default!;

    public string Title { get; init; } = default!;

    public Dictionary<string, string> Fields { get; init; } = new();

    public string Markdown { get; set; } = default!;

    /// <summary>A stable id, so a report names the same rows on every run.</summary>
    public Guid DocumentId => StableGuid("document:" + Id);

    public Guid TypeId => StableGuid("type:" + Type);

    public static Guid StableGuid(string value) => new(MD5.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class EvaluationQuestion
{
    public string Query { get; init; } = default!;

    public string Document { get; init; } = default!;

    public string? Heading { get; init; }

    public string Language { get; init; } = "ja";

    /// <summary><c>table</c> for a question whose answer is in a table; a written question otherwise.</summary>
    public string? Source { get; init; }
}

/// <param name="Heading">Part of the expected chunk's heading path; <c>null</c> when any chunk of the document will do.</param>
/// <param name="Source"><c>field</c>, <c>question</c>, or <c>table</c> for a written question answered by a table.</param>
public sealed record EvaluationCase(string Query, string Document, string? Heading, string Language, string Source);

using System;
using System.Collections.Generic;
using System.Linq;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Finds the passages field extraction quoted (<see cref="FieldExtractionWorkflowResult.Sources"/>) in the Markdown it
/// read, as ranges of that Markdown.
/// <para>
/// The approach is LangExtract's (google/langextract, Apache-2.0, <c>resolver.py</c>): the model quotes the source text
/// and code aligns the quote, exactly first and then leniently, instead of the model emitting positions. The lenient
/// match here compares the two texts with what a model does not reproduce faithfully taken out on both sides —
/// compatibility forms (NFKC: full-width digits, half-width kana), case, whitespace, Markdown emphasis and escapes,
/// table pipes and the HTML table markup the Markdown renderer writes, and entity-encoded characters — and maps the hit
/// back to the original characters. LangExtract's third step, a fuzzy token alignment for a quote the model paraphrased,
/// is not ported: the quote is asked for verbatim from the very text it is searched in. The normalization is
/// <see cref="NormalizedMarkdown"/>'s, shared with Index, which finds its chunks the same way.
/// </para>
/// <para>
/// A passage found more than once is located at its first occurrence. A passage not found is dropped: no source is
/// better than a wrong one.
/// </para>
/// </summary>
public static class FieldSourceLocator
{
    /// <summary>The ranges of <paramref name="markdown"/> holding each field's passages; a field none of whose passages was found is absent.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<DocumentTextSpan>> Locate(
        string markdown,
        IReadOnlyDictionary<string, IReadOnlyList<string>> passagesByField)
    {
        var result = new Dictionary<string, IReadOnlyList<DocumentTextSpan>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(markdown) || passagesByField.Count == 0)
        {
            return result;
        }

        NormalizedMarkdown? normalized = null;
        foreach (var (field, passages) in passagesByField)
        {
            var spans = new List<DocumentTextSpan>();
            foreach (var passage in passages)
            {
                if (string.IsNullOrWhiteSpace(passage))
                {
                    continue;
                }

                var span = FindExact(markdown, passage) ?? ToSpan((normalized ??= NormalizedMarkdown.Of(markdown)).Find(passage));
                if (span is not null && !spans.Contains(span))
                {
                    spans.Add(span);
                }
            }

            if (spans.Count > 0)
            {
                result[field] = spans.OrderBy(span => span.Offset).ToList();
            }
        }

        return result;
    }

    private static DocumentTextSpan? FindExact(string markdown, string passage)
    {
        var trimmed = passage.Trim();
        var index = markdown.IndexOf(trimmed, StringComparison.Ordinal);
        return index < 0 ? null : new DocumentTextSpan(index, trimmed.Length);
    }

    private static DocumentTextSpan? ToSpan((int Offset, int Length)? range)
        => range is { } r ? new DocumentTextSpan(r.Offset, r.Length) : null;
}

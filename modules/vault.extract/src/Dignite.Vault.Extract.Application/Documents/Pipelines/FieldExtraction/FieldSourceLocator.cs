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
/// A passage is located only where it is unique: verbatim, or failing that, compared normalized. A passage found more
/// than once (a value quoted bare, "1" or "13,200", without the words around it) or not at all is dropped: no source is
/// better than a wrong one, and nothing tells which occurrence the model read.
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

                var span = FindUnique(markdown, normalized ??= NormalizedMarkdown.Of(markdown), passage);
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

    /// <summary>
    /// The passage's one occurrence: the verbatim one when the passage is there verbatim, otherwise the normalized one;
    /// <c>null</c> when the comparison that finds it finds it more than once, or nothing finds it. A verbatim quote found
    /// once is what the model read, whatever looser variants the text also holds.
    /// </summary>
    private static DocumentTextSpan? FindUnique(string markdown, NormalizedMarkdown normalized, string passage)
    {
        var trimmed = passage.Trim();
        var exact = markdown.IndexOf(trimmed, StringComparison.Ordinal);
        if (exact >= 0)
        {
            return markdown.IndexOf(trimmed, exact + 1, StringComparison.Ordinal) < 0 ? new DocumentTextSpan(exact, trimmed.Length) : null;
        }

        var key = NormalizedMarkdown.Key(passage);
        var first = normalized.IndexOf(key);
        if (first < 0 || normalized.IndexOf(key, first + 1) >= 0)
        {
            return null;
        }

        var (offset, length) = normalized.ToSource(first, first + key.Length);
        return new DocumentTextSpan(offset, length);
    }
}

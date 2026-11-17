using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
/// is not ported: the quote is asked for verbatim from the very text it is searched in.
/// </para>
/// <para>
/// A passage found more than once is located at its first occurrence. A passage not found is dropped: no source is
/// better than a wrong one.
/// </para>
/// </summary>
public static class FieldSourceLocator
{
    private static readonly Regex TableTag = new(@"\G</?(?:table|thead|tbody|tr|td|th|br)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Entity, char Character)[] Entities =
        [("&amp;", '&'), ("&lt;", '<'), ("&gt;", '>'), ("&quot;", '"'), ("&#39;", '\''), ("&nbsp;", ' ')];

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

        NormalizedText? normalized = null;
        foreach (var (field, passages) in passagesByField)
        {
            var spans = new List<DocumentTextSpan>();
            foreach (var passage in passages)
            {
                if (string.IsNullOrWhiteSpace(passage))
                {
                    continue;
                }

                var span = FindExact(markdown, passage) ?? (normalized ??= NormalizedText.Of(markdown)).Find(passage);
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

    /// <summary>A text with the ignorable characters taken out, each kept character pointing at the original range it came from.</summary>
    private sealed class NormalizedText
    {
        private readonly string _text;
        private readonly int[] _starts;
        private readonly int[] _ends;

        private NormalizedText(string text, int[] starts, int[] ends)
        {
            _text = text;
            _starts = starts;
            _ends = ends;
        }

        public static NormalizedText Of(string source)
        {
            var text = new StringBuilder(source.Length);
            var starts = new List<int>(source.Length);
            var ends = new List<int>(source.Length);
            Normalize(source, (character, start, end) =>
            {
                text.Append(character);
                starts.Add(start);
                ends.Add(end);
            });
            return new NormalizedText(text.ToString(), starts.ToArray(), ends.ToArray());
        }

        public DocumentTextSpan? Find(string passage)
        {
            var key = new StringBuilder(passage.Length);
            Normalize(passage, (character, _, _) => key.Append(character));
            if (key.Length == 0)
            {
                return null;
            }

            var index = _text.IndexOf(key.ToString(), StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            var start = _starts[index];
            return new DocumentTextSpan(start, _ends[index + key.Length - 1] - start);
        }

        private static void Normalize(string source, Action<char, int, int> emit)
        {
            var i = 0;
            while (i < source.Length)
            {
                var c = source[i];
                if (c == '<' && TableTag.Match(source, i) is { Success: true } tag)
                {
                    i += tag.Length;
                    continue;
                }

                if (c == '&' && Entities.FirstOrDefault(e => string.CompareOrdinal(source, i, e.Entity, 0, e.Entity.Length) == 0) is { Entity: not null } entity)
                {
                    if (!char.IsWhiteSpace(entity.Character))
                    {
                        emit(entity.Character, i, i + entity.Entity.Length);
                    }

                    i += entity.Entity.Length;
                    continue;
                }

                var width = char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]) ? 2 : 1;
                if (width == 1 && (char.IsWhiteSpace(c) || "|*_#>`\\~".Contains(c)))
                {
                    i++;
                    continue;
                }

                if (width == 1 && char.IsSurrogate(c))
                {
                    // A lone surrogate is not normalizable text; it can only match itself.
                    emit(c, i, i + 1);
                    i++;
                    continue;
                }

                foreach (var normalized in source.Substring(i, width).Normalize(NormalizationForm.FormKC))
                {
                    if (!char.IsWhiteSpace(normalized))
                    {
                        emit(char.ToLowerInvariant(normalized), i, i + width);
                    }
                }

                i += width;
            }
        }
    }
}

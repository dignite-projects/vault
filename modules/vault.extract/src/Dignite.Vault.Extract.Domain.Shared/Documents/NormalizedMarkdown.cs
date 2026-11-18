using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// A Markdown text with what a re-rendering or a model does not reproduce faithfully taken out, each kept character
/// pointing back at the original range it came from, so a text derived from the Markdown can be found in it.
/// <para>
/// Taken out: whitespace, Markdown emphasis, escapes, heading and quote markers, table pipes, the HTML table markup the
/// Markdown renderer writes, and entity encoding (the entity's character is kept). The rest is compared by its
/// compatibility form (NFKC: full-width digits, half-width kana) in lower case. Field extraction finds the passages a
/// model quoted this way; Index finds the chunks DataIngestion re-rendered from the Markdown.
/// </para>
/// </summary>
public sealed class NormalizedMarkdown
{
    private static readonly Regex TableTag = new(@"\G</?(?:table|thead|tbody|tr|td|th|br)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Entity, char Character)[] Entities =
        [("&amp;", '&'), ("&lt;", '<'), ("&gt;", '>'), ("&quot;", '"'), ("&#39;", '\''), ("&nbsp;", ' ')];

    private readonly int[] _starts;
    private readonly int[] _ends;

    private NormalizedMarkdown(string text, int[] starts, int[] ends)
    {
        Text = text;
        _starts = starts;
        _ends = ends;
    }

    /// <summary>The normalized characters.</summary>
    public string Text { get; }

    public static NormalizedMarkdown Of(string source)
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
        return new NormalizedMarkdown(text.ToString(), starts.ToArray(), ends.ToArray());
    }

    /// <summary>What <paramref name="text"/> is compared as.</summary>
    public static string Key(string text)
    {
        var key = new StringBuilder(text.Length);
        Normalize(text, (character, _, _) => key.Append(character));
        return key.ToString();
    }

    /// <summary>
    /// The first occurrence of <paramref name="key"/> (a <see cref="Key"/>) at or after normalized position
    /// <paramref name="from"/>, as a normalized position; -1 when there is none.
    /// </summary>
    public int IndexOf(string key, int from = 0)
        => key.Length == 0 || from >= Text.Length ? -1 : Text.IndexOf(key, Math.Max(from, 0), StringComparison.Ordinal);

    /// <summary>The original range covering normalized characters <paramref name="start"/> to <paramref name="end"/> (exclusive).</summary>
    public (int Offset, int Length) ToSource(int start, int end)
    {
        var offset = _starts[start];
        return (offset, _ends[end - 1] - offset);
    }

    /// <summary>The original range of the first occurrence of <paramref name="text"/>, compared normalized; <c>null</c> when absent.</summary>
    public (int Offset, int Length)? Find(string text)
    {
        var key = Key(text);
        var index = IndexOf(key);
        return index < 0 ? null : ToSource(index, index + key.Length);
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

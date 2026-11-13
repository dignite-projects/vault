using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dignite.Vault.Index.Compilation;

/// <summary>
/// The numbers a summary states must be numbers its input states: an amount, a date, a count or a period the model
/// wrote without reading it is invented. The compile model has done that, filling a document summary with dates and
/// payment terms the document does not have, so <see cref="DocumentSummaryCompiler"/> keeps no summary that fails this.
/// <para>
/// Numbers are compared as values: digits after NFKC (full-width digits count), without thousands separators, a number
/// followed by 万, 千 or 億 counts as its value too (<c>30万</c> is 300000, <c>5万6000</c> is 56000), and kanji numerals in the input count as
/// their values (<c>三か月</c> is 3), as do afternoon hours on the 24-hour clock (<c>午後 7 時</c> is 19). Words without
/// digits are not checked.
/// </para>
/// </summary>
public static class SummaryFacts
{
    private static readonly Regex Number = new(@"(\d+(?:[.,]\d+)*)\s*(万|千|億)?", RegexOptions.Compiled);
    private static readonly Regex KanjiNumber = new("[〇一二三四五六七八九十百千万]+", RegexOptions.Compiled);

    /// <summary>An afternoon hour, <c>午後 7 時</c>: the input states 19 as well, as a summary may write it.</summary>
    private static readonly Regex AfternoonHour = new(@"午後\s*(\d{1,2})\s*時", RegexOptions.Compiled);

    /// <summary>A number written in 万 with the rest after it, such as <c>5万6000</c>: one value, 56000.</summary>
    private static readonly Regex ManCompound = new(@"(\d+)万(\d{1,4})(?!\d)", RegexOptions.Compiled);

    private static readonly Dictionary<char, int> KanjiDigits = new()
    {
        ['〇'] = 0,
        ['一'] = 1,
        ['二'] = 2,
        ['三'] = 3,
        ['四'] = 4,
        ['五'] = 5,
        ['六'] = 6,
        ['七'] = 7,
        ['八'] = 8,
        ['九'] = 9
    };

    /// <summary>The numbers in <paramref name="summary"/> that <paramref name="input"/> does not state.</summary>
    public static IReadOnlyList<string> Unsupported(string summary, string input)
    {
        var stated = InputNumbers(input);
        return SummaryNumbers(summary).Where(n => !n.Any(stated.Contains)).Select(n => n[0]).Distinct().ToList();
    }

    /// <summary>Each number of a summary with the values it may stand for: as written, and multiplied by its unit.</summary>
    private static IEnumerable<string[]> SummaryNumbers(string text)
        => Tokens(text.Normalize(NormalizationForm.FormKC))
            .Where(t => t.Plain != "0")
            .Select(t => t.Unit is { } unit && Scaled(t.Plain, unit) is { } scaled ? new[] { t.Plain, scaled } : [t.Plain]);

    private static HashSet<string> InputNumbers(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var numbers = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var (plain, unit) in Tokens(normalized))
        {
            numbers.Add(plain);
            if (unit is not null && Scaled(plain, unit) is { } scaled)
            {
                numbers.Add(scaled);
            }
        }

        foreach (Match match in AfternoonHour.Matches(normalized))
        {
            var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (hour < 12)
            {
                numbers.Add((hour + 12).ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (Match match in KanjiNumber.Matches(normalized))
        {
            if (match.Value.Any(KanjiDigits.ContainsKey))
            {
                numbers.Add(Kanji(match.Value).ToString(CultureInfo.InvariantCulture));
            }
        }

        return numbers;
    }

    /// <summary>
    /// The numbers in a text, with the unit that follows one: <c>1,820</c> is one number, <c>1,2</c> two, and a leading
    /// zero is dropped, so <c>2026-11-01</c> states 1 as <c>11月1日</c> does.
    /// </summary>
    private static IEnumerable<(string Plain, string? Unit)> Tokens(string text)
    {
        text = ManCompound.Replace(text, m => (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 10_000
                                               + long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
        foreach (Match match in Number.Matches(text))
        {
            var number = match.Groups[1].Value;
            var unit = match.Groups[2].Success ? match.Groups[2].Value : null;
            if (Thousands.IsMatch(number) || !number.Contains(','))
            {
                yield return (Plain(number.Replace(",", string.Empty)), unit);
                continue;
            }

            var parts = number.Split(',');
            for (var i = 0; i < parts.Length; i++)
            {
                yield return (Plain(parts[i]), i == parts.Length - 1 ? unit : null);
            }
        }
    }

    private static readonly Regex Thousands = new(@"^\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>Without leading zeros, and a decimal without trailing ones.</summary>
    private static string Plain(string number)
    {
        if (number.Contains('.'))
        {
            number = number.TrimEnd('0').TrimEnd('.');
        }

        var trimmed = number.TrimStart('0');
        return trimmed.Length == 0 || trimmed[0] == '.' ? "0" + trimmed : trimmed;
    }

    private static string? Scaled(string plain, string unit)
    {
        if (!decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var scaled = value * unit switch { "万" => 10_000m, "千" => 1_000m, _ => 100_000_000m };
        return scaled == decimal.Truncate(scaled) ? decimal.Truncate(scaled).ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A kanji numeral's value, for numbers below a hundred million: 十二 is 12, 二十 is 20, 三千五百 is 3500.</summary>
    private static long Kanji(string numeral)
    {
        long total = 0, section = 0, digit = 0;
        foreach (var ch in numeral)
        {
            if (KanjiDigits.TryGetValue(ch, out var value))
            {
                digit = value;
                continue;
            }

            var unit = ch switch { '十' => 10, '百' => 100, '千' => 1_000, _ => 0 };
            if (unit > 0)
            {
                section += (digit == 0 ? 1 : digit) * unit;
                digit = 0;
            }
            else
            {
                total += (section + digit) * 10_000;
                section = digit = 0;
            }
        }

        return total + section + digit;
    }
}

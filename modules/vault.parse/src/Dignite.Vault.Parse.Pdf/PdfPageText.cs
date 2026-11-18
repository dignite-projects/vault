using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>What a page's digital text layer is worth.</summary>
internal enum PdfPageTextQuality
{
    /// <summary>Readable text: the page is read from its text layer.</summary>
    Usable,

    /// <summary>No letter or digit at all: a scanned page, or a page of images and drawings.</summary>
    Missing,

    /// <summary>Text that is not what the page shows: a broken font encoding, or scan noise. The page is read by OCR.</summary>
    Garbled
}

/// <summary>
/// Judges a page's text layer, so a page that has none, or one that does not say what the page shows, is read by OCR
/// instead. The three garble rules are RAGFlow's (<c>internal/deepdoc/parser/pdf/util/garbled.go</c>, Apache-2.0): too
/// many characters no font maps to text, a subset font whose text is mostly ASCII punctuation with no CJK, and scan
/// noise with no word-like run. RAGFlow's fourth rule, keeping a clean layer its recogniser cannot spell, does not apply:
/// the OCR engines here read any script.
/// </summary>
internal static partial class PdfPageText
{
    // Rules need this many non-space characters before they judge; a sparse page keeps its text layer.
    private const int MinCharacters = 20;

    // Share of unmappable characters (private use, U+FFFD, controls, unassigned) that marks the text garbled.
    private const double UnmappedRatio = 0.3;

    // Broken font encoding: subset fonts carry at least this share of the characters, ...
    private const double SubsetFontRatio = 0.3;

    // ... CJK makes up less than this share, ...
    private const double MaxCjkRatio = 0.05;

    // ... and ASCII punctuation more than this share.
    private const double MinPunctuationRatio = 0.4;

    // Scan noise is judged on at least this many characters; real text with this share of digits is never noise.
    private const int MinNoiseCharacters = 30;
    private const double NoiseMaxDigitRatio = 0.10;

    public static PdfPageTextQuality Judge(IReadOnlyList<Word> words)
    {
        if (!words.Any(word => word.Text.Any(char.IsLetterOrDigit)))
        {
            return PdfPageTextQuality.Missing;
        }

        var letters = words.SelectMany(word => word.Letters).Where(letter => !string.IsNullOrWhiteSpace(letter.Value)).ToList();
        var text = string.Join(" ", words.Select(word => word.Text));
        return letters.Count >= MinCharacters && (IsUnmapped(text) || IsBrokenFontEncoding(letters) || IsScanNoise(text))
            ? PdfPageTextQuality.Garbled
            : PdfPageTextQuality.Usable;
    }

    /// <summary>Whether at least <see cref="UnmappedRatio"/> of the characters map to no text, or a CID placeholder appears.</summary>
    internal static bool IsUnmapped(string text)
    {
        if (CidPlaceholder().IsMatch(text))
        {
            return true;
        }

        var total = 0;
        var unmapped = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            total++;
            if (IsUnmapped(rune))
            {
                unmapped++;
            }
        }

        return total > 0 && unmapped >= total * UnmappedRatio;
    }

    private static bool IsUnmapped(Rune rune)
        => rune.Value == 0xFFFD
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.PrivateUse
                or UnicodeCategory.Control
                or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.Surrogate;

    /// <summary>
    /// A font whose glyphs were mapped to the wrong codes: subset fonts carry the text, yet it is nearly all ASCII
    /// punctuation and has no CJK — what a CJK subset font with a broken ToUnicode map reads as.
    /// </summary>
    internal static bool IsBrokenFontEncoding(IReadOnlyList<Letter> letters)
        => IsBrokenFontEncoding(letters.Select(letter => (letter.Value, letter.FontName)).ToList());

    /// <summary>
    /// The same over each glyph's text and font name, in content order. A run of three or more of one punctuation
    /// character (the dot leaders of a table of contents, a rule of dashes or underscores) is layout, not text, and is
    /// not counted.
    /// </summary>
    internal static bool IsBrokenFontEncoding(IReadOnlyList<(string Value, string? FontName)> letters)
    {
        var subset = letters.Count(letter => SubsetFontPrefix().IsMatch(letter.FontName ?? string.Empty));
        if (subset < letters.Count * SubsetFontRatio)
        {
            return false;
        }

        var cjk = letters.Count(letter => letter.Value.Length > 0 && IsCjkLike(letter.Value[0]));
        var punctuation = 0;
        for (var i = 0; i < letters.Count; i++)
        {
            var value = letters[i].Value;
            if (value.Length == 0 || !char.IsAscii(value[0]) || !(char.IsPunctuation(value[0]) || char.IsSymbol(value[0])))
            {
                continue;
            }

            var run = 1;
            for (var j = i - 1; j >= 0 && letters[j].Value == value; j--)
            {
                run++;
            }

            for (var j = i + 1; j < letters.Count && letters[j].Value == value; j++)
            {
                run++;
            }

            if (run < 3)
            {
                punctuation++;
            }
        }

        return cjk < letters.Count * MaxCjkRatio && punctuation > letters.Count * MinPunctuationRatio;
    }

    /// <summary>
    /// Scan noise: enough characters, few digits, and not one word-like run — four Latin letters in a row, two
    /// CJK characters, or four non-ASCII letters.
    /// </summary>
    internal static bool IsScanNoise(string text)
    {
        var total = 0;
        var digits = 0;
        int lower = 0, cjk = 0, other = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                lower = cjk = other = 0;
                continue;
            }

            total++;
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }

            // Either case: a form, a cover or a title block in capitals reads as words too.
            lower = char.IsAsciiLetter(c) ? lower + 1 : 0;
            cjk = IsCjkLike(c) && !IsFullwidthForm(c) ? cjk + 1 : 0;
            other = char.IsLetter(c) && !char.IsAscii(c) ? other + 1 : 0;
            if (lower >= 4 || cjk >= 2 || other >= 4)
            {
                return false;
            }
        }

        return total >= MinNoiseCharacters && digits < total * NoiseMaxDigitRatio;
    }

    /// <summary>CJK ideographs and compatibility forms, Hangul, kana, and fullwidth forms.</summary>
    private static bool IsCjkLike(char c)
        => c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '가' and <= '힯' || IsFullwidthForm(c);

    private static bool IsFullwidthForm(char c) => c is >= '＀' and <= '～';

    /// <summary>A PDF subset font name: a tag of two to six capitals or digits and a plus, as in <c>ABCDEF+SimSun</c>.</summary>
    [GeneratedRegex(@"^[A-Z0-9]{2,6}\+")]
    private static partial Regex SubsetFontPrefix();

    /// <summary>pdfminer-style placeholders for glyphs with no Unicode, as in <c>(cid:123)</c>.</summary>
    [GeneratedRegex(@"\(cid\s*:\s*\d+\s*\)")]
    private static partial Regex CidPlaceholder();
}

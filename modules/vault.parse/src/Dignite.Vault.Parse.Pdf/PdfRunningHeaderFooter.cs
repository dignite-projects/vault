using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// Cross-page running header / footer + page-number detection for a digital PDF (#383). Such chrome
/// (a title or confidentiality line repeated at the page edge, "Page 3 of 10", "第 3 页", a bare "3")
/// is mechanical noise that pollutes the Markdown body and degrades downstream chunking / classification /
/// field extraction, so it is stripped before rendering.
/// <para>
/// <b>Mechanism — repetition, gated by position (not position alone).</b> Position only <i>selects</i>
/// candidates: the first / last <see cref="CandidateLinesPerEdge"/> visual lines of each page (the
/// header / footer band). A candidate is dropped only when its <b>digit-normalized</b> text
/// (so "第 1 页" / "第 2 页" collapse to one template, and "Page 1 of 10" matches "Page 2 of 10")
/// repeats in the <b>same band</b> across at least <see cref="MinRepeatPageFraction"/> of the pages
/// (floor <see cref="MinRepeatPageCount"/>), and its numbers behave like chrome: the same on every page, or a page
/// number, which grows with the page (<see cref="NumbersAreChrome"/>). A subtotal or a ledger row in the band has the
/// same template on every page but numbers of its own, and stays. This is deliberately <b>not</b> "delete anything in the
/// margin band": a per-page signature block / seal / total / footnote sits in the band on a single page,
/// never repeats, and is therefore preserved — honouring the PdfPig stack's non-lossy discipline
/// (cf. <see cref="PdfExtractor.IsFullPageScanBackground"/> erring toward keeping).
/// </para>
/// <para>
/// <b>Single-page documents drop nothing</b> — there is no cross-page signal, which is the natural and
/// accepted consequence of a repetition-based detector (a lone invoice page keeps its footer).
/// </para>
/// </summary>
internal static class PdfRunningHeaderFooter
{
    /// <summary>How many visual lines at each page edge (top and bottom) are header/footer candidates.</summary>
    private const int CandidateLinesPerEdge = 3;

    /// <summary>Absolute floor on the number of pages a candidate must repeat across before it is dropped.</summary>
    private const int MinRepeatPageCount = 2;

    /// <summary>
    /// Fraction of the document's pages a candidate must repeat across (in the same band) to be treated as
    /// running chrome. With the <see cref="MinRepeatPageCount"/> floor, a 2-page document needs the line on
    /// both pages; a 10-page document needs it on at least five.
    /// </summary>
    private const double MinRepeatPageFraction = 0.5;

    // Runs of ASCII or full-width digits collapse to a single placeholder so a varying page number reads as
    // one stable template across pages ("第 1 页"/"第 2 页" -> "第 # 页", "Page 1 of 10" -> "page # of #").
    private static readonly Regex DigitRun = new("[0-9０-９]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // Band tags keyed into the template so a top-of-page line and a bottom-of-page line never count as the
    // same running candidate (a header repeats at the top, a footer at the bottom).
    private const char TopBand = 'T';
    private const char BottomBand = 'B';

    /// <summary>
    /// Detects running header/footer/page-number lines across the given pages and returns, per page (by
    /// index), the set of <see cref="Word"/> instances to drop before rendering. The returned word instances
    /// are the same references found in <paramref name="pageWords"/>, so the caller filters by reference
    /// identity. Pages with nothing to drop get an empty set; a document with fewer than
    /// <see cref="MinRepeatPageCount"/> pages drops nothing.
    /// </summary>
    public static IReadOnlyList<IReadOnlySet<Word>> Detect(IReadOnlyList<IReadOnlyList<Word>> pageWords)
    {
        var pageCount = pageWords.Count;
        var result = new List<IReadOnlySet<Word>>(pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            result.Add(EmptyWordSet);
        }

        if (pageCount < MinRepeatPageCount)
        {
            // Single page (or empty): no cross-page repetition is possible, so nothing is running chrome.
            return result;
        }

        // Collect every edge-band candidate line, keyed by (band, normalized text), tracking which pages each
        // template appears on. A line that is both in the top and bottom band (a very short page) registers
        // under both bands.
        var candidates = new List<Candidate>();
        var pagesByKey = new Dictionary<string, HashSet<int>>();

        for (var page = 0; page < pageCount; page++)
        {
            var lines = PdfReadingOrder.GroupWordsIntoLinesWithWords(pageWords[page]);
            var lineCount = lines.Count;
            for (var i = 0; i < lineCount; i++)
            {
                var isTop = i < CandidateLinesPerEdge;
                var isBottom = i >= lineCount - CandidateLinesPerEdge;
                if (!isTop && !isBottom)
                {
                    continue;
                }

                var normalized = Normalize(lines[i].Text);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (isTop)
                {
                    Register(candidates, pagesByKey, page, TopBand, normalized, lines[i].Text, lines[i].Words);
                }

                if (isBottom)
                {
                    Register(candidates, pagesByKey, page, BottomBand, normalized, lines[i].Text, lines[i].Words);
                }
            }
        }

        // Per template, only the lines whose numbers agree with the template's chrome pattern go, and only when those
        // lines alone repeat on enough pages: one stray line of the same shape (a bare number at the foot of one page
        // beside its page number) stays, instead of making every page keep its page number.
        var threshold = Math.Max(MinRepeatPageCount, (int)Math.Ceiling(pageCount * MinRepeatPageFraction));
        var running = new List<Candidate>();
        foreach (var template in candidates.GroupBy(c => c.Key))
        {
            if (pagesByKey[template.Key].Count < threshold)
            {
                continue;
            }

            var chrome = ChromeLines(template.ToList());
            if (chrome.Select(c => c.Page).Distinct().Count() >= threshold)
            {
                running.AddRange(chrome);
            }
        }

        if (running.Count == 0)
        {
            return result;
        }

        // Materialize the per-page drop sets only for pages that actually have running content.
        var dropByPage = new Dictionary<int, HashSet<Word>>();
        foreach (var candidate in running)
        {

            if (!dropByPage.TryGetValue(candidate.Page, out var set))
            {
                set = new HashSet<Word>();
                dropByPage[candidate.Page] = set;
            }

            foreach (var word in candidate.Words)
            {
                set.Add(word);
            }
        }

        foreach (var (page, set) in dropByPage)
        {
            result[page] = set;
        }

        return result;
    }

    private static void Register(
        List<Candidate> candidates,
        Dictionary<string, HashSet<int>> pagesByKey,
        int page,
        char band,
        string normalized,
        string text,
        IReadOnlyList<Word> words)
    {
        var key = band + normalized;
        candidates.Add(new Candidate(page, key, text, words));
        if (!pagesByKey.TryGetValue(key, out var pages))
        {
            pages = new HashSet<int>();
            pagesByKey[key] = pages;
        }

        pages.Add(page);
    }

    /// <summary>
    /// Normalizes a candidate line into a cross-page-stable template: collapse digit runs to a placeholder
    /// (so page numbers match), collapse whitespace, trim, and lower-case (a running header keeps consistent
    /// case across pages; lower-casing is a no-op for CJK and harmless for Latin).
    /// </summary>
    private static string Normalize(string text)
    {
        var normalized = DigitRun.Replace(text, "#");
        normalized = WhitespaceRun.Replace(normalized, " ").Trim();
        return normalized.ToLowerInvariant();
    }

    /// <summary>
    /// The lines of one template whose numbers are chrome's. Each number of the template is either the same on every
    /// page (a date in a header, the "10" of "of 10") or a page number, its value minus the page's index the same on
    /// every page (so it may start anywhere and skip a page). For each number the pattern that the lines of the most
    /// pages share is taken (a fixed value, or a fixed offset from the page index; the fixed value when they tie), and a
    /// line is chrome when every number of it follows its pattern. A number that changes otherwise is the page's own
    /// data, so a ledger row or subtotal shares no pattern beyond a page or two.
    /// </summary>
    private static List<Candidate> ChromeLines(IReadOnlyList<Candidate> lines)
    {
        // A fixed number is compared by value, or as written when it is too long to be a page number; an offset needs
        // the value.
        var digits = lines.Select(line => DigitRun.Matches(line.Text).Select(m => m.Value).ToList()).ToList();
        var follows = Enumerable.Repeat(true, lines.Count).ToArray();
        var count = digits.Min(n => n.Count);
        for (var run = 0; run < count; run++)
        {
            var fixedValue = Dominant(lines, i => Fixed(digits[i][run]));
            var fixedOffset = Dominant(lines, i => Offset(digits[i][run], lines[i].Page));
            var byOffset = fixedOffset.Pages > fixedValue.Pages;
            for (var i = 0; i < lines.Count; i++)
            {
                follows[i] &= byOffset
                    ? Offset(digits[i][run], lines[i].Page) is { } offset && offset == fixedOffset.Pattern
                    : Fixed(digits[i][run]) == fixedValue.Pattern;
            }
        }

        return lines.Where((line, i) => follows[i] && digits[i].Count == count).ToList();
    }

    private static string Fixed(string digits) => Value(digits)?.ToString(CultureInfo.InvariantCulture) ?? digits;

    private static string? Offset(string digits, int page) => (Value(digits) - page)?.ToString(CultureInfo.InvariantCulture);

    /// <summary>The pattern the lines of the most pages share, and how many pages that is.</summary>
    private static (string? Pattern, int Pages) Dominant(IReadOnlyList<Candidate> lines, Func<int, string?> patternOf)
    {
        return Enumerable.Range(0, lines.Count)
            .Where(i => patternOf(i) is not null)
            .GroupBy(patternOf)
            .Select(g => (g.Key, Pages: g.Select(i => lines[i].Page).Distinct().Count()))
            .OrderByDescending(g => g.Pages)
            .FirstOrDefault();
    }

    /// <summary>The value of a run of ASCII or full-width digits; null when it is too long to be a page number.</summary>
    private static long? Value(string digits)
    {
        if (digits.Length > 9)
        {
            return null;
        }

        long value = 0;
        foreach (var c in digits)
        {
            value = value * 10 + (c >= '0' && c <= '9' ? c - '0' : c - '０');
        }

        return value;
    }

    private static readonly IReadOnlySet<Word> EmptyWordSet = new HashSet<Word>();

    private readonly record struct Candidate(int Page, string Key, string Text, IReadOnlyList<Word> Words);
}

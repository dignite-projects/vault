using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Vault.Extract.Ai;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// One contiguous part of a document's Markdown that a field-extraction call reads on its own, because the rows of its
/// tables are longer than one answer can hold. <see cref="Offset"/> and <see cref="Length"/> are the part of the Markdown
/// the excerpt owns; the excerpts of one split cover the Markdown once, in order, without overlap.
/// </summary>
/// <param name="Offset">Where the owned part starts in the Markdown.</param>
/// <param name="Length">How long the owned part is, its line breaks included.</param>
/// <param name="Text">The owned part without its trailing line breaks.</param>
public sealed record MarkdownExcerpt(int Offset, int Length, string Text)
{
    /// <summary>
    /// The header of the table the excerpt starts inside, when the cut before it fell between two of that table's rows:
    /// a pipe table's header and delimiter lines, or an HTML table's opening tag and header rows.
    /// </summary>
    public string? TableHeader { get; init; }

    /// <summary>
    /// The lines just before the excerpt, when the cut before it fell between two lines that may be one record (text, not
    /// a recognised table): read-only context, so a row that began there is known to be the previous excerpt's.
    /// </summary>
    public string? ContextBefore { get; init; }

    /// <summary>The lines just after the excerpt, under the same condition: what a row that begins in it continues with.</summary>
    public string? ContextAfter { get; init; }

    /// <summary>
    /// The rows of recognised tables (pipe or HTML, headers not counted) that begin in the excerpt: how many rows an answer
    /// that reads every table in it returns. An answer may return fewer for a table no field asks for.
    /// </summary>
    public int TableRows { get; init; }
}

/// <summary>
/// Splits Markdown into <see cref="MarkdownExcerpt"/>s for reading a long table in parts.
/// <para>
/// Cuts fall only between lines. The Markdown renderer writes every table row on a line of its own (a pipe-table row,
/// or an HTML <c>&lt;tr&gt;</c> line), so a cut between lines never splits a row of a table the parse recognised. A cut
/// never falls inside a table's header, inside an HTML row that spans lines, or between rows an HTML <c>rowspan</c> joins.
/// Within that, a cut at a table's edge is preferred, so an excerpt rarely holds the end of one table and the start of
/// the next (a statement's table repeated on every page): models asked for the rows of a table that ran on after a page
/// break returned only the first part's (2026-11-26). Then a cut at a blank line or between two table rows, then one
/// between two lines of text, the way LangExtract's <c>ChunkIterator</c> (google/langextract, Apache-2.0,
/// <c>chunking.py</c>) prefers a line break to a cut inside a sentence.
/// </para>
/// <para>
/// A cut between two table rows carries the table's header into the next excerpt, as Extend's "table header
/// continuation" does. A cut between two lines of text may split a record a table was flattened into, so both excerpts
/// get the neighbouring lines as read-only context (LangExtract's <c>context_window_chars</c>, both ways here), and a
/// record belongs to the excerpt it begins in. The excerpts never overlap, so nothing has to be deduplicated after the
/// merge: content-based deduplication would also merge the identical rows a bank statement legitimately repeats.
/// </para>
/// </summary>
public static class MarkdownExcerptSplitter
{
    /// <summary>Lines of context given on each side of a cut between two lines of text.</summary>
    public const int ContextLines = 3;

    /// <summary>Characters of context given on each side, at most.</summary>
    public const int MaxContextChars = 400;

    private static readonly Regex RowOpen = new(@"<tr[\s>]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RowClose = new(@"</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RowSpan = new(@"rowspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The characters of the rows of every table recognised in <paramref name="markdown"/> (pipe tables and HTML tables,
    /// headers not counted): what a table field's answer would transcribe.
    /// </summary>
    public static int TableRowChars(string markdown)
    {
        var lines = MarkdownLines.Of(markdown);
        var chars = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines.Roles[i] is LineRole.Row or LineRole.CoveredRow or LineRole.InsideRow)
            {
                chars += lines.Lengths[i];
            }
        }

        return chars;
    }

    /// <summary>
    /// Splits <paramref name="markdown"/> into excerpts of at most <paramref name="maxChars"/> characters each, except
    /// where no cut is allowed within that length (one long line, a long HTML row): the excerpt then runs to the first
    /// allowed cut.
    /// </summary>
    public static IReadOnlyList<MarkdownExcerpt> Split(string markdown, int maxChars)
    {
        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "An excerpt must be allowed at least one character.");
        }

        var lines = MarkdownLines.Of(markdown);
        var excerpts = new List<MarkdownExcerpt>();
        int start = 0, previousStart = 0;
        while (start < lines.Count)
        {
            var end = ChooseEnd(lines, start, maxChars);
            var excerpt = Build(markdown, lines, start, end);
            if (string.IsNullOrWhiteSpace(excerpt.Text) && excerpts.Count > 0)
            {
                // Only blank lines (the document's trailing line break, say): nothing to read, so the previous excerpt owns them.
                excerpts[^1] = Build(markdown, lines, previousStart, end);
            }
            else
            {
                excerpts.Add(excerpt);
                previousStart = start;
            }

            start = end;
        }

        return excerpts;
    }

    /// <summary>
    /// <paramref name="excerpt"/> cut in two at the allowed cut nearest its middle, for reading again in two calls; the
    /// excerpt alone when no cut is allowed inside it.
    /// </summary>
    public static IReadOnlyList<MarkdownExcerpt> Halve(string markdown, MarkdownExcerpt excerpt)
    {
        var lines = MarkdownLines.Of(markdown);
        var first = lines.LineAt(excerpt.Offset);
        var end = excerpt.Offset + excerpt.Length >= markdown.Length ? lines.Count : lines.LineAt(excerpt.Offset + excerpt.Length);
        var middle = excerpt.Offset + (excerpt.Length / 2);

        var best = -1;
        for (var k = first + 1; k < end; k++)
        {
            if (Cut(lines, k) == CutKind.Forbidden)
            {
                continue;
            }

            if (best < 0 || Math.Abs(lines.Starts[k] - middle) < Math.Abs(lines.Starts[best] - middle))
            {
                best = k;
            }
        }

        return best < 0
            ? [excerpt]
            : [Build(markdown, lines, first, best), Build(markdown, lines, best, end)];
    }

    /// <summary>
    /// Where the excerpt starting at line <paramref name="start"/> ends (exclusive): the furthest table edge past a quarter
    /// of the allowed length, else the furthest blank line or cut between table rows in its second half, else the furthest
    /// cut between two text lines there, else the furthest allowed cut at all, else the first allowed cut beyond the length.
    /// </summary>
    private static int ChooseEnd(MarkdownLines lines, int start, int maxChars)
    {
        if (lines.CharsBetween(start, lines.Count) <= maxChars)
        {
            return lines.Count;
        }

        int edge = -1, structural = -1, textual = -1, anywhere = -1;
        for (var k = start + 1; k < lines.Count; k++)
        {
            var size = lines.CharsBetween(start, k);
            if (size > maxChars)
            {
                break;
            }

            var kind = Cut(lines, k);
            if (kind == CutKind.Forbidden)
            {
                continue;
            }

            anywhere = k;
            if (kind == CutKind.TableEdge && size * 4 >= maxChars)
            {
                edge = k;
            }

            if (size * 2 < maxChars)
            {
                continue;
            }

            if (kind == CutKind.Text)
            {
                textual = k;
            }
            else
            {
                structural = k;
            }
        }

        if (edge > 0)
        {
            return edge;
        }

        if (structural > 0)
        {
            return structural;
        }

        if (textual > 0)
        {
            return textual;
        }

        if (anywhere > 0)
        {
            return anywhere;
        }

        for (var k = start + 1; k < lines.Count; k++)
        {
            if (Cut(lines, k) != CutKind.Forbidden)
            {
                return k;
            }
        }

        return lines.Count;
    }

    private static MarkdownExcerpt Build(string markdown, MarkdownLines lines, int start, int end)
    {
        var offset = lines.Starts[start];
        var stop = end < lines.Count ? lines.Starts[end] : markdown.Length;
        var owned = markdown.Substring(offset, stop - offset);

        string? header = null, before = null, after = null;
        if (start > 0)
        {
            switch (Cut(lines, start))
            {
                case CutKind.TableRow:
                    var table = lines.Tables[lines.TableOf[start]];
                    header = markdown.Substring(
                        lines.Starts[table.HeaderStart],
                        lines.Starts[table.BodyStart - 1] + lines.Lengths[table.BodyStart - 1] - lines.Starts[table.HeaderStart]);
                    break;
                case CutKind.Text:
                    before = Context(markdown, lines, start - 1, -1);
                    break;
            }
        }

        if (end < lines.Count && Cut(lines, end) == CutKind.Text)
        {
            after = Context(markdown, lines, end, +1);
        }

        return new MarkdownExcerpt(offset, owned.Length, owned.TrimEnd('\r', '\n'))
        {
            TableHeader = header,
            ContextBefore = before,
            ContextAfter = after,
            TableRows = Enumerable.Range(start, end - start).Count(line => lines.Roles[line] is LineRole.Row or LineRole.CoveredRow)
        };
    }

    /// <summary>Up to <see cref="ContextLines"/> non-blank lines from <paramref name="from"/> in <paramref name="step"/>'s direction.</summary>
    private static string? Context(string markdown, MarkdownLines lines, int from, int step)
    {
        var taken = new List<string>();
        for (var i = from; i >= 0 && i < lines.Count && taken.Count < ContextLines && !lines.IsBlank(i); i += step)
        {
            taken.Add(markdown.Substring(lines.Starts[i], lines.Lengths[i]).TrimEnd('\r'));
        }

        if (taken.Count == 0)
        {
            return null;
        }

        if (step < 0)
        {
            taken.Reverse();
            var text = string.Join('\n', taken);
            return text.Length <= MaxContextChars ? text : Tail(text, MaxContextChars);
        }

        return TextTruncator.AtCharBoundary(string.Join('\n', taken), MaxContextChars);
    }

    /// <summary>The trailing <paramref name="maxChars"/> code units, moving forward one when that would split a surrogate pair.</summary>
    private static string Tail(string text, int maxChars)
    {
        var start = text.Length - maxChars;
        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    /// <summary>What a cut before line <paramref name="k"/> (between it and the line above) would be.</summary>
    private static CutKind Cut(MarkdownLines lines, int k)
    {
        int above = lines.TableOf[k - 1], below = lines.TableOf[k];
        if (above >= 0 && above == below)
        {
            // Inside one table: only before a row, never before the first one (the excerpt above would hold only the header).
            return lines.Roles[k] == LineRole.Row && k > lines.Tables[below].BodyStart ? CutKind.TableRow : CutKind.Forbidden;
        }

        if (above != below)
        {
            return CutKind.TableEdge;
        }

        if (lines.IsBlank(k - 1) || lines.IsBlank(k))
        {
            return CutKind.Structural;
        }

        return CutKind.Text;
    }

    private enum CutKind
    {
        Forbidden,
        TableEdge,
        Structural,
        TableRow,
        Text
    }

    private enum LineRole
    {
        Text,
        Header,

        /// <summary>A line starting a table row, where a cut may fall.</summary>
        Row,

        /// <summary>A line starting an HTML row that a <c>rowspan</c> above reaches into: a row, but no place to cut.</summary>
        CoveredRow,

        /// <summary>Any other line of a table's body: an HTML row's continuation, the closing tag.</summary>
        InsideRow
    }

    private sealed record Table(int HeaderStart, int BodyStart, int End);

    /// <summary>The Markdown's lines and the tables recognised in them.</summary>
    private sealed class MarkdownLines
    {
        private readonly string _markdown;

        private MarkdownLines(string markdown)
        {
            _markdown = markdown;
            var start = 0;
            while (true)
            {
                var newline = markdown.IndexOf('\n', start);
                Starts.Add(start);
                if (newline < 0)
                {
                    Lengths.Add(markdown.Length - start);
                    break;
                }

                Lengths.Add(newline - start);
                start = newline + 1;
            }

            TableOf = Enumerable.Repeat(-1, Count).ToArray();
            Roles = new LineRole[Count];
        }

        public List<int> Starts { get; } = new();

        public List<int> Lengths { get; } = new();

        public int[] TableOf { get; }

        public LineRole[] Roles { get; }

        public List<Table> Tables { get; } = new();

        public int Count => Starts.Count;

        public static MarkdownLines Of(string markdown)
        {
            var lines = new MarkdownLines(markdown);
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines.IsBlank(i))
                {
                    continue;
                }

                var line = lines.Line(i).TrimStart();
                if (line.StartsWith("<table", StringComparison.OrdinalIgnoreCase))
                {
                    i = lines.ReadHtmlTable(i) - 1;
                }
                else if (line.Contains('|') && i + 1 < lines.Count && lines.IsDelimiterRow(i + 1))
                {
                    i = lines.ReadPipeTable(i) - 1;
                }
            }

            return lines;
        }

        public ReadOnlySpan<char> Line(int i) => _markdown.AsSpan(Starts[i], Lengths[i]);

        public bool IsBlank(int i) => Line(i).IsWhiteSpace();

        public int CharsBetween(int start, int end) => (end < Count ? Starts[end] : _markdown.Length) - Starts[start];

        /// <summary>The line holding <paramref name="offset"/>.</summary>
        public int LineAt(int offset)
        {
            var index = Starts.BinarySearch(offset);
            return index >= 0 ? index : ~index - 1;
        }

        /// <summary>A GFM delimiter row: pipes, dashes, colons and blanks only, with at least one pipe and one dash.</summary>
        private bool IsDelimiterRow(int i)
        {
            var line = Line(i).Trim();
            return line.Contains('|') && line.Contains('-') && line.IndexOfAnyExcept("|-: \t") < 0;
        }

        /// <summary>Header line, delimiter row, then every following non-blank line with a pipe. Returns the line after it.</summary>
        private int ReadPipeTable(int header)
        {
            var end = header + 2;
            while (end < Count && !IsBlank(end) && Line(end).Contains('|'))
            {
                end++;
            }

            Mark(header, header + 2, end, row => LineRole.Row);
            return end;
        }

        /// <summary>
        /// From the opening tag to the line holding the closing tag (the rest of the document when there is none). A row is a
        /// line opening a <c>&lt;tr&gt;</c> outside any open row; the leading rows with <c>&lt;th&gt;</c> cells are the header;
        /// a row a <c>rowspan</c> above reaches into is not a place to cut. Returns the line after the table.
        /// </summary>
        private int ReadHtmlTable(int open)
        {
            var roles = new List<LineRole> { LineRole.Header };
            var depth = RowOpen.Matches(Line(open).ToString()).Count - RowClose.Matches(Line(open).ToString()).Count;
            var row = -1;
            var coveredUntil = 0;
            var inHeader = true;
            var end = Count;
            if (Line(open).Contains("</table", StringComparison.OrdinalIgnoreCase))
            {
                end = open + 1;
            }

            for (var i = open + 1; i < end; i++)
            {
                var text = Line(i).ToString();
                if (depth <= 0 && text.TrimStart().StartsWith("<tr", StringComparison.OrdinalIgnoreCase))
                {
                    row++;
                    if (inHeader && text.Contains("<th", StringComparison.OrdinalIgnoreCase))
                    {
                        roles.Add(LineRole.Header);
                    }
                    else
                    {
                        inHeader = false;
                        roles.Add(row >= coveredUntil ? LineRole.Row : LineRole.CoveredRow);
                    }
                }
                else
                {
                    roles.Add(inHeader ? LineRole.Header : LineRole.InsideRow);
                }

                foreach (Match span in RowSpan.Matches(text))
                {
                    coveredUntil = Math.Max(coveredUntil, row + int.Parse(span.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                }

                depth = Math.Max(0, depth + RowOpen.Matches(text).Count - RowClose.Matches(text).Count);
                if (text.Contains("</table", StringComparison.OrdinalIgnoreCase))
                {
                    end = i + 1;
                }
            }

            var bodyStart = open + roles.FindIndex(role => role != LineRole.Header);
            if (bodyStart < open)
            {
                bodyStart = end;
            }

            Mark(open, bodyStart, end, line => roles[line - open]);
            return end;
        }

        private void Mark(int headerStart, int bodyStart, int end, Func<int, LineRole> bodyRole)
        {
            var table = Tables.Count;
            Tables.Add(new Table(headerStart, bodyStart, end));
            for (var i = headerStart; i < end; i++)
            {
                TableOf[i] = table;
                Roles[i] = i < bodyStart ? LineRole.Header : bodyRole(i);
            }
        }
    }
}

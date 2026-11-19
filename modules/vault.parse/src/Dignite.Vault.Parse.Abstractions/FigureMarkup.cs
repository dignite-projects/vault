using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Dignite.Vault.Parse;

/// <summary>
/// The Markdown of a figure: an <see cref="Microsoft.Extensions.Documents.ExtractionDocumentImageElement"/> written
/// the way Azure Document Intelligence's layout model writes a figure in its Markdown output (a <c>&lt;figure&gt;</c>
/// block, the caption in a <c>&lt;figcaption&gt;</c>, the text read from the figure inside, see
/// https://learn.microsoft.com/azure/ai-services/document-intelligence/concept/markdown-elements#figure). The
/// element's <c>Text</c> (the OCR or transcription of the image) is the figure's content and its
/// <c>AlternativeText</c> (caption or description) is the <c>&lt;figcaption&gt;</c>:
/// <code>
/// &lt;figure id="fig-1"&gt;
/// &lt;figcaption&gt;図1 領収書&lt;/figcaption&gt;
///
/// (the transcription, as Markdown)
///
/// &lt;/figure&gt;
/// </code>
/// <para>
/// The blank lines make the transcription Markdown again: CommonMark ends the <c>&lt;figure&gt;</c> HTML block at the
/// first blank line, renders what follows as Markdown (a table stays a table) and reads <c>&lt;/figure&gt;</c> as an HTML
/// block of its own. The caption is HTML-encoded, on one line, because it sits inside the HTML block.
/// </para>
/// <para>
/// The renderer numbers the figures of a document (<c>id="fig-N"</c>, the HTML <c>id</c> attribute) so that each
/// opening line is unique: sub-document segmentation finds a span by its first line, and two identical
/// <c>&lt;figure&gt;</c> lines would let a cut land on the wrong figure. Azure's own output carries no id; both forms
/// are read.
/// </para>
/// <para>
/// Each tag is a line of its own, and the pipeline reads them as structure: the parse reads a figure back into an
/// image element (<see cref="Split"/>), classification takes <see cref="Contains"/> as the figure-bearing signal, and
/// sub-document segmentation cuts a figure span at its opening line (<see cref="OpensFigureAt"/>) and seeds the child
/// from <see cref="ExtractBodies"/> or <see cref="Strip"/>. A tag counts only where CommonMark would read it as one:
/// indented by at most three spaces and outside a fenced code block, so an HTML sample in a code block is never a
/// figure. A line of a document's own text that reads as a tag is escaped (<see cref="Escape"/>). The LLM reads the
/// Markdown after <c>PromptBoundary.WrapDocument</c>, which encodes <c>&lt;</c>, so it sees <c>&amp;lt;figure</c>.
/// </para>
/// </summary>
public static partial class FigureMarkup
{
    /// <summary>The line that opens a figure with no id (Azure Document Intelligence's form).</summary>
    public const string OpenTag = "<figure>";

    /// <summary>The line that closes a figure.</summary>
    public const string CloseTag = "</figure>";

    /// <summary>Opens the caption, on the line after the opening tag.</summary>
    public const string CaptionOpenTag = "<figcaption>";

    /// <summary>Closes the caption.</summary>
    public const string CaptionCloseTag = "</figcaption>";

    /// <summary>The id the renderer gives the <paramref name="ordinal"/>-th figure of a document (1-based).</summary>
    public static string Id(int ordinal) => "fig-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The opening line of a figure: <see cref="OpenTag"/>, or <c>&lt;figure id="…"&gt;</c>.</summary>
    public static string OpenLine(string? id)
    {
        if (id is null)
        {
            return OpenTag;
        }

        var line = "<figure id=\"" + id + "\">";
        return OpenWithId().IsMatch(line) ? line : throw new ArgumentException("A figure id is letters, digits and . _ : -.", nameof(id));
    }

    /// <summary>
    /// A figure holding <paramref name="text"/> (written as it is, but for <see cref="Escape"/>, and with a fenced code
    /// block left open at its end closed) under <paramref name="caption"/>, either of which may be empty.
    /// </summary>
    public static string Write(string? text, string? caption, string? id = null)
    {
        var builder = new StringBuilder(OpenLine(id));
        var captionText = CollapseWhitespace(caption);
        if (captionText.Length > 0)
        {
            builder.Append('\n').Append(CaptionOpenTag).Append(WebUtility.HtmlEncode(captionText)).Append(CaptionCloseTag);
        }

        var body = text?.Trim('\n');
        if (string.IsNullOrWhiteSpace(body))
        {
            builder.Append('\n');
        }
        else
        {
            // A fence the transcription leaves open would hold the closing tag as code, in a renderer and here alike.
            var lines = body.Split('\n');
            var open = FenceOpenAtEnd(lines);
            builder.Append("\n\n").Append(Escape(body));
            if (open is not null)
            {
                builder.Append('\n').Append(open);
            }

            builder.Append("\n\n");
        }

        return builder.Append(CloseTag).ToString();
    }

    /// <summary>
    /// Escapes every line of <paramref name="text"/> that would read as a figure's opening or closing tag with a
    /// backslash before its <c>&lt;</c>: Markdown shows <c>\&lt;figure&gt;</c> as the same text, and it is no longer a
    /// tag line. Lines in a fenced code block are code and stay as written. The renderer passes the text it writes
    /// through this, and <see cref="Write"/> a figure's content, so no line of a document's own text, or of a picture's,
    /// opens or closes a figure no image made.
    /// </summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("figure", StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var lines = text.Split('\n');
        var code = CodeLines(lines);
        var escaped = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!code[i] && (IsOpenLine(lines[i]) || IsCloseLine(lines[i])))
            {
                lines[i] = lines[i].Insert(lines[i].IndexOf('<'), "\\");
                escaped = true;
            }
        }

        return escaped ? string.Join("\n", lines) : text;
    }

    /// <summary>
    /// The runs of <paramref name="markdown"/> between figures, and each figure with its caption. An opening line that
    /// no closing line follows is text.
    /// </summary>
    public static IEnumerable<FigureMarkupPart> Split(string markdown)
    {
        var lines = markdown.Split('\n');
        var code = CodeLines(lines);
        var text = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!code[i] && IsOpenLine(lines[i]))
            {
                var close = CloseOf(lines, code, i);
                if (close > 0)
                {
                    if (text.Count > 0)
                    {
                        yield return new FigureMarkupPart(false, string.Join('\n', text), null);
                        text.Clear();
                    }

                    var bodyStart = ReadCaption(lines, i + 1, close, out var caption);
                    yield return new FigureMarkupPart(true, string.Join('\n', lines[bodyStart..close]).Trim('\n'), caption);
                    i = close;
                    continue;
                }
            }

            text.Add(lines[i]);
        }

        if (text.Count > 0)
        {
            yield return new FigureMarkupPart(false, string.Join('\n', text), null);
        }
    }

    /// <summary>
    /// Whether <paramref name="markdown"/> opens a figure anywhere: the figure-bearing signal. Content that only
    /// mentions the tag inside a longer line, or in a code block, is not a figure.
    /// </summary>
    public static bool Contains(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || markdown.IndexOf("<figure", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        var lines = markdown.Split('\n');
        var code = CodeLines(lines);
        return lines.Where((line, i) => !code[i] && IsOpenLine(line)).Any();
    }

    /// <summary>
    /// Whether the line of <paramref name="markdown"/> starting at <paramref name="lineStart"/> opens a figure: an
    /// opening tag line outside a fenced code block.
    /// </summary>
    public static bool OpensFigureAt(string markdown, int lineStart)
    {
        var end = markdown.IndexOf('\n', lineStart);
        var line = end < 0 ? markdown[lineStart..] : markdown[lineStart..end];
        if (!IsOpenLine(line))
        {
            return false;
        }

        var before = markdown[..lineStart].Split('\n');
        return FenceOpenAtEnd(before[..^1]) is null;
    }

    /// <summary>
    /// <paramref name="markdown"/> without its figure tags: the tag lines go, a caption stays as a line of plain text, and
    /// each figure's content stays where it was. Seeds a text sub-document whose span held a figure.
    /// </summary>
    public static string Strip(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || markdown.IndexOf("figure", StringComparison.Ordinal) < 0)
        {
            return markdown ?? string.Empty;
        }

        var lines = markdown.Split('\n');
        var code = CodeLines(lines);
        var kept = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (code[i] || !(IsOpenLine(lines[i]) || IsCloseLine(lines[i])))
            {
                kept.Add(lines[i]);
                continue;
            }

            if (IsCloseLine(lines[i]))
            {
                continue;
            }

            var close = CloseOf(lines, code, i);
            var next = ReadCaption(lines, i + 1, close > 0 ? close : lines.Length, out var caption);
            if (caption is not null)
            {
                kept.Add(caption);
            }

            i = next - 1;
        }

        return string.Join("\n", kept);
    }

    /// <summary>
    /// The content of each figure in <paramref name="markdown"/>, without the caption, the figures apart by a blank line,
    /// and nothing of the text around them. Seeds a figure sub-document with the transcription alone, even when its span
    /// took in some of the parent's text. A figure whose closing line is missing runs to the end, so no content is
    /// dropped.
    /// </summary>
    public static string ExtractBodies(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Split('\n');
        var code = CodeLines(lines);
        var bodies = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (code[i] || !IsOpenLine(lines[i]))
            {
                continue;
            }

            var close = CloseOf(lines, code, i);
            var end = close > 0 ? close : lines.Length;
            var start = ReadCaption(lines, i + 1, end, out _);
            var body = string.Join('\n', lines[start..end]).Trim('\n');
            if (body.Length > 0)
            {
                bodies.Add(body);
            }

            i = end;
        }

        return string.Join("\n\n", bodies);
    }

    /// <summary>
    /// Whether the line opens a figure (<see cref="OpenTag"/>, with or without an id), indented by at most three spaces
    /// as CommonMark requires of an HTML block. Says nothing of a code block around it.
    /// </summary>
    public static bool IsOpenLine(string? line)
    {
        if (line is null || Indent(line) > 3)
        {
            return false;
        }

        var trimmed = line.Trim();
        return trimmed == OpenTag || OpenWithId().IsMatch(trimmed);
    }

    /// <summary>Whether the line is <see cref="CloseTag"/>, indented by at most three spaces.</summary>
    public static bool IsCloseLine(string? line) => line is not null && Indent(line) <= 3 && line.Trim() == CloseTag;

    /// <summary>The first closing line after <paramref name="open"/> outside a code block, or -1.</summary>
    private static int CloseOf(string[] lines, bool[] code, int open)
    {
        for (var j = open + 1; j < lines.Length; j++)
        {
            if (!code[j] && IsCloseLine(lines[j]))
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the caption that opens a figure at <paramref name="from"/>: lines from a <see cref="CaptionOpenTag"/> to the
    /// first that ends with <see cref="CaptionCloseTag"/>, before <paramref name="end"/>. Returns the line after it, or
    /// <paramref name="from"/> (and a null caption) when the figure has none.
    /// </summary>
    private static int ReadCaption(string[] lines, int from, int end, out string? caption)
    {
        caption = null;
        if (from >= end || !lines[from].TrimStart().StartsWith(CaptionOpenTag, StringComparison.Ordinal))
        {
            return from;
        }

        for (var last = from; last < end; last++)
        {
            if (!lines[last].TrimEnd().EndsWith(CaptionCloseTag, StringComparison.Ordinal))
            {
                continue;
            }

            var joined = string.Join(' ', lines[from..(last + 1)]).Trim();
            var inner = joined[CaptionOpenTag.Length..^CaptionCloseTag.Length];
            caption = CollapseWhitespace(WebUtility.HtmlDecode(inner));
            return last + 1;
        }

        return from;
    }

    /// <summary>
    /// Which lines are inside a fenced code block, fences included (CommonMark: three or more backticks or tildes,
    /// indented at most three spaces; closed by a run of the same character at least as long). A block left open runs to
    /// the end. Shared with <see cref="PageMarkup"/>, which reads its comments by the same rule.
    /// </summary>
    internal static bool[] CodeLines(string[] lines)
    {
        var code = new bool[lines.Length];
        var fence = '\0';
        var fenceLength = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (fence != '\0')
            {
                code[i] = true;
                if (Fence(lines[i], out var c, out var n, out var rest) && c == fence && n >= fenceLength && rest.Trim().Length == 0)
                {
                    fence = '\0';
                }

                continue;
            }

            if (Fence(lines[i], out var opener, out var length, out var info) && !(opener == '`' && info.Contains('`')))
            {
                fence = opener;
                fenceLength = length;
                code[i] = true;
            }
        }

        return code;
    }

    /// <summary>The fence that closes a code block <paramref name="lines"/> leave open, or null when none is open.</summary>
    private static string? FenceOpenAtEnd(string[] lines)
    {
        var fence = '\0';
        var fenceLength = 0;
        foreach (var line in lines)
        {
            if (fence != '\0')
            {
                if (Fence(line, out var c, out var n, out var rest) && c == fence && n >= fenceLength && rest.Trim().Length == 0)
                {
                    fence = '\0';
                }

                continue;
            }

            if (Fence(line, out var opener, out var length, out var info) && !(opener == '`' && info.Contains('`')))
            {
                fence = opener;
                fenceLength = length;
            }
        }

        return fence == '\0' ? null : new string(fence, fenceLength);
    }

    /// <summary>Whether <paramref name="line"/> is a code fence: its character, its length and what follows.</summary>
    private static bool Fence(string line, out char character, out int length, out string rest)
    {
        character = '\0';
        length = 0;
        rest = string.Empty;
        var indent = Indent(line);
        if (indent > 3 || indent >= line.Length || (line[indent] != '`' && line[indent] != '~'))
        {
            return false;
        }

        character = line[indent];
        var end = indent;
        while (end < line.Length && line[end] == character)
        {
            end++;
        }

        length = end - indent;
        rest = line[end..];
        return length >= 3;
    }

    /// <summary>The columns of leading whitespace, a tab counting as reaching the next multiple of four.</summary>
    private static int Indent(string line)
    {
        var columns = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                columns++;
            }
            else if (c == '\t')
            {
                columns += 4 - columns % 4;
            }
            else
            {
                break;
            }
        }

        return columns;
    }

    private static string CollapseWhitespace(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^<figure id=""[A-Za-z0-9][A-Za-z0-9._:-]{0,63}"">$")]
    private static partial Regex OpenWithId();
}

/// <summary>
/// A part of Markdown that <see cref="FigureMarkup.Split"/> found: a run of text, or a figure, whose <see cref="Text"/>
/// is its content and <see cref="Caption"/> its caption.
/// </summary>
public sealed record FigureMarkupPart(bool IsFigure, string Text, string? Caption);

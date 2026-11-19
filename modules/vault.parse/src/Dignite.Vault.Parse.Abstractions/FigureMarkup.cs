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
/// &lt;figure&gt;
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
/// Each tag is a line of its own, and the pipeline reads them as structure: the parse reads a figure back into an
/// image element (<see cref="Split"/>), classification takes <see cref="Contains"/> as the figure-bearing signal, and
/// sub-document segmentation cuts a figure span at its <c>&lt;figure&gt;</c> line and seeds the child from
/// <see cref="ExtractBodies"/> or <see cref="Strip"/>. So a line of a document's own text that reads as one of the two
/// tags is escaped (<see cref="Escape"/>), and only the whole trimmed line counts. The LLM reads the Markdown after
/// <c>PromptBoundary.WrapDocument</c>, which encodes <c>&lt;</c>, so it sees <c>&amp;lt;figure&gt;</c>.
/// </para>
/// </summary>
public static partial class FigureMarkup
{
    /// <summary>The line that opens a figure.</summary>
    public const string OpenTag = "<figure>";

    /// <summary>The line that closes a figure.</summary>
    public const string CloseTag = "</figure>";

    /// <summary>Opens the caption, on the line after <see cref="OpenTag"/>.</summary>
    public const string CaptionOpenTag = "<figcaption>";

    /// <summary>Closes the caption.</summary>
    public const string CaptionCloseTag = "</figcaption>";

    /// <summary>
    /// A figure holding <paramref name="text"/> (written as it is, but for <see cref="Escape"/>) under
    /// <paramref name="caption"/>, either of which may be empty.
    /// </summary>
    public static string Write(string? text, string? caption)
    {
        var builder = new StringBuilder(OpenTag);
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
            builder.Append("\n\n").Append(Escape(body)).Append("\n\n");
        }

        return builder.Append(CloseTag).ToString();
    }

    /// <summary>
    /// Escapes every line of <paramref name="text"/> that would read as <see cref="OpenTag"/> or <see cref="CloseTag"/>
    /// with a backslash before its <c>&lt;</c>: Markdown shows <c>\&lt;figure&gt;</c> as the same text, and it is no
    /// longer a tag line. The renderer passes the text it writes through this, and <see cref="Write"/> a figure's
    /// content, so no line of a document's own text, or of a picture's, opens or closes a figure no image made.
    /// </summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("figure>", StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var lines = text.Split('\n');
        var escaped = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsOpenLine(lines[i]) || IsCloseLine(lines[i]))
            {
                lines[i] = lines[i].Insert(lines[i].IndexOf('<'), "\\");
                escaped = true;
            }
        }

        return escaped ? string.Join("\n", lines) : text;
    }

    /// <summary>
    /// The runs of <paramref name="markdown"/> between figures, and each figure with its caption. A
    /// <see cref="OpenTag"/> line that no <see cref="CloseTag"/> line follows is text.
    /// </summary>
    public static IEnumerable<FigureMarkupPart> Split(string markdown)
    {
        var lines = markdown.Split('\n');
        var text = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsOpenLine(lines[i]))
            {
                var close = Array.FindIndex(lines, i + 1, line => IsCloseLine(line));
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
    /// Whether <paramref name="markdown"/> has a <see cref="OpenTag"/> line: the figure-bearing signal. Content that only
    /// mentions the tag inside a longer line is not a figure.
    /// </summary>
    public static bool Contains(string? markdown)
        => !string.IsNullOrEmpty(markdown)
            && markdown.IndexOf(OpenTag, StringComparison.Ordinal) >= 0
            && markdown.Split('\n').Any(IsOpenLine);

    /// <summary>
    /// <paramref name="markdown"/> without its figure tags: the tag lines go, a caption stays as a line of plain text, and
    /// each figure's content stays where it was. Seeds a text sub-document whose span held a figure.
    /// </summary>
    public static string Strip(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || markdown.IndexOf("figure>", StringComparison.Ordinal) < 0)
        {
            return markdown ?? string.Empty;
        }

        var lines = markdown.Split('\n');
        var kept = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsCloseLine(lines[i]))
            {
                continue;
            }

            if (!IsOpenLine(lines[i]))
            {
                kept.Add(lines[i]);
                continue;
            }

            var close = Array.FindIndex(lines, i + 1, line => IsCloseLine(line));
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
    /// took in some of the parent's text. A figure whose <see cref="CloseTag"/> line is missing runs to the end, so no
    /// content is dropped.
    /// </summary>
    public static string ExtractBodies(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Split('\n');
        var bodies = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsOpenLine(lines[i]))
            {
                continue;
            }

            var close = Array.FindIndex(lines, i + 1, line => IsCloseLine(line));
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

    /// <summary>Whether the trimmed line is <see cref="OpenTag"/>.</summary>
    public static bool IsOpenLine(string? line) => line is not null && line.Trim() == OpenTag;

    /// <summary>Whether the trimmed line is <see cref="CloseTag"/>.</summary>
    public static bool IsCloseLine(string? line) => line is not null && line.Trim() == CloseTag;

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

    private static string CollapseWhitespace(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// A part of Markdown that <see cref="FigureMarkup.Split"/> found: a run of text, or a figure, whose <see cref="Text"/>
/// is its content and <see cref="Caption"/> its caption.
/// </summary>
public sealed record FigureMarkupPart(bool IsFigure, string Text, string? Caption);

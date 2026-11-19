using System.IO;
using System.Threading;
using Dignite.Vault.Parse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using NSubstitute;
using NSubstitute.Core;

namespace Dignite.Vault.Extract.Documents.Pipelines.Parse;

/// <summary>
/// What the Parse router hands the parse job, built from Markdown without an engine: one section holding the Markdown as
/// written, which <see cref="ExtractionDocumentMarkdown"/> renders back unchanged. A figure span
/// (<see cref="ImageOcrMarkup"/>) becomes an image element, as every engine emits a figure, so its markers render as
/// markers rather than as text that reads like one.
/// </summary>
public static class ExtractedDocuments
{
    public const string DefaultProvider = "ElBruno.MarkItDotNet";

    public static ExtractionDocument Of(
        string markdown,
        string? provider = DefaultProvider,
        string? language = null,
        string? incompleteReason = null)
    {
        var document = new ExtractionDocument("test");
        var section = new ExtractionDocumentSection();
        if (!ImageOcrMarkup.Contains(markdown))
        {
            if (markdown.Length > 0)
            {
                section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(markdown), ParseElementRoles.Markdown));
            }

            document.Sections.Add(section);
            document.SetProvider(provider).SetLanguage(language);
            return incompleteReason is null ? document : document.MarkIncomplete(incompleteReason);
        }

        var lines = markdown.Split('\n');
        var text = new System.Collections.Generic.List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var close = ImageOcrMarkup.IsOpenLine(lines[i]) ? System.Array.FindIndex(lines, i + 1, ImageOcrMarkup.IsCloseLine) : -1;
            if (close < 0)
            {
                text.Add(lines[i]);
                continue;
            }

            AddText(section, text);
            var image = new ExtractionDocumentImageElement(text: string.Join('\n', lines[(i + 1)..close]));
            if (ImageOcrMarkup.TryParsePage(lines[i]) is { } pageNumber)
            {
                var page = new ExtractionDocumentSourcePage("page-" + pageNumber, pageNumber);
                document.SourcePages.Add(page);
                image.Sources.Add(new ExtractionDocumentSourceReference(page));
            }

            section.Elements.Add(image);
            i = close;
        }

        AddText(section, text);

        document.Sections.Add(section);
        document.SetProvider(provider).SetLanguage(language);
        return incompleteReason is null ? document : document.MarkIncomplete(incompleteReason);
    }

    private static void AddText(ExtractionDocumentSection section, System.Collections.Generic.List<string> lines)
    {
        var text = string.Join('\n', lines).Trim('\n');
        if (text.Length > 0)
        {
            section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(text), ParseElementRoles.Markdown));
        }

        lines.Clear();
    }

    /// <summary>Configures <paramref name="client"/> (a substitute) to answer every extraction through <paramref name="answer"/>.</summary>
    public static void Answers(this DocumentExtractionClient client, System.Func<CallInfo, ExtractionDocument> answer)
        => client.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => answer(call));

    /// <summary>Configures <paramref name="client"/> (a substitute) to answer every extraction with <paramref name="markdown"/>.</summary>
    public static void Answers(this DocumentExtractionClient client, string markdown, string? provider = DefaultProvider, string? language = null)
        => client.Answers(_ => Of(markdown, provider, language));
}

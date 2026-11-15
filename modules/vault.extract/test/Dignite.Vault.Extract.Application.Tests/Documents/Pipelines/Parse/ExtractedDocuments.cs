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
/// written, which <see cref="ExtractionDocumentMarkdown"/> renders back unchanged.
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
        if (markdown.Length > 0)
        {
            section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(markdown), ParseElementRoles.Markdown));
        }

        document.Sections.Add(section);
        document.SetProvider(provider).SetLanguage(language);
        return incompleteReason is null ? document : document.MarkIncomplete(incompleteReason);
    }

    /// <summary>Configures <paramref name="client"/> (a substitute) to answer every extraction through <paramref name="answer"/>.</summary>
    public static void Answers(this DocumentExtractionClient client, System.Func<CallInfo, ExtractionDocument> answer)
        => client.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => answer(call));

    /// <summary>Configures <paramref name="client"/> (a substitute) to answer every extraction with <paramref name="markdown"/>.</summary>
    public static void Answers(this DocumentExtractionClient client, string markdown, string? provider = DefaultProvider, string? language = null)
        => client.Answers(_ => Of(markdown, provider, language));
}

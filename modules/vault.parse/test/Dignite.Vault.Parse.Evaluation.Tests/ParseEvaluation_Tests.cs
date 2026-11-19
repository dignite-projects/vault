using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Parse.Evaluation.Scoring;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Dignite.Vault.Parse.Evaluation;

/// <summary>
/// Parses every corpus document with the host's engines and scores it against its ground truth. The digital
/// documents are deterministic, so each must hold its <c>Reports/baseline.json</c> scores within
/// <see cref="ParseEvaluationFixture.Tolerance"/>; the documents that need OCR depend on a vision model and are only
/// reported.
/// </summary>
public class ParseEvaluation_Tests(ParseEvaluationFixture fixture, ITestOutputHelper output) : IClassFixture<ParseEvaluationFixture>
{
    public static TheoryData<string> DigitalDocuments() => [.. ParseEvaluationFixture.Corpus.Where(d => !d.Ocr).Select(d => d.Id)];

    public static TheoryData<string> OcrDocuments() => [.. ParseEvaluationFixture.Corpus.Where(d => d.Ocr).Select(d => d.Id)];

    [Theory]
    [MemberData(nameof(DigitalDocuments))]
    public async Task A_digital_document_holds_its_baseline(string id)
    {
        var outcome = await fixture.EvaluateAsync(ParseEvaluationFixture.Corpus.Single(d => d.Id == id));
        Write(outcome);
        if (ParseEvaluationFixture.WritingBaseline)
        {
            return;
        }

        fixture.Baseline.TryGetValue(id, out var baseline).ShouldBeTrue(
            $"'{id}' has no baseline. Run with VAULT_PARSE_EVAL_WRITE_BASELINE=1 and commit Reports/baseline.json.");

        var regressions = new List<string>();
        foreach (var (metric, expected) in baseline!)
        {
            if (!outcome.Scores.TryGetValue(metric, out var actual))
            {
                regressions.Add($"{metric}: {expected:0.000} -> none");
            }
            else if (actual < expected - ParseEvaluationFixture.Tolerance)
            {
                regressions.Add($"{metric}: {expected:0.000} -> {actual:0.000}");
            }
            else if (actual > expected + ParseEvaluationFixture.Tolerance)
            {
                output.WriteLine($"{metric} improved: {expected:0.000} -> {actual:0.000}. Rewrite the baseline to keep it.");
            }
        }

        regressions.ShouldBeEmpty($"'{id}' fell below its baseline:\n" + string.Join('\n', regressions));
    }

    /// <summary>
    /// Vault's Markdown is rendered from the document tree, and engines that answer in Markdown are read into it. That
    /// Markdown read back into a tree and rendered again must score the same: the reader and the renderer agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(DigitalDocuments))]
    public async Task A_parse_read_into_the_document_tree_and_rendered_back_scores_the_same(string id)
    {
        var document = ParseEvaluationFixture.Corpus.Single(d => d.Id == id);
        var markdown = ExtractionDocumentMarkdown.Render(await fixture.ParseAsync(document));

        var rendered = ExtractionDocumentMarkdown.Render(MarkdownDocumentReader.Read(markdown, id));

        var direct = ParseScorer.Score(document, markdown);
        var throughTree = ParseScorer.Score(document, rendered);
        output.WriteLine(rendered);
        throughTree.ShouldBe(direct);
    }

    /// <summary>The tree Vault stores: written as JSON and read back, it renders the same Markdown.</summary>
    [Theory]
    [MemberData(nameof(DigitalDocuments))]
    public async Task A_parse_survives_json(string id)
    {
        var tree = await fixture.ParseAsync(ParseEvaluationFixture.Corpus.Single(d => d.Id == id));

        var read = ExtractionDocumentJson.Deserialize(ExtractionDocumentJson.Serialize(tree));

        ExtractionDocumentMarkdown.Render(read).ShouldBe(ExtractionDocumentMarkdown.Render(tree));
        read.Sections.SelectMany(section => section.Elements).Sum(element => element.Sources.Count)
            .ShouldBe(tree.Sections.SelectMany(section => section.Elements).Sum(element => element.Sources.Count));
    }

    [VisionTheory]
    [MemberData(nameof(OcrDocuments))]
    public async Task An_ocr_document_is_scored(string id)
    {
        var outcome = await fixture.EvaluateAsync(ParseEvaluationFixture.Corpus.Single(d => d.Id == id));
        Write(outcome);
        outcome.Scores[ParseQualityEvaluator.TextSimilarity].ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The Markdown an AI client produced for each corpus document (<c>VAULT_PARSE_EVAL_CLIENT_MARKDOWN</c>, one
    /// <c>&lt;id&gt;.md</c> per document), scored the way Vault takes it in. Reported, never gated: it depends on the
    /// client and its model. A document the directory has no file for is passed over.
    /// </summary>
    [ClientMarkdownTheory]
    [MemberData(nameof(AllDocuments))]
    public async Task A_client_markdown_is_scored(string id)
    {
        var document = ParseEvaluationFixture.Corpus.Single(d => d.Id == id);
        if (ParseEvaluationFixture.ClientMarkdownPath(document) is not { } path)
        {
            output.WriteLine($"No client Markdown for '{id}'.");
            return;
        }

        var (outcome, pagesFound) = await fixture.EvaluateClientMarkdownAsync(document, path);
        Write(outcome);
        output.WriteLine($"  Pages marked: {pagesFound}" + (document.Pages is { } pages ? $" of {pages}" : string.Empty));
    }

    public static TheoryData<string> AllDocuments() => [.. ParseEvaluationFixture.Corpus.Select(d => d.Id)];

    private void Write(ParseOutcome outcome)
    {
        output.WriteLine($"{outcome.Document.Id} ({outcome.ProviderName}, complete: {outcome.IsComplete})");
        foreach (var (metric, value) in outcome.Scores)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {metric}: {value:0.000}"));
        }
    }
}

/// <summary>Runs only with <c>VAULT_PARSE_EVAL_CLIENT_MARKDOWN</c>, the directory of a client's Markdown.</summary>
public sealed class ClientMarkdownTheoryAttribute : TheoryAttribute
{
    public ClientMarkdownTheoryAttribute()
    {
        if (ParseEvaluationFixture.ClientMarkdownDirectory is null)
        {
            Skip = "Set VAULT_PARSE_EVAL_CLIENT_MARKDOWN to a directory of <id>.md files to score a client's Markdown.";
        }
    }
}

/// <summary>Runs only with a vision model (<c>VAULT_PARSE_EVAL_VISION_*</c>); each OCR page costs one model call.</summary>
public sealed class VisionTheoryAttribute : TheoryAttribute
{
    public VisionTheoryAttribute()
    {
        if (!VisionModel.IsConfigured)
        {
            Skip = "Set VAULT_PARSE_EVAL_VISION_ENDPOINT, _API_KEY and _MODEL to evaluate the documents that need OCR.";
        }
    }
}

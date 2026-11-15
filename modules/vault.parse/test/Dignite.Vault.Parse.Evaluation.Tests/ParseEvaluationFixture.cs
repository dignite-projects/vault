using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Parse.Evaluation;

/// <summary>The scores of one parse, by metric name; a metric the document has nothing to measure for is absent.</summary>
public sealed record ParseOutcome(CorpusDocument Document, string? ProviderName, bool IsComplete, IReadOnlyDictionary<string, double> Scores);

/// <summary>
/// One Parse application and one evaluation execution for the whole corpus. Every result is stored by
/// Microsoft.Extensions.AI.Evaluation's disk-based reporting (under <c>VAULT_PARSE_EVAL_RESULTS</c>, or the temp
/// directory), so <c>dotnet aieval report</c> renders it. With <c>VAULT_PARSE_EVAL_WRITE_BASELINE=1</c> the digital
/// documents' scores replace <c>Reports/baseline.json</c> in the source tree; with <c>VAULT_PARSE_EVAL_REPORT</c> set
/// to a file path, a Markdown table of every document parsed is written there.
/// </summary>
public sealed class ParseEvaluationFixture : IAsyncLifetime
{
    public const double Tolerance = 0.01;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, ParseOutcome> _outcomes = new();
    private IAbpApplicationWithInternalServiceProvider _application = default!;
    private ReportingConfiguration _reporting = default!;

    public static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "Corpus");

    public static bool WritingBaseline => Environment.GetEnvironmentVariable("VAULT_PARSE_EVAL_WRITE_BASELINE") is "1" or "true";

    public static IReadOnlyList<CorpusDocument> Corpus { get; } = CorpusDocument.LoadAll(CorpusDirectory);

    /// <summary>The scores each digital document held when the baseline was last written.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Baseline { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<string, double>>();

    public async Task InitializeAsync()
    {
        _application = await AbpApplicationFactory.CreateAsync<ParseEvaluationTestModule>(options => options.UseAutofac());
        await _application.InitializeAsync();

        _reporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: Environment.GetEnvironmentVariable("VAULT_PARSE_EVAL_RESULTS") ?? Path.Combine(Path.GetTempPath(), "vault-parse-eval"),
            evaluators: [new ParseQualityEvaluator()],
            enableResponseCaching: false,
            tags: [VisionModel.ModelId is { Length: > 0 } model ? $"vision:{model}" : "vision:none"]);

        var baselinePath = Path.Combine(AppContext.BaseDirectory, "Reports", "baseline.json");
        if (File.Exists(baselinePath))
        {
            Baseline = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(await File.ReadAllTextAsync(baselinePath))!
                .ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, double>)p.Value);
        }
    }

    /// <summary>The document parsed by the host's engines, the way Extract's parse job asks for it.</summary>
    public async Task<ExtractionDocument> ParseAsync(CorpusDocument document)
    {
        using var scope = _application.ServiceProvider.CreateScope();
        await using var stream = File.OpenRead(document.Path);
        return await scope.ServiceProvider.GetRequiredService<DocumentExtractionClient>().ExtractAsync(
            stream, document.Id, document.MediaType, ParseExtractionOptions.Create(document.File));
    }

    public async Task<ParseOutcome> EvaluateAsync(CorpusDocument document)
    {
        var extracted = await ParseAsync(document);
        var markdown = ExtractionDocumentMarkdown.Render(extracted);

        EvaluationResult evaluation;
        await using (var run = await _reporting.CreateScenarioRunAsync($"Parse.{document.Id}"))
        {
            evaluation = await run.EvaluateAsync(
                [new ChatMessage(ChatRole.User, document.File)],
                new ChatResponse(new ChatMessage(ChatRole.Assistant, markdown)),
                [new ParseTruthContext(document)]);
        }

        var scores = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in ParseQualityEvaluator.MetricNames)
        {
            if (evaluation.Get<NumericMetric>(name).Value is { } value)
            {
                scores[name] = Math.Round(value, 3);
            }
        }

        var outcome = new ParseOutcome(document, extracted.GetProvider(), extracted.IsComplete(), scores);
        _outcomes[document.Id] = outcome;
        return outcome;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (WritingBaseline)
            {
                // Merged, so a run over part of the corpus keeps the other documents' entries.
                var baseline = new SortedDictionary<string, IReadOnlyDictionary<string, double>>(Baseline.ToDictionary(), StringComparer.Ordinal);
                foreach (var outcome in _outcomes.Values.Where(o => !o.Document.Ocr))
                {
                    baseline[outcome.Document.Id] = outcome.Scores;
                }

                var path = Path.Combine(SourceDirectory(), "Reports", "baseline.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(baseline, JsonOptions) + "\n");
            }

            if (Environment.GetEnvironmentVariable("VAULT_PARSE_EVAL_REPORT") is { Length: > 0 } reportPath)
            {
                await File.WriteAllTextAsync(reportPath, Report(_outcomes.Values.OrderBy(o => o.Document.Id, StringComparer.Ordinal)));
            }
        }
        finally
        {
            await _application.ShutdownAsync();
            _application.Dispose();
        }
    }

    /// <summary>A Markdown table of the outcomes, one row per document and one column per metric.</summary>
    public static string Report(IEnumerable<ParseOutcome> outcomes)
    {
        var builder = new StringBuilder();
        builder.Append("| Document | Format | OCR | Provider | Complete");
        foreach (var name in ParseQualityEvaluator.MetricNames)
        {
            builder.Append(" | ").Append(name);
        }

        builder.AppendLine(" |");
        builder.Append("|---|---|---|---|---");
        builder.Append(string.Concat(Enumerable.Repeat("|---:", ParseQualityEvaluator.MetricNames.Count)));
        builder.AppendLine("|");
        foreach (var outcome in outcomes)
        {
            builder.Append(CultureInfo.InvariantCulture, $"| {outcome.Document.Id} | {outcome.Document.Format} | {(outcome.Document.Ocr ? "yes" : "no")} | {outcome.ProviderName} | {(outcome.IsComplete ? "yes" : "no")}");
            foreach (var name in ParseQualityEvaluator.MetricNames)
            {
                builder.Append(" | ").Append(outcome.Scores.TryGetValue(name, out var value) ? value.ToString("0.000", CultureInfo.InvariantCulture) : "–");
            }

            builder.AppendLine(" |");
        }

        return builder.ToString();
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

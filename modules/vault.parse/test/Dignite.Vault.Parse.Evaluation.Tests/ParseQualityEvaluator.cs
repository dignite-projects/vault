using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Parse.Evaluation.Scoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Dignite.Vault.Parse.Evaluation;

/// <summary>The ground truth of the document whose parse is evaluated.</summary>
public sealed class ParseTruthContext(CorpusDocument document) : EvaluationContext(ContextName, document.Id)
{
    public const string ContextName = "Parse ground truth";

    public CorpusDocument Document { get; } = document;
}

/// <summary>
/// Scores a parser's Markdown (the response text) against its <see cref="ParseTruthContext"/>, with
/// <see cref="ParseScorer"/>. Deterministic: no model is called, so no <see cref="ChatConfiguration"/> is needed.
/// </summary>
public sealed class ParseQualityEvaluator : IEvaluator
{
    public const string TextSimilarity = "Text similarity";
    public const string BlockRecall = "Block recall";
    public const string ReadingOrder = "Reading order";
    public const string TableCellRecall = "Table cell recall";
    public const string TableCellPrecision = "Table cell precision";
    public const string CellHeaderRecall = "Cell header recall";
    public const string HeadingRecall = "Heading recall";
    public const string HeadingLevelAgreement = "Heading level agreement";
    public const string NoiseFree = "Noise free";

    public static readonly IReadOnlyList<string> MetricNames =
        [TextSimilarity, BlockRecall, ReadingOrder, TableCellRecall, TableCellPrecision, CellHeaderRecall, HeadingRecall, HeadingLevelAgreement, NoiseFree];

    public IReadOnlyCollection<string> EvaluationMetricNames => MetricNames.ToList();

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var truth = additionalContext?.OfType<ParseTruthContext>().FirstOrDefault()
            ?? throw new ArgumentException($"A {nameof(ParseTruthContext)} is required.", nameof(additionalContext));
        var score = ParseScorer.Score(truth.Document, modelResponse.Text);
        var noiseReason = score.NoiseFree is null ? null : $"{score.NoiseLeaks} running header or page number occurrence(s) in the output.";
        return ValueTask.FromResult(new EvaluationResult(
            Metric(TextSimilarity, score.TextSimilarity),
            Metric(BlockRecall, score.BlockRecall),
            Metric(ReadingOrder, score.ReadingOrder),
            Metric(TableCellRecall, score.TableCellRecall),
            Metric(TableCellPrecision, score.TableCellPrecision),
            Metric(CellHeaderRecall, score.CellHeaderRecall),
            Metric(HeadingRecall, score.HeadingRecall),
            Metric(HeadingLevelAgreement, score.HeadingLevelAgreement),
            Metric(NoiseFree, score.NoiseFree, noiseReason)));
    }

    private static NumericMetric Metric(string name, double? value, string? reason = null)
    {
        var metric = new NumericMetric(name, value, reason ?? (value is null ? "Nothing to measure in this document." : null));
        if (value is { } v)
        {
            var rating = v switch
            {
                >= 0.98 => EvaluationRating.Exceptional,
                >= 0.9 => EvaluationRating.Good,
                >= 0.75 => EvaluationRating.Average,
                >= 0.5 => EvaluationRating.Poor,
                _ => EvaluationRating.Unacceptable
            };
            metric.Interpretation = new EvaluationMetricInterpretation(
                rating, failed: v < 0.5, reason: v.ToString("0.000", CultureInfo.InvariantCulture));
        }

        return metric;
    }
}

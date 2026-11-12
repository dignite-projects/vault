using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Dignite.Vault.Index.Evaluation;

/// <summary>Where one ranking placed one case's expected document and passage, from 1; <c>null</c> when outside the top 10.</summary>
public sealed record CaseResult(EvaluationCase Case, string Mode, int? DocumentRank, int? PassageRank);

/// <summary>
/// The metrics of the design: hit rate (Hit@k, the share of cases whose answer is in the top k) and mean reciprocal
/// rank over the top 10. Document level counts a hit from the expected document; passage level also needs the
/// expected heading, and covers only the cases that name one.
/// </summary>
public static class EvaluationReport
{
    public const int Depth = 10;

    private static readonly int[] Ks = [1, 3, 5, 10];

    public static string ToMarkdown(string title, IReadOnlyList<CaseResult> results, IEnumerable<string> notes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# " + title).AppendLine();
        foreach (var note in notes)
        {
            builder.AppendLine("- " + note);
        }

        builder.AppendLine();
        Table(builder, "Document level, all cases", results, r => r.DocumentRank, _ => true);
        Table(builder, "Passage level (written questions)", results, r => r.PassageRank, r => r.Case.Heading is not null);
        Table(builder, "Document level, field cases", results, r => r.DocumentRank, r => r.Case.Source == "field");
        Table(builder, "Document level, written questions in Japanese", results, r => r.DocumentRank, r => r.Case.Source == "question" && r.Case.Language == "ja");
        Table(builder, "Document level, written questions in Chinese and English", results, r => r.DocumentRank, r => r.Case.Language is "zh" or "en");

        builder.AppendLine("## Fused misses at passage level").AppendLine();
        builder.AppendLine("| Query | Expected | Document rank | Keyword | Vector |").AppendLine("|---|---|---|---|---|");
        var byCase = results.GroupBy(r => r.Case).ToList();
        foreach (var group in byCase)
        {
            var fused = group.Single(r => r.Mode == "fused");
            var miss = fused.Case.Heading is null ? fused.DocumentRank != 1 : fused.PassageRank != 1;
            if (!miss)
            {
                continue;
            }

            string Rank(string mode)
            {
                var r = group.Single(x => x.Mode == mode);
                var rank = r.Case.Heading is null ? r.DocumentRank : r.PassageRank;
                return rank?.ToString(CultureInfo.InvariantCulture) ?? "-";
            }

            builder.Append("| ").Append(fused.Case.Query.Replace("|", "\\|")).Append(" | ")
                .Append(fused.Case.Document).Append(fused.Case.Heading is null ? "" : " / " + fused.Case.Heading).Append(" | ")
                .Append(fused.DocumentRank?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(" | ")
                .Append(Rank("keyword")).Append(" | ").Append(Rank("vector")).AppendLine(" |");
        }

        return builder.ToString();
    }

    public static (int Count, double[] HitAtK, double Mrr) Measure(IEnumerable<int?> ranks)
    {
        var list = ranks.ToList();
        if (list.Count == 0)
        {
            return (0, new double[Ks.Length], 0);
        }

        var hits = Ks.Select(k => list.Count(r => r is { } rank && rank <= k) / (double)list.Count).ToArray();
        var mrr = list.Sum(r => r is { } rank && rank <= Depth ? 1d / rank : 0d) / list.Count;
        return (list.Count, hits, mrr);
    }

    private static void Table(StringBuilder builder, string heading, IReadOnlyList<CaseResult> results, Func<CaseResult, int?> rank, Func<CaseResult, bool> include)
    {
        builder.AppendLine("## " + heading).AppendLine();
        builder.AppendLine("| Ranking | Cases | Hit@1 | Hit@3 | Hit@5 | Hit@10 | MRR@10 |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (var mode in new[] { "fused", "keyword", "vector" })
        {
            var (count, hits, mrr) = Measure(results.Where(r => r.Mode == mode && include(r)).Select(rank));
            builder.Append("| ").Append(mode).Append(" | ").Append(count);
            foreach (var hit in hits)
            {
                builder.Append(" | ").Append(hit.ToString("0.00", CultureInfo.InvariantCulture));
            }

            builder.Append(" | ").Append(mrr.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        builder.AppendLine();
    }
}

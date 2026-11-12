using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.EntityFrameworkCore;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Reranking;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Runs;
using Dignite.Vault.Index.Search;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OpenAI;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Modularity;
using Xunit;
using Xunit.Abstractions;

namespace Dignite.Vault.Index.Evaluation;

/// <summary>
/// Runs only with a SQL Server (<c>VAULT_INDEX_TEST_SQLSERVER</c>) and an OpenAI-compatible embedding model
/// (<c>VAULT_INDEX_EVAL_EMBEDDING_ENDPOINT</c>, <c>_API_KEY</c>, <c>_MODEL</c>; <c>_DIMENSIONS</c> to send a dimension).
/// Each run costs one embedding call per chunk and per query.
/// </summary>
public sealed class EvaluationFactAttribute : FactAttribute
{
    public EvaluationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SqlServerTestDatabase.ServerConnectionString) || string.IsNullOrWhiteSpace(EvaluationModel.ModelId))
        {
            Skip = "Set VAULT_INDEX_TEST_SQLSERVER and VAULT_INDEX_EVAL_EMBEDDING_* to run the retrieval evaluation.";
        }
    }
}

public static class EvaluationModel
{
    public static string? Endpoint => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_EMBEDDING_ENDPOINT");

    public static string? ApiKey => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_EMBEDDING_API_KEY");

    public static string? ModelId => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_EMBEDDING_MODEL");

    public static int? Dimensions
        => int.TryParse(Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_EMBEDDING_DIMENSIONS"), out var d) ? d : null;

    /// <summary>Optional: a rerank model, on the embedding endpoint unless <c>_RERANK_ENDPOINT</c> / <c>_RERANK_API_KEY</c> say otherwise.</summary>
    public static string? RerankModelId => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_RERANK_MODEL");

    public static string? RerankEndpoint => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_RERANK_ENDPOINT");

    public static string? RerankApiKey => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_RERANK_API_KEY");

    /// <summary>Optional: a chat model for chunk enrichment, on the embedding endpoint.</summary>
    public static string? EnrichmentModelId => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_ENRICHMENT_MODEL");
}

/// <summary>
/// Real Index (application, SQL Server storage, ingestion job, search service) on the evaluation database, with the
/// configured embedding model; Extract and the job queue are doubles the runner fills from the corpus.
/// </summary>
[DependsOn(
    typeof(VaultIndexTestBaseModule),
    typeof(VaultIndexApplicationModule),
    typeof(VaultIndexEntityFrameworkCoreSqlServerModule))]
public class VaultIndexEvaluationTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentIntegrationService>());
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton<HtmlTablesSwitch>();
        context.Services.Replace(ServiceDescriptor.Transient<IndexChunker, EvaluationChunker>());

        if (string.IsNullOrWhiteSpace(SqlServerTestDatabase.ServerConnectionString) || string.IsNullOrWhiteSpace(EvaluationModel.ModelId))
        {
            return;
        }

        SqlServerTestDatabase.Evaluation.Configure(context);
        var client = new OpenAIClient(new ApiKeyCredential(EvaluationModel.ApiKey!), new OpenAIClientOptions { Endpoint = new Uri(EvaluationModel.Endpoint!) });
        // A run makes hundreds of calls on one key; a provider's occasional refusal is retried, not a failed run.
        context.Services.AddKeyedEmbeddingGenerator(
            VaultIndexConsts.EmbeddingGeneratorKey,
            _ => new RetryingEmbeddingGenerator(client.GetEmbeddingClient(EvaluationModel.ModelId).AsIEmbeddingGenerator(EvaluationModel.Dimensions)));

        if (!string.IsNullOrWhiteSpace(EvaluationModel.EnrichmentModelId))
        {
            context.Services.AddKeyedChatClient(
                VaultIndexConsts.EnrichmentChatClientKey,
                _ => client.GetChatClient(EvaluationModel.EnrichmentModelId).AsIChatClient());
        }

        if (!string.IsNullOrWhiteSpace(EvaluationModel.RerankModelId))
        {
            context.Services.AddVaultIndexHttpReranker(options =>
            {
                options.ModelId = EvaluationModel.RerankModelId!;
                options.Endpoint = EvaluationModel.RerankEndpoint ?? EvaluationModel.Endpoint!;
                options.ApiKey = EvaluationModel.RerankApiKey ?? EvaluationModel.ApiKey!;
            });
        }
    }
}

/// <summary>
/// The retrieval evaluation (design section 13): indexes the corpus through the real ingestion job, then ranks every
/// case several ways and reports hit rate and MRR for each: each leg alone, fusion with several keyword weights, the
/// same rankings reranked when a rerank model is configured, and the search service as configured (the egress
/// result). It measures and does not assert quality; the report goes to the test output and to
/// <c>VAULT_INDEX_EVAL_REPORT</c> or <c>index-evaluation.md</c> in the working directory.
/// </summary>
public class IndexEvaluation_Tests : VaultIndexTestBase<VaultIndexEvaluationTestModule>
{
    private readonly ITestOutputHelper _output;

    public IndexEvaluation_Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    [EvaluationFact]
    public async Task Measure_retrieval_on_the_evaluation_set()
    {
        var set = EvaluationSet.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation"));
        await ClearAsync();
        Arrange(set);

        var job = GetRequiredService<IndexDocumentJob>();
        var eventTime = new DateTime(2026, 11, 12, 0, 0, 0);
        foreach (var document in set.Documents)
        {
            eventTime = eventTime.AddMinutes(1);
            await job.ExecuteAsync(new IndexDocumentJobArgs
            {
                DocumentId = document.DocumentId,
                Kind = IndexDocumentJobKind.Sync,
                Trigger = IndexRunTrigger.Backfill,
                EventTime = eventTime
            });
        }

        var rows = await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexedDocument, Guid>>().GetListAsync());
        rows.Count(r => r.Status == IndexedDocumentStatus.Indexed).ShouldBe(set.Documents.Count);
        await WaitForFullTextAsync();

        var chunks = (await WithUnitOfWorkAsync(() => GetRequiredService<IChunkRepository>().GetListAsync())).ToDictionary(c => c.Id);
        var documentIds = set.Documents.ToDictionary(d => d.DocumentId, d => d.Id);
        var titles = set.Documents.ToDictionary(d => d.DocumentId, d => d.Title);
        var texts = chunks.Values.ToDictionary(c => c.Id, c => ChunkContext.Compose(titles.GetValueOrDefault(c.DocumentId), c.Text));
        var reranker = GetRequiredService<IndexRanker>().Reranker;
        var cases = set.Cases();
        var results = new List<CaseResult>();
        var rerankTime = TimeSpan.Zero;
        foreach (var evaluationCase in cases)
        {
            var (caseResults, elapsed) = await RankAsync(evaluationCase, chunks, texts, documentIds, reranker);
            results.AddRange(caseResults);
            rerankTime += elapsed;
        }

        var serving = await WithUnitOfWorkAsync(() => GetRequiredService<IndexGenerationManager>().FindServingAsync());
        var report = EvaluationReport.ToMarkdown(
            "Vault Index retrieval evaluation",
            results,
            [
                $"Evaluation set version {set.Version}: {set.Documents.Count} documents, {chunks.Count} chunks, {cases.Count} cases " +
                $"({cases.Count(c => c.Source == "field")} field, {cases.Count(c => c.Source == "question")} written; " +
                $"{cases.Count(c => c.Language == "zh")} Chinese, {cases.Count(c => c.Language == "en")} English)",
                $"Embedding model {serving!.ModelId} ({serving.Dimensions} dimensions); chunking {ResolvedIndexProfile.BuiltIn.Stamp}; RRF k = {IndexSearchConsts.RrfK}, {IndexSearchConsts.CandidatesPerLeg} candidates per leg",
                "Keyword leg: SQL Server full-text FREETEXTTABLE, Japanese word breaker (LCID 1041)",
                reranker is null
                    ? "No rerank model configured: the rerank variants are absent"
                    : $"Rerank model {reranker.ModelId}, top {RerankDepth} of each ranking; mean rerank call {rerankTime.TotalMilliseconds / Math.Max(1, cases.Count):0} ms (one call per case over the union of candidates)",
                $"`service` is IIndexSearchAppService as configured: keyword weight {Options.KeywordWeight}, vector weight {Options.VectorWeight}, reranker {(reranker is null ? "none" : reranker.ModelId)}",
                $"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
            ],
            "service");

        _output.WriteLine(report);
        var path = Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_REPORT") ?? Path.Combine(Directory.GetCurrentDirectory(), "index-evaluation.md");
        await File.WriteAllTextAsync(path, report);
    }

    /// <summary>
    /// The chunking strategies compared on the same cases. "HTML tables dropped" is how every strategy read a table
    /// written as HTML before the chunker rewrote it as Markdown: DataIngestion's reader leaves it out.
    /// </summary>
    private static readonly (string Name, ResolvedIndexProfile Profile, bool HtmlTables)[] Strategies =
    [
        ("heading 400", new(ChunkingStrategy.Heading, 400, 0), true),
        ("heading 400, HTML tables dropped", new(ChunkingStrategy.Heading, 400, 0), false),
        ("heading 200", new(ChunkingStrategy.Heading, 200, 0), true),
        ("heading 800", new(ChunkingStrategy.Heading, 800, 0), true),
        ("token 400/40", new(ChunkingStrategy.Token, 400, 40), true)
    ];

    /// <summary>
    /// Indexes the corpus once per chunking strategy (as the layer default profile) and ranks every case with the
    /// fused legs and with the search service as configured (reranked when a rerank model is set). The report goes to
    /// <c>VAULT_INDEX_EVAL_STRATEGY_REPORT</c> or <c>index-strategies.md</c>.
    /// </summary>
    [EvaluationFact]
    public async Task Compare_chunking_strategies()
    {
        var set = EvaluationSet.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation"));
        Arrange(set);
        var cases = set.Cases();
        var documentIds = set.Documents.ToDictionary(d => d.DocumentId, d => d.Id);
        var results = new List<CaseResult>();
        var notes = new List<string>
        {
            $"Evaluation set version {set.Version}: {set.Documents.Count} documents, {cases.Count} cases ({cases.Count(c => c.Source == "table")} answered by a table)",
            "Each strategy is the layer default profile; the corpus is indexed again for each. `fused` is RRF 1:1 of the two legs; `service` is IIndexSearchAppService as configured"
        };

        foreach (var (name, profile, htmlTables) in Strategies)
        {
            await ClearAsync();
            GetRequiredService<HtmlTablesSwitch>().Enabled = htmlTables;
            await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexProfile, Guid>>().InsertAsync(
                new IndexProfile(Guid.NewGuid(), null, null, profile.Strategy, profile.MaxTokensPerChunk, profile.OverlapTokens)));
            await IndexCorpusAsync(set);
            await WaitForFullTextAsync();

            var chunks = (await WithUnitOfWorkAsync(() => GetRequiredService<IChunkRepository>().GetListAsync())).ToDictionary(c => c.Id);
            notes.Add($"`{name}`: {chunks.Count} chunks");
            foreach (var evaluationCase in cases)
            {
                var (keyword, vector) = await LegsAsync(evaluationCase.Query);
                var fused = ReciprocalRankFusion.Fuse(keyword, vector, IndexSearchConsts.RrfK)
                    .Take(EvaluationReport.Depth)
                    .Select(f => chunks.TryGetValue(f.ChunkId, out var c) ? (c.DocumentId, c.HeadingPath) : (Guid.Empty, (string?)null))
                    .ToList();
                var service = await WithUnitOfWorkAsync(async () => (await GetRequiredService<IIndexSearchAppService>()
                        .SearchAsync(new IndexSearchInput { Query = evaluationCase.Query, MaxResultCount = EvaluationReport.Depth }))
                    .Hits.Select(h => (h.DocumentId, h.HeadingPath)).ToList());
                results.Add(Score(evaluationCase, name + " · fused", fused, documentIds));
                results.Add(Score(evaluationCase, name + " · service", service, documentIds));
            }
        }

        GetRequiredService<HtmlTablesSwitch>().Enabled = true;
        var reranker = GetRequiredService<IndexRanker>().Reranker;
        notes.Add(reranker is null ? "No rerank model: `service` is the fused order" : $"Rerank model {reranker.ModelId} in `service`");
        notes.Add($"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

        var report = EvaluationReport.ToMarkdown("Vault Index chunking strategies", results, notes, "heading 400 · service");
        _output.WriteLine(report);
        var path = Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_STRATEGY_REPORT") ?? Path.Combine(Directory.GetCurrentDirectory(), "index-strategies.md");
        await File.WriteAllTextAsync(path, report);
    }

    private static readonly (string Name, IndexEnrichment Enrichment)[] Enrichments =
    [
        ("no enrichment", IndexEnrichment.None),
        ("keywords", IndexEnrichment.Keywords),
        ("questions", IndexEnrichment.Questions),
        ("keywords + questions", IndexEnrichment.Keywords | IndexEnrichment.Questions)
    ];

    /// <summary>
    /// Indexes the corpus once per enrichment (heading chunking at 400 tokens) with the model in
    /// <c>VAULT_INDEX_EVAL_ENRICHMENT_MODEL</c>, and ranks every case with the fused legs and with search as configured.
    /// Reports what enrichment cost per document. The report goes to <c>VAULT_INDEX_EVAL_ENRICHMENT_REPORT</c> or
    /// <c>index-enrichment.md</c>.
    /// </summary>
    [EvaluationFact]
    public async Task Compare_enrichment()
    {
        if (!GetRequiredService<IndexEnricherProvider>().IsAvailable)
        {
            _output.WriteLine("No enrichment model (VAULT_INDEX_EVAL_ENRICHMENT_MODEL): nothing to compare.");
            return;
        }

        var set = EvaluationSet.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation"));
        Arrange(set);
        var cases = set.Cases();
        var documentIds = set.Documents.ToDictionary(d => d.DocumentId, d => d.Id);
        var results = new List<CaseResult>();
        var notes = new List<string>
        {
            $"Evaluation set version {set.Version}: {set.Documents.Count} documents, {cases.Count} cases",
            $"Heading chunking at 400 tokens; enrichment model {EvaluationModel.EnrichmentModelId}, {IndexChunkEnricher.BatchSize} chunks per call. `fused` is RRF 1:1 of the two legs; `service` is IIndexSearchAppService as configured"
        };

        foreach (var (name, enrichment) in Enrichments)
        {
            await ClearAsync();
            await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexProfile, Guid>>().InsertAsync(
                new IndexProfile(Guid.NewGuid(), null, null, ChunkingStrategy.Heading, 400, 0, enrichment)));
            await IndexCorpusAsync(set);
            await WaitForFullTextAsync();

            var chunks = (await WithUnitOfWorkAsync(() => GetRequiredService<IChunkRepository>().GetListAsync())).ToDictionary(c => c.Id);
            var runs = await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexRun, Guid>>().GetListAsync());
            var calls = chunks.Values.GroupBy(c => c.DocumentId).Sum(g => (g.Count() + IndexChunkEnricher.BatchSize - 1) / IndexChunkEnricher.BatchSize);
            var input = runs.Sum(r => r.LlmInputTokenCount ?? 0);
            var output = runs.Sum(r => r.LlmOutputTokenCount ?? 0);
            var failed = runs.Count(r => r.ErrorMessage is not null);
            notes.Add(enrichment == IndexEnrichment.None
                ? $"`{name}`: {chunks.Count} chunks"
                : $"`{name}`: {chunks.Count} chunks, {calls} model calls, per document {input / (double)set.Documents.Count:0} input and {output / (double)set.Documents.Count:0} output tokens; {failed} runs with enrichment failures");

            foreach (var evaluationCase in cases)
            {
                var (keyword, vector) = await LegsAsync(evaluationCase.Query);
                var fused = ReciprocalRankFusion.Fuse(keyword, vector, IndexSearchConsts.RrfK)
                    .Take(EvaluationReport.Depth)
                    .Select(f => chunks.TryGetValue(f.ChunkId, out var c) ? (c.DocumentId, c.HeadingPath) : (Guid.Empty, (string?)null))
                    .ToList();
                var service = await WithUnitOfWorkAsync(async () => (await GetRequiredService<IIndexSearchAppService>()
                        .SearchAsync(new IndexSearchInput { Query = evaluationCase.Query, MaxResultCount = EvaluationReport.Depth }))
                    .Hits.Select(h => (h.DocumentId, h.HeadingPath)).ToList());
                results.Add(Score(evaluationCase, name + " · fused", fused, documentIds));
                results.Add(Score(evaluationCase, name + " · service", service, documentIds));
            }
        }

        var reranker = GetRequiredService<IndexRanker>().Reranker;
        notes.Add(reranker is null ? "No rerank model: `service` is the fused order" : $"Rerank model {reranker.ModelId} in `service`");
        notes.Add($"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

        var report = EvaluationReport.ToMarkdown("Vault Index chunk enrichment", results, notes, "no enrichment · service");
        _output.WriteLine(report);
        var path = Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_ENRICHMENT_REPORT") ?? Path.Combine(Directory.GetCurrentDirectory(), "index-enrichment.md");
        await File.WriteAllTextAsync(path, report);
    }

    private async Task IndexCorpusAsync(EvaluationSet set)
    {
        var job = GetRequiredService<IndexDocumentJob>();
        var eventTime = new DateTime(2026, 11, 12, 0, 0, 0);
        foreach (var document in set.Documents)
        {
            eventTime = eventTime.AddMinutes(1);
            await job.ExecuteAsync(new IndexDocumentJobArgs
            {
                DocumentId = document.DocumentId,
                Kind = IndexDocumentJobKind.Sync,
                Trigger = IndexRunTrigger.Backfill,
                EventTime = eventTime
            });
        }

        var rows = await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexedDocument, Guid>>().GetListAsync());
        rows.Count(r => r.Status == IndexedDocumentStatus.Indexed).ShouldBe(set.Documents.Count);
    }

    private async Task<(IReadOnlyList<ChunkMatch> Keyword, IReadOnlyList<ChunkMatch> Vector)> LegsAsync(string query)
    {
        var scope = new ChunkSearchScope(true, [], null);
        var keyword = await WithUnitOfWorkAsync(() => GetRequiredService<IChunkSearchIndex>()
            .SearchKeywordsAsync(query, scope, IndexSearchConsts.CandidatesPerLeg));
        var vector = await WithUnitOfWorkAsync(async () =>
        {
            var serving = (await GetRequiredService<IndexGenerationManager>().FindServingAsync())!;
            var embedding = await GetRequiredService<IndexEmbedder>().EmbedAsync([query]);
            return await GetRequiredService<IChunkSearchIndex>().SearchVectorsAsync(serving.Id, embedding.Vectors[0], scope, IndexSearchConsts.CandidatesPerLeg);
        });
        return (keyword, vector);
    }

    /// <summary>The rerank variants rerank this many leading candidates, as search does by default.</summary>
    private const int RerankDepth = 30;

    private static readonly (string Name, double KeywordWeight)[] FusionVariants = [("fused 1:1", 1), ("fused 0.5:1", 0.5), ("fused 0.25:1", 0.25)];

    private VaultIndexSearchOptions Options => GetRequiredService<Microsoft.Extensions.Options.IOptions<VaultIndexSearchOptions>>().Value;

    private async Task<(IEnumerable<CaseResult> Results, TimeSpan RerankTime)> RankAsync(
        EvaluationCase evaluationCase,
        IReadOnlyDictionary<Guid, Chunk> chunks,
        IReadOnlyDictionary<Guid, string> texts,
        IReadOnlyDictionary<Guid, string> documentIds,
        IReranker? reranker)
    {
        var scope = new ChunkSearchScope(true, [], null);

        var keyword = await WithUnitOfWorkAsync(() => GetRequiredService<IChunkSearchIndex>()
            .SearchKeywordsAsync(evaluationCase.Query, scope, IndexSearchConsts.CandidatesPerLeg));
        var vector = await WithUnitOfWorkAsync(async () =>
        {
            var serving = (await GetRequiredService<IndexGenerationManager>().FindServingAsync())!;
            var embedding = await GetRequiredService<IndexEmbedder>().EmbedAsync([evaluationCase.Query]);
            return await GetRequiredService<IChunkSearchIndex>().SearchVectorsAsync(serving.Id, embedding.Vectors[0], scope, IndexSearchConsts.CandidatesPerLeg);
        });
        var service = await WithUnitOfWorkAsync(async () => (await GetRequiredService<IIndexSearchAppService>()
                .SearchAsync(new IndexSearchInput { Query = evaluationCase.Query, MaxResultCount = EvaluationReport.Depth }))
            .Hits.Select(h => (h.DocumentId, h.HeadingPath)).ToList());

        var rankings = new List<(string Mode, IReadOnlyList<Guid> ChunkIds)>
        {
            ("keyword", keyword.Select(m => m.ChunkId).ToList()),
            ("vector", vector.Select(m => m.ChunkId).ToList())
        };
        foreach (var (name, weight) in FusionVariants)
        {
            rankings.Add((name, ReciprocalRankFusion.Fuse(keyword, vector, IndexSearchConsts.RrfK, weight, 1).Select(f => f.ChunkId).ToList()));
        }

        var elapsed = TimeSpan.Zero;
        if (reranker is not null)
        {
            // A cross-encoder scores each (query, text) pair on its own, so one call over the union of every ranking's
            // leading candidates serves them all.
            var union = rankings.SelectMany(r => r.ChunkIds.Take(RerankDepth)).Distinct().ToList();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await reranker.RerankAsync(
                evaluationCase.Query,
                union.Select(id => TextTruncator.AtCharBoundary(texts[id], IndexSearchConsts.MaxRerankTextLength)).ToList());
            elapsed = watch.Elapsed;
            var scores = union.Select((id, i) => (id, score: result.Scores[i])).ToDictionary(x => x.id, x => x.score);

            foreach (var (mode, ids) in rankings.Where(r => r.Mode is "vector" or "fused 1:1" or "fused 0.5:1").ToList())
            {
                var head = ids.Take(RerankDepth).OrderByDescending(id => scores[id]).ToList();
                rankings.Add(("rerank " + mode, head));
            }
        }

        List<(Guid DocumentId, string? HeadingPath)> Resolve(IReadOnlyList<Guid> ids)
            => ids.Take(EvaluationReport.Depth)
                .Select(id => chunks.TryGetValue(id, out var c) ? (c.DocumentId, c.HeadingPath) : (Guid.Empty, null))
                .ToList();

        var results = rankings.Select(r => Score(evaluationCase, r.Mode, Resolve(r.ChunkIds), documentIds)).ToList();
        results.Add(Score(evaluationCase, "service", service, documentIds));
        return (results, elapsed);
    }

    private static CaseResult Score(
        EvaluationCase evaluationCase,
        string mode,
        IReadOnlyList<(Guid DocumentId, string? HeadingPath)> ranking,
        IReadOnlyDictionary<Guid, string> documentIds)
    {
        int? documentRank = null, passageRank = null;
        for (var i = 0; i < ranking.Count; i++)
        {
            if (documentIds.GetValueOrDefault(ranking[i].DocumentId) != evaluationCase.Document)
            {
                continue;
            }

            documentRank ??= i + 1;
            if (evaluationCase.Heading is not null && ranking[i].HeadingPath?.Contains(evaluationCase.Heading, StringComparison.Ordinal) == true)
            {
                passageRank ??= i + 1;
            }
        }

        return new CaseResult(evaluationCase, mode, documentRank, evaluationCase.Heading is null ? null : passageRank);
    }

    private void Arrange(EvaluationSet set)
    {
        var integration = GetRequiredService<IDocumentIntegrationService>();
        foreach (var document in set.Documents)
        {
            integration.FindReadyAsync(document.DocumentId).Returns(new DocumentIntegrationDto
            {
                Id = document.DocumentId,
                DocumentTypeId = document.TypeId,
                Title = document.Title,
                Language = "ja",
                Markdown = document.Markdown
            });
        }

        GetRequiredService<IDocumentAppService>().GetReadScopeAsync()
            .Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => set.Documents
            .Select(d => d.Type).Distinct()
            .Select(t => new DocumentTypeSummaryDto { Id = EvaluationDocument.StableGuid("type:" + t), TypeCode = t, DisplayName = t })
            .ToList());
    }

    private static async Task ClearAsync()
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.Evaluation.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM VaultIndexChunkVectors; DELETE FROM VaultIndexChunks; DELETE FROM VaultIndexRuns; DELETE FROM VaultIndexedDocuments; DELETE FROM VaultIndexGenerations; DELETE FROM VaultIndexProfiles;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForFullTextAsync()
    {
        const string sql = """
            SELECT CASE WHEN OBJECTPROPERTYEX(OBJECT_ID('VaultIndexChunks'), 'TableFulltextPendingChanges') = 0
                          AND OBJECTPROPERTYEX(OBJECT_ID('VaultIndexChunks'), 'TableFulltextItemCount') = (SELECT COUNT(*) FROM VaultIndexChunks)
                        THEN 1 ELSE 0 END
            """;
        await using var connection = new SqlConnection(SqlServerTestDatabase.Evaluation.ConnectionString);
        await connection.OpenAsync();
        for (var i = 0; i < 240; i++)
        {
            await using var command = new SqlCommand(sql, connection);
            if ((int)(await command.ExecuteScalarAsync())! == 1)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The full-text index did not catch up in two minutes.");
    }
}

/// <summary>Whether the evaluation chunker rewrites HTML tables, so a run can show what reading them as Markdown changed.</summary>
public sealed class HtmlTablesSwitch
{
    public bool Enabled { get; set; } = true;
}

/// <summary>The real chunker, with the HTML rewrite switchable for the "HTML tables dropped" strategy.</summary>
public class EvaluationChunker : IndexChunker
{
    private readonly HtmlTablesSwitch _switch;

    public EvaluationChunker(HtmlTablesSwitch htmlTablesSwitch)
    {
        _switch = htmlTablesSwitch;
    }

    public override string Prepare(string markdown) => _switch.Enabled ? base.Prepare(markdown) : markdown;
}

/// <summary>Retries an embedding call a few times with a growing pause: the evaluation's calls are many and bursty.</summary>
public sealed class RetryingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var list = values.ToList();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.GenerateAsync(list, options, cancellationToken);
            }
            catch (ClientResultException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }
}

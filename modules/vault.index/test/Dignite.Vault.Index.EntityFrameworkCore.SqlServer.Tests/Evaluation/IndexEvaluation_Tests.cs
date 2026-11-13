using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Compilation;
using Dignite.Vault.Index.EntityFrameworkCore;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Mcp.Search;
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

    /// <summary>Optional: Index's chat model (enrichment, summaries, and the navigating agent), on the embedding endpoint.</summary>
    public static string? ChatModelId => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_CHAT_MODEL");

    /// <summary>
    /// Optional: the model that plays the client agent in <c>Compare_agents</c>, on the embedding endpoint; the chat
    /// model when unset. A deployment's MCP clients are usually stronger models than the one it compiles with.
    /// </summary>
    public static string? AgentModelId => Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_AGENT_MODEL") ?? ChatModelId;

    public const string AgentClientKey = "evaluation-agent";
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

        if (!string.IsNullOrWhiteSpace(EvaluationModel.ChatModelId))
        {
            var chat = new CountingChatClient(client.GetChatClient(EvaluationModel.ChatModelId).AsIChatClient());
            context.Services.AddSingleton(chat);
            context.Services.AddKeyedChatClient(VaultIndexConsts.ChatClientKey, _ => chat);
            context.Services.AddKeyedChatClient(
                EvaluationModel.AgentClientKey,
                _ => new CountingChatClient(client.GetChatClient(EvaluationModel.AgentModelId).AsIChatClient()));
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
    /// <c>VAULT_INDEX_EVAL_CHAT_MODEL</c>, and ranks every case with the fused legs and with search as configured.
    /// Reports what enrichment cost per document. The report goes to <c>VAULT_INDEX_EVAL_ENRICHMENT_REPORT</c> or
    /// <c>index-enrichment.md</c>.
    /// </summary>
    [EvaluationFact]
    public async Task Compare_enrichment()
    {
        if (!GetRequiredService<IndexEnricherProvider>().IsAvailable)
        {
            _output.WriteLine("No enrichment model (VAULT_INDEX_EVAL_CHAT_MODEL): nothing to compare.");
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
            $"Heading chunking at 400 tokens; enrichment model {EvaluationModel.ChatModelId}, {IndexChunkEnricher.BatchSize} chunks per call. `fused` is RRF 1:1 of the two legs; `service` is IIndexSearchAppService as configured"
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

    /// <summary>The documents of version 3 whose article headings carry no caption.</summary>
    private static readonly HashSet<string> UncaptionedDocuments = ["outsourcing-basic-terms", "safety-rules"];

    private const string NavigatorInstructions =
        "You find where a question is answered in a document. You are given the question and the document's outline: one "
        + "line per section: its path (such as 1.2), its title and, when present, a summary of the section. Return the path "
        + "of the one section most likely to contain the answer, exactly as it starts its line; prefer the most specific "
        + "section.\n\n" + PromptBoundary.BoundaryRule;

    /// <summary>
    /// Outline navigation, the done mark of section summaries: compiles them for the corpus with the model in
    /// <c>VAULT_INDEX_EVAL_CHAT_MODEL</c>, then has the same model pick the section that answers each written question
    /// from the expected document's outline, once with titles only and once with titles and summaries. A pick is
    /// precise when its heading path contains the expected heading, and covers it when it is that section, inside it
    /// or above it. Reports what compiling cost per document. The report goes to
    /// <c>VAULT_INDEX_EVAL_NAVIGATION_REPORT</c> or <c>index-navigation.md</c>.
    /// </summary>
    [EvaluationFact]
    public async Task Compare_navigation()
    {
        if (!GetRequiredService<DocumentSummaryCompilerProvider>().IsAvailable)
        {
            _output.WriteLine("No chat model (VAULT_INDEX_EVAL_CHAT_MODEL): nothing to compare.");
            return;
        }

        var set = EvaluationSet.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation"));
        Arrange(set);
        await ClearAsync();
        await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexProfile, Guid>>().InsertAsync(
            new IndexProfile(Guid.NewGuid(), null, null, ChunkingStrategy.Heading, 400, 0, IndexEnrichment.None, IndexCompilation.Summaries)));
        await IndexCorpusAsync(set);

        var model = GetRequiredService<CountingChatClient>();
        var compileJob = GetRequiredService<IndexCompileJob>();
        foreach (var document in set.Documents)
        {
            await compileJob.ExecuteAsync(new IndexCompileJobArgs { DocumentId = document.DocumentId });
        }

        var compileCalls = model.Calls;
        var artifacts = await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<Artifact, Guid>>().GetListAsync());
        var search = GetRequiredService<IIndexSearchAppService>();
        var outlines = new Dictionary<string, DocumentOutlineDto>();
        foreach (var document in set.Documents)
        {
            outlines[document.Id] = await WithUnitOfWorkAsync(() => search.GetOutlineAsync(document.DocumentId));
        }

        var cases = set.Cases().Where(c => c.Source != "field" && c.Heading is not null && outlines[c.Document].Nodes.Count > 0).ToList();
        var results = new List<NavigationResult>();
        foreach (var evaluationCase in cases)
        {
            var outline = outlines[evaluationCase.Document];
            foreach (var withSummaries in new[] { false, true })
            {
                var path = await NavigateAsync(evaluationCase.Query, outline, withSummaries);
                results.Add(ScoreNavigation(evaluationCase, outline, path, withSummaries));
            }
        }

        var report = NavigationReport(set, cases.Count, results, artifacts, compileCalls, outlines) + Faithfulness(set, artifacts, outlines);
        _output.WriteLine(report);
        var reportPath = Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_NAVIGATION_REPORT") ?? Path.Combine(Directory.GetCurrentDirectory(), "index-navigation.md");
        await File.WriteAllTextAsync(reportPath, report);
    }

    private async Task<string?> NavigateAsync(string question, DocumentOutlineDto outline, bool withSummaries)
    {
        var lines = new System.Text.StringBuilder();
        foreach (var node in outline.Nodes)
        {
            lines.Append(new string(' ', 2 * (node.Depth - 1))).Append(node.Pointer[(node.Pointer.LastIndexOf('/') + 1)..]).Append(' ').Append(node.Title);
            if (withSummaries && node.Summary is not null)
            {
                lines.Append(" — ").Append(node.Summary);
            }

            lines.Append('\n');
        }

        var user = "Question:\n" + PromptBoundary.WrapField(question) + "\nOutline:\n" + PromptBoundary.WrapDocument(lines.ToString().TrimEnd());
        if (withSummaries && outline.DocumentSummary is not null)
        {
            user = "Document summary:\n" + PromptBoundary.WrapField(outline.DocumentSummary) + "\n" + user;
        }

        // An answer that cannot be read is asked for again, as the summary compiler does, so both outlines are judged on
        // what the navigator chose rather than on the model's occasional whitespace runaway in JSON schema mode.
        ChatResponse response = default!;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            response = await ServiceProvider.GetRequiredKeyedService<IChatClient>(VaultIndexConsts.ChatClientKey).GetResponseAsync(
                [new ChatMessage(ChatRole.System, NavigatorInstructions), new ChatMessage(ChatRole.User, user)],
                new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(AIJsonUtilities.CreateJsonSchema(typeof(NavigationAnswer)), "section_pick"),
                    MaxOutputTokens = 100
                });
            if (response.Text.Contains("\"path\"", StringComparison.Ordinal))
            {
                break;
            }
        }

        try
        {
            // A model may write the path as a JSON number (1.2) or echo the title after it: take the first token either way.
            var path = Ai.LlmJson.Read<NavigationAnswer>(response.Text)?.Path;
            var text = path is not { } element ? null
                : element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() : element.GetRawText();
            return text is null
                ? "no path: " + response.Text[..Math.Min(60, response.Text.Length)]
                : text.Trim().Split(' ', 2)[0].TrimEnd('.');
        }
        catch (System.Text.Json.JsonException)
        {
            return "unreadable: " + response.Text[..Math.Min(60, response.Text.Length)];
        }
    }

    private static NavigationResult ScoreNavigation(EvaluationCase evaluationCase, DocumentOutlineDto outline, string? path, bool withSummaries)
    {
        var paths = HeadingPaths(outline);
        var picked = outline.Nodes.FirstOrDefault(n => n.Pointer.EndsWith("/" + path, StringComparison.Ordinal));
        var expected = outline.Nodes.Where(n => paths[n.Pointer].Contains(evaluationCase.Heading!, StringComparison.Ordinal)).Select(Position).ToList();
        var position = picked is null ? null : Position(picked);
        var precise = picked is not null && paths[picked.Pointer].Contains(evaluationCase.Heading!, StringComparison.Ordinal);
        var covers = position is not null && expected.Any(e => e.StartsWith(position + ".", StringComparison.Ordinal) || e == position || position.StartsWith(e + ".", StringComparison.Ordinal));
        return new NavigationResult(evaluationCase, withSummaries, precise, covers, picked?.ChunkCount ?? 0, path, picked?.Title);
    }

    private static string Position(OutlineNodeDto node) => node.Pointer[(node.Pointer.LastIndexOf('/') + 1)..];

    /// <summary>Each node's titles from the root down, joined, as the chunks' heading paths name them.</summary>
    private static Dictionary<string, string> HeadingPaths(DocumentOutlineDto outline)
    {
        var stack = new List<string>();
        var paths = new Dictionary<string, string>();
        foreach (var node in outline.Nodes)
        {
            stack.RemoveRange(node.Depth - 1, stack.Count - (node.Depth - 1));
            stack.Add(node.Title);
            paths[node.Pointer] = string.Join(" > ", stack);
        }

        return paths;
    }

    private static string NavigationReport(
        EvaluationSet set,
        int caseCount,
        List<NavigationResult> results,
        List<Artifact> artifacts,
        int compileCalls,
        Dictionary<string, DocumentOutlineDto> outlines)
    {
        var report = new System.Text.StringBuilder();
        report.Append("# Vault Index outline navigation\n\n");
        report.Append($"- Evaluation set version {set.Version}: {set.Documents.Count} documents; {caseCount} written questions whose answer is under a heading\n");
        report.Append($"- Heading chunking at 400 tokens; chat model {EvaluationModel.ChatModelId} compiles the summaries and navigates\n");
        report.Append("- The navigator gets the question and the expected document's outline and picks one section. `precise`: the pick's heading path contains the expected heading. `covers`: the pick is that section, inside it or above it. `chunks`: the chunks a read of the pick returns, on average\n");
        report.Append($"- Compiling: {artifacts.Count} documents, {compileCalls} model calls, per document {artifacts.Average(a => a.InputTokenCount ?? 0):0} input and {artifacts.Average(a => a.OutputTokenCount ?? 0):0} output tokens; {artifacts.Count(a => a.Note is not null)} with a note\n");
        report.Append($"- Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n\n");

        report.Append("| Questions | Outline | Cases | Precise | Covers | Chunks |\n| --- | --- | ---: | ---: | ---: | ---: |\n");
        var groups = new (string Name, Func<NavigationResult, bool> Filter)[]
        {
            ("all", _ => true),
            ("captioned headings", r => !UncaptionedDocuments.Contains(r.Case.Document)),
            ("uncaptioned headings (v3)", r => UncaptionedDocuments.Contains(r.Case.Document)),
            ("Japanese", r => r.Case.Language == "ja"),
            ("Chinese and English", r => r.Case.Language != "ja")
        };
        foreach (var (name, filter) in groups)
        {
            foreach (var withSummaries in new[] { false, true })
            {
                var rows = results.Where(r => r.WithSummaries == withSummaries && filter(r)).ToList();
                if (rows.Count == 0)
                {
                    continue;
                }

                report.Append($"| {name} | {(withSummaries ? "titles + summaries" : "titles")} | {rows.Count} | {rows.Average(r => r.Precise ? 1.0 : 0):P0} | {rows.Average(r => r.Covers ? 1.0 : 0):P0} | {rows.Average(r => r.ChunkCount):0.0} |\n");
            }
        }

        var unresolved = results.Count(r => r.PickedTitle is null);
        report.Append($"\n{unresolved} of {results.Count} answers named no section of the outline.\n");
        foreach (var withSummaries in new[] { false, true })
        {
            var missed = results.Where(r => r.WithSummaries == withSummaries && !r.Precise)
                .Select(r => $"`{r.Case.Document}` {r.Case.Heading}: {r.Case.Query} → `{r.Answer}` {r.PickedTitle}")
                .ToList();
            if (missed.Count > 0)
            {
                report.Append(withSummaries ? "\nMissed with summaries:\n\n" : "\nMissed with titles only:\n\n")
                    .Append(string.Join("\n", missed.Select(m => "- " + m))).Append('\n');
            }
        }

        var sample = outlines.GetValueOrDefault("outsourcing-basic-terms");
        if (sample is not null)
        {
            report.Append("\nSample (`outsourcing-basic-terms`):\n\n- Document: ").Append(sample.DocumentSummary).Append('\n');
            foreach (var node in sample.Nodes.Take(6))
            {
                report.Append("- ").Append(Position(node)).Append(' ').Append(node.Title).Append(": ").Append(node.Summary).Append('\n');
            }
        }

        return report.ToString();
    }

    /// <summary>
    /// How far the compiled summaries can be trusted, by what can be checked without a judge: the summaries that state a
    /// number the document's text does not (<see cref="SummaryFacts"/> against the whole document), and how many
    /// documents and sections got one.
    /// </summary>
    private static string Faithfulness(EvaluationSet set, List<Artifact> artifacts, Dictionary<string, DocumentOutlineDto> outlines)
    {
        int documents = 0, documentsUnsupported = 0, documentsOmitting = 0, sections = 0, sectionsUnsupported = 0;
        var examples = new List<string>();
        var documentLines = new List<string>();
        foreach (var artifact in artifacts)
        {
            var document = set.Documents.Single(d => d.DocumentId == artifact.DocumentId);
            var summaries = DocumentSummaries.FromJson(artifact.Content);
            if (summaries is null)
            {
                continue;
            }

            if (summaries.Document is { } documentSummary)
            {
                documents++;
                documentLines.Add($"`{document.Id}`: {documentSummary}");
                if (OmissionPhrase.IsMatch(documentSummary))
                {
                    documentsOmitting++;
                }

                if (SummaryFacts.Unsupported(documentSummary, document.Markdown) is { Count: > 0 } invented)
                {
                    documentsUnsupported++;
                    examples.Add($"`{document.Id}` document: {string.Join(", ", invented)} in \"{documentSummary}\"");
                }
            }

            foreach (var (key, summary) in summaries.Sections)
            {
                sections++;
                if (SummaryFacts.Unsupported(summary, document.Markdown) is { Count: > 0 } invented)
                {
                    sectionsUnsupported++;
                    examples.Add($"`{document.Id}` {key}: {string.Join(", ", invented)} in \"{summary}\"");
                }
            }
        }

        var nodes = outlines.Values.Sum(o => o.TotalNodeCount);
        var report = new System.Text.StringBuilder("\n## Faithfulness\n\n");
        report.Append("| Summaries | Written | Of | Stating a number the document does not | Saying what the text lacks |\n| --- | ---: | ---: | ---: | ---: |\n");
        report.Append($"| Document | {documents} | {set.Documents.Count} | {documentsUnsupported} | {documentsOmitting} |\n");
        report.Append($"| Section | {sections} | {nodes} | {sectionsUnsupported} | |\n");
        if (examples.Count > 0)
        {
            report.Append("\n").Append(string.Join("\n", examples.Select(e => "- " + e))).Append("\n");
        }

        report.Append("\nDocument summaries:\n\n").Append(string.Join("\n", documentLines.OrderBy(l => l, StringComparer.Ordinal).Select(l => "- " + l))).Append("\n");

        return report.ToString();
    }

    /// <summary>A summary that talks about what it or the text leaves out, instead of what the text says.</summary>
    private static readonly System.Text.RegularExpressions.Regex OmissionPhrase = new(
        "記載されていない|記載がない|記載はない|含まない|含まれていない|含まれない|明記されていない|不明|未記載|触れていない|not (stated|mentioned|specified|included)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private sealed record NavigationResult(
        EvaluationCase Case, bool WithSummaries, bool Precise, bool Covers, int ChunkCount, string? Answer, string? PickedTitle);

    private sealed class NavigationAnswer
    {
        public System.Text.Json.JsonElement? Path { get; set; }
    }

    private const string AgentInstructions =
        "You answer questions about the documents of 株式会社ディグナイト, the company asking, using the tools: they "
        + "search the documents' text, list the document types, show a document's sections and read text. Look up every "
        + "document the question needs; when it asks about all documents of a kind, check each of them. Answer concisely "
        + "in the language of the question, name the documents you used, and give amounts and dates as the documents "
        + "write them.\n\n" + PromptBoundary.BoundaryRule;

    /// <summary>Each question is asked this many times per toolset: an agent's path varies from run to run.</summary>
    private const int AgentRepeats = 2;

    /// <summary>
    /// The P3 gate: whether questions only several documents answer need more than the tools Index already has. An
    /// agent (the model in <c>VAULT_INDEX_EVAL_CHAT_MODEL</c> with tool calls) answers the set's cross questions twice:
    /// with the toolset before P2 (search, outline with titles, read) while no summaries exist, then with P2's (the same
    /// plus the catalog, and outlines with summaries) after compiling them. The tools are the MCP tools themselves, with
    /// their descriptions and schemas. An answer is scored by the facts it contains. The report goes to
    /// <c>VAULT_INDEX_EVAL_AGENT_REPORT</c> or <c>index-agents.md</c>.
    /// </summary>
    [EvaluationFact]
    public async Task Compare_agents()
    {
        if (!GetRequiredService<DocumentSummaryCompilerProvider>().IsAvailable)
        {
            _output.WriteLine("No chat model (VAULT_INDEX_EVAL_CHAT_MODEL): nothing to compare.");
            return;
        }

        var set = EvaluationSet.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation"));
        Arrange(set);
        await ClearAsync();
        await WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexProfile, Guid>>().InsertAsync(
            new IndexProfile(Guid.NewGuid(), null, null, ChunkingStrategy.Heading, 400, 0, IndexEnrichment.None, IndexCompilation.Summaries)));
        await IndexCorpusAsync(set);
        await WaitForFullTextAsync();

        var results = new List<AgentResult>();
        foreach (var question in set.CrossQuestions)
        {
            for (var run = 0; run < AgentRepeats; run++)
            {
                results.Add(await AskAgentAsync(set, question, withCatalog: false));
            }
        }

        var compileJob = GetRequiredService<IndexCompileJob>();
        foreach (var document in set.Documents)
        {
            await compileJob.ExecuteAsync(new IndexCompileJobArgs { DocumentId = document.DocumentId });
        }

        foreach (var question in set.CrossQuestions)
        {
            for (var run = 0; run < AgentRepeats; run++)
            {
                results.Add(await AskAgentAsync(set, question, withCatalog: true));
            }
        }

        var report = AgentReport(set, results);
        _output.WriteLine(report);
        var reportPath = Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_AGENT_REPORT") ?? Path.Combine(Directory.GetCurrentDirectory(), "index-agents.md");
        await File.WriteAllTextAsync(reportPath, report);
    }

    private async Task<AgentResult> AskAgentAsync(EvaluationSet set, EvaluationCrossQuestion question, bool withCatalog)
    {
        var search = GetRequiredService<IIndexSearchAppService>();
        var tools = new List<AITool>
        {
            McpTool(nameof(IndexSearchTools.SearchAsync), search),
            McpTool(nameof(IndexSearchTools.OutlineAsync), search),
            McpTool(nameof(IndexSearchTools.ReadAsync), search),
            AIFunctionFactory.Create(
                () => set.Documents.Select(d => d.Type).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList(),
                "vault_extract_list_document_types",
                "The document type codes, to pass as documentTypeCode.")
        };
        if (withCatalog)
        {
            tools.Add(McpTool(nameof(IndexSearchTools.CatalogAsync), search));
        }

        var agent = new ChatClientBuilder(ServiceProvider.GetRequiredKeyedService<IChatClient>(EvaluationModel.AgentClientKey))
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = 12;
                f.IncludeDetailedErrors = true;
            })
            .Build();

        ChatResponse response;
        try
        {
            response = await agent.GetResponseAsync(
                [new ChatMessage(ChatRole.System, AgentInstructions), new ChatMessage(ChatRole.User, question.Query)],
                new ChatOptions { Tools = tools, MaxOutputTokens = 1_500 });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AgentResult(question, withCatalog, "error: " + exception.Message, [], 0, 0, 0);
        }

        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.Name).ToList();
        var answer = string.IsNullOrWhiteSpace(response.Text)
            ? $"(no text; finish {response.FinishReason?.Value ?? "none"}, {response.Messages.Count} messages, last {string.Join("+", response.Messages.LastOrDefault()?.Contents.Select(c => c.GetType().Name) ?? [])})"
            : response.Text;
        var normalized = NormalizeFact(answer);
        var missed = question.Facts.Where(spellings => !spellings.Any(s => normalized.Contains(NormalizeFact(s), StringComparison.Ordinal))).ToList();
        return new AgentResult(
            question, withCatalog, answer, calls, question.Facts.Count - missed.Count,
            response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0)
        {
            Missed = missed.Select(m => m[0]).ToList()
        };
    }

    /// <summary>An MCP tool as an AI function: its name, description and schema as the MCP server publishes them.</summary>
    private static AIFunction McpTool(string methodName, IIndexSearchAppService search)
    {
        var method = typeof(IndexSearchTools).GetMethod(methodName)!;
        var name = ((ModelContextProtocol.Server.McpServerToolAttribute)Attribute.GetCustomAttribute(
            method, typeof(ModelContextProtocol.Server.McpServerToolAttribute))!).Name!;
        return AIFunctionFactory.Create(method, target: null, new AIFunctionFactoryOptions
        {
            Name = name,
            ConfigureParameterBinding = parameter => parameter.ParameterType == typeof(IIndexSearchAppService)
                ? new AIFunctionFactoryOptions.ParameterBindingOptions { ExcludeFromSchema = true, BindParameter = (_, _) => search }
                : default
        });
    }

    private static string NormalizeFact(string text)
        => new string(text.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(c => !char.IsWhiteSpace(c) && c != ',' && c != '，')
            .ToArray()).ToLowerInvariant();

    private static string AgentReport(EvaluationSet set, List<AgentResult> results)
    {
        var report = new System.Text.StringBuilder();
        report.Append("# Vault Index cross-document questions\n\n");
        report.Append($"- Evaluation set version {set.Version}: {set.Documents.Count} documents; {set.CrossQuestions.Count} questions only several documents answer, each asked {AgentRepeats} times per toolset\n");
        report.Append($"- Agent: {EvaluationModel.AgentModelId} with tool calls (at most 12 rounds), the MCP tools as published; summaries compiled by {EvaluationModel.ChatModelId}. `before P2`: search, outline (titles), read, type list. `P2`: the same plus the catalog, after compiling section and document summaries (heading chunking at 400 tokens)\n");
        report.Append("- An answer is scored by the facts it contains (any one spelling of each, after NFKC, without spaces or commas). `complete`: every fact. `calls`: tool calls per answer\n");
        report.Append($"- Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n\n");

        report.Append("| Toolset | Answers | Complete | Facts | Calls | Input tokens | Output tokens |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: |\n");
        foreach (var withCatalog in new[] { false, true })
        {
            var rows = results.Where(r => r.WithCatalog == withCatalog).ToList();
            var facts = rows.Sum(r => r.Question.Facts.Count);
            report.Append($"| {(withCatalog ? "P2" : "before P2")} | {rows.Count} | {rows.Count(r => r.FactsFound == r.Question.Facts.Count)} | {rows.Sum(r => r.FactsFound)} / {facts} | {rows.Average(r => r.Calls.Count):0.0} | {rows.Average(r => r.InputTokens):0} | {rows.Average(r => r.OutputTokens):0} |\n");
        }

        var p2 = results.Where(r => r.WithCatalog).SelectMany(r => r.Calls).GroupBy(c => c).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");
        report.Append("\nTool calls with the P2 toolset: ").Append(string.Join(", ", p2)).Append("\n\n");

        report.Append("| # | Question | Before P2 (facts per run) | P2 (facts per run) |\n| ---: | --- | --- | --- |\n");
        for (var i = 0; i < set.CrossQuestions.Count; i++)
        {
            var question = set.CrossQuestions[i];
            string Cell(bool withCatalog) => string.Join(", ", results
                .Where(r => r.Question == question && r.WithCatalog == withCatalog)
                .Select(r => $"{r.FactsFound}/{question.Facts.Count} ({r.Calls.Count} calls)"));
            report.Append($"| {i + 1} | {question.Query} | {Cell(false)} | {Cell(true)} |\n");
        }

        report.Append("\nMissed facts:\n\n");
        foreach (var result in results.Where(r => r.Missed.Count > 0))
        {
            var answer = result.Answer.Replace("\n", " ");
            report.Append($"- {(result.WithCatalog ? "P2" : "before P2")} · {result.Question.Query} · missed {string.Join(", ", result.Missed)} · answer: {answer[..Math.Min(240, answer.Length)]}\n");
        }

        return report.ToString();
    }

    private sealed record AgentResult(
        EvaluationCrossQuestion Question,
        bool WithCatalog,
        string Answer,
        List<string> Calls,
        int FactsFound,
        long InputTokens,
        long OutputTokens)
    {
        public List<string> Missed { get; init; } = [];
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
            "DELETE FROM VaultIndexChunkVectors; DELETE FROM VaultIndexChunks; DELETE FROM VaultIndexRuns; DELETE FROM VaultIndexArtifacts; DELETE FROM VaultIndexedDocuments; DELETE FROM VaultIndexGenerations; DELETE FROM VaultIndexProfiles;",
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

/// <summary>Counts the chat calls, and retries one a few times with a growing pause, as the embedding wrapper does.</summary>
public sealed class CountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private int _calls;

    public int Calls => _calls;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        System.Threading.Interlocked.Increment(ref _calls);
        var list = messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(list, options, cancellationToken);
            }
            catch (ClientResultException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }
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

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
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Search;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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

        if (string.IsNullOrWhiteSpace(SqlServerTestDatabase.ServerConnectionString) || string.IsNullOrWhiteSpace(EvaluationModel.ModelId))
        {
            return;
        }

        SqlServerTestDatabase.Evaluation.Configure(context);
        var client = new OpenAIClient(new ApiKeyCredential(EvaluationModel.ApiKey!), new OpenAIClientOptions { Endpoint = new Uri(EvaluationModel.Endpoint!) });
        context.Services.AddKeyedEmbeddingGenerator(
            VaultIndexConsts.EmbeddingGeneratorKey,
            _ => client.GetEmbeddingClient(EvaluationModel.ModelId).AsIEmbeddingGenerator(EvaluationModel.Dimensions));
    }
}

/// <summary>
/// The retrieval evaluation (design section 13): indexes the corpus through the real ingestion job, then ranks every
/// case three ways (the fused search the egress returns, and each leg alone) and reports hit rate and MRR. It measures
/// and does not assert quality; the report goes to the test output and to <c>VAULT_INDEX_EVAL_REPORT</c> or
/// <c>index-evaluation.md</c> in the working directory.
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
        var cases = set.Cases();
        var results = new List<CaseResult>();
        foreach (var evaluationCase in cases)
        {
            results.AddRange(await RankAsync(evaluationCase, chunks, documentIds));
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
                $"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
            ]);

        _output.WriteLine(report);
        var path = Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_REPORT") ?? Path.Combine(Directory.GetCurrentDirectory(), "index-evaluation.md");
        await File.WriteAllTextAsync(path, report);
    }

    private async Task<IEnumerable<CaseResult>> RankAsync(
        EvaluationCase evaluationCase,
        IReadOnlyDictionary<Guid, Chunk> chunks,
        IReadOnlyDictionary<Guid, string> documentIds)
    {
        var scope = new ChunkSearchScope(true, [], null);

        var fused = await WithUnitOfWorkAsync(async () => (await GetRequiredService<IIndexSearchAppService>()
                .SearchAsync(new IndexSearchInput { Query = evaluationCase.Query, MaxResultCount = EvaluationReport.Depth }))
            .Hits.Select(h => (h.DocumentId, h.HeadingPath)).ToList());

        var keyword = await WithUnitOfWorkAsync(() => GetRequiredService<IChunkSearchIndex>()
            .SearchKeywordsAsync(evaluationCase.Query, scope, EvaluationReport.Depth));

        var vector = await WithUnitOfWorkAsync(async () =>
        {
            var serving = (await GetRequiredService<IndexGenerationManager>().FindServingAsync())!;
            var embedding = await GetRequiredService<IndexEmbedder>().EmbedAsync([evaluationCase.Query]);
            return await GetRequiredService<IChunkSearchIndex>().SearchVectorsAsync(serving.Id, embedding.Vectors[0], scope, EvaluationReport.Depth);
        });

        List<(Guid DocumentId, string? HeadingPath)> Resolve(IReadOnlyList<ChunkMatch> matches)
            => matches.Select(m => (m.DocumentId, chunks.TryGetValue(m.ChunkId, out var c) ? c.HeadingPath : null)).ToList();

        return
        [
            Score(evaluationCase, "fused", fused, documentIds),
            Score(evaluationCase, "keyword", Resolve(keyword), documentIds),
            Score(evaluationCase, "vector", Resolve(vector), documentIds)
        ];
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
            "DELETE FROM VaultIndexChunkVectors; DELETE FROM VaultIndexChunks; DELETE FROM VaultIndexRuns; DELETE FROM VaultIndexedDocuments; DELETE FROM VaultIndexGenerations;",
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

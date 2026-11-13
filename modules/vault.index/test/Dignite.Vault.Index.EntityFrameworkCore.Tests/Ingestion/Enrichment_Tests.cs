using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Runs;
using Dignite.Vault.Index.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Chunk enrichment through the real ingestion job on SQLite, with a scripted model registered as the host would: what
/// a profile switches on reaches the chunks, the keyword leg and the embedded text, its cost is on the run, and the
/// settings stamp names it only when it was applied.
/// </summary>
public class Enrichment_Tests : VaultIndexTestBase<VaultIndexEnrichmentTestModule>
{
    private const string Lease = """
        # 賃貸借契約書

        本契約は、甲と乙の間で締結される。

        ## 第2条（賃料）

        乙は毎月末日までに賃料を支払うものとする。
        """;

    private static readonly Guid LeaseType = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid LeaseDoc = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private readonly ScriptedEnrichmentChatClient _model;
    private readonly FakeEmbeddingGenerator _embedder;
    private DateTime _eventTime = new(2026, 11, 12, 1, 0, 0);

    public Enrichment_Tests()
    {
        _model = GetRequiredService<ScriptedEnrichmentChatClient>();
        _embedder = GetRequiredService<FakeEmbeddingGenerator>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" }
        });
        GetRequiredService<IDocumentAppService>().GetReadScopeAsync()
            .Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
    }

    [Fact]
    public async Task A_profile_with_enrichment_puts_keywords_and_questions_on_the_chunks_and_records_the_cost()
    {
        await SetProfileAsync(IndexEnrichment.Keywords | IndexEnrichment.Questions);

        await IndexAsync();

        var chunks = await ChunksAsync();
        chunks.Count.ShouldBe(2);
        chunks.ShouldAllBe(c => c.Keywords == $"kw{c.Ordinal} term" && c.Questions == $"What does passage {c.Ordinal} say?");
        _model.Calls.Count.ShouldBe(1);
        _model.Calls[0].System.ShouldContain("Write them in Japanese");
        // The questions are embedded with the chunk; the keywords are not.
        _embedder.Inputs.ShouldContain(i => i.EndsWith("\nWhat does passage 1 say?") && !i.Contains("kw1"));

        var run = await LatestRunAsync();
        (run.LlmInputTokenCount, run.LlmOutputTokenCount, run.ErrorMessage).ShouldBe((100L, 50L, (string?)null));
        (await DocumentAsync()).ProfileStamp.ShouldBe("Heading:400:0:3");
    }

    [Fact]
    public async Task An_enrichment_keyword_finds_its_chunk_on_the_keyword_leg_but_is_not_in_the_hit()
    {
        await SetProfileAsync(IndexEnrichment.Keywords);
        await IndexAsync();

        var hits = await WithUnitOfWorkAsync(() => GetRequiredService<IChunkSearchIndex>()
            .SearchKeywordsAsync("kw1", new ChunkSearchScope(true, [], null), 10));
        var chunk = (await ChunksAsync()).Single(c => c.Ordinal == 1);
        hits.ShouldHaveSingleItem().ChunkId.ShouldBe(chunk.Id);

        var hit = (await GetRequiredService<IIndexSearchAppService>().SearchAsync(new IndexSearchInput { Query = "kw1" })).Hits[0];
        hit.Text.ShouldNotContain("kw1");
    }

    [Fact]
    public async Task A_failed_enrichment_still_indexes_the_document_and_says_so()
    {
        await SetProfileAsync(IndexEnrichment.Keywords);
        _model.FailOnCall = 1;

        await IndexAsync();

        (await ChunksAsync()).ShouldAllBe(c => c.Keywords == null);
        var run = await LatestRunAsync();
        run.Status.ShouldBe(IndexRunStatus.Succeeded);
        run.ErrorMessage.ShouldNotBeNull().ShouldContain("2 of 2");
        (await DocumentAsync()).Status.ShouldBe(IndexedDocumentStatus.Indexed);
    }

    [Fact]
    public async Task Turning_enrichment_on_rechunks_the_document_on_its_next_sync()
    {
        await IndexAsync();
        _model.Calls.ShouldBeEmpty();

        await SetProfileAsync(IndexEnrichment.Questions);
        await IndexAsync();

        _model.Calls.Count.ShouldBe(1);
        (await ChunksAsync()).ShouldAllBe(c => c.Questions != null && c.Keywords == null);
    }

    [Fact]
    public async Task The_estimate_counts_the_enrichment_calls_a_change_would_make()
    {
        await IndexAsync();
        var profiles = GetRequiredService<IIndexProfileAppService>();

        (await profiles.GetListAsync()).ChatModelAvailable.ShouldBeTrue();
        var estimate = await profiles.GetRebuildEstimateAsync(new IndexProfileInput { DocumentTypeId = LeaseType, Enrichment = IndexEnrichment.Keywords });

        estimate.DocumentCount.ShouldBe(1);
        estimate.EnrichmentCallCount.ShouldBe(1);
        (await profiles.GetRebuildEstimateAsync(new IndexProfileInput { DocumentTypeId = LeaseType, MaxTokensPerChunk = 200 }))
            .EnrichmentCallCount.ShouldBe(0);
    }

    private Task SetProfileAsync(IndexEnrichment enrichment)
        => GetRequiredService<IIndexProfileAppService>().SetAsync(new IndexProfileInput { DocumentTypeId = LeaseType, Enrichment = enrichment });

    private async Task IndexAsync()
    {
        GetRequiredService<IDocumentIntegrationService>().FindReadyAsync(LeaseDoc).Returns(new DocumentIntegrationDto
        {
            Id = LeaseDoc,
            DocumentTypeId = LeaseType,
            Title = "賃貸借契約書",
            Language = "ja",
            Markdown = Lease
        });
        _eventTime = _eventTime.AddMinutes(1);
        await GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = LeaseDoc,
            Kind = IndexDocumentJobKind.Sync,
            Trigger = IndexRunTrigger.DocumentReady,
            EventTime = _eventTime
        });
    }

    private Task<List<Chunk>> ChunksAsync()
        => WithUnitOfWorkAsync(async () => (await GetRequiredService<IChunkRepository>().GetListAsync()).OrderBy(c => c.Ordinal).ToList());

    private Task<IndexedDocument> DocumentAsync()
        => WithUnitOfWorkAsync(() => GetRequiredService<IRepository<IndexedDocument, Guid>>().GetAsync(LeaseDoc));

    private Task<IndexRun> LatestRunAsync()
        => WithUnitOfWorkAsync(async () => (await GetRequiredService<IRepository<IndexRun, Guid>>().GetListAsync())
            .OrderByDescending(r => r.StartedTime).First());
}

/// <summary>The ingestion host for tests, plus an enrichment model registered under its key, as a host would.</summary>
[DependsOn(typeof(VaultIndexIngestionTestModule))]
public class VaultIndexEnrichmentTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var model = new ScriptedEnrichmentChatClient();
        context.Services.AddSingleton(model);
        context.Services.AddKeyedSingleton<IChatClient>(VaultIndexConsts.ChatClientKey, model);
    }
}

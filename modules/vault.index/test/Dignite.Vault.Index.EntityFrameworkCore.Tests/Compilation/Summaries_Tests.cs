using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Mcp.Search;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Index.Compilation;

/// <summary>
/// Section and document summaries through the real jobs on SQLite, with a scripted model registered as the host would:
/// a sync queues the compilation, the compile job writes the artifact, the outline serves it only while it matches the
/// chunks, and the profile switch adds and removes it.
/// </summary>
public class Summaries_Tests : VaultIndexTestBase<VaultIndexSummaryTestModule>
{
    private const string Lease = """
        # 賃貸借契約書

        本契約は、甲と乙の間で締結される。

        ## 第1条（目的）

        甲は乙に物件を賃貸する。

        ## 第2条（賃料）

        乙は毎月末日までに賃料を支払うものとする。
        """;

    private static readonly Guid LeaseType = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid OtherType = Guid.Parse("a0000000-0000-0000-0000-00000000000b");
    private static readonly Guid LeaseDoc = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private readonly ScriptedSummaryChatClient _model;
    private readonly IBackgroundJobManager _jobs;
    private DateTime _eventTime = new(2026, 11, 13, 1, 0, 0);

    public Summaries_Tests()
    {
        _model = GetRequiredService<ScriptedSummaryChatClient>();
        _jobs = GetRequiredService<IBackgroundJobManager>();
        GetRequiredService<IDocumentTypeAppService>().GetVisibleSummariesAsync().Returns(_ => new List<DocumentTypeSummaryDto>
        {
            new() { Id = LeaseType, TypeCode = "lease", DisplayName = "Lease" },
            new() { Id = OtherType, TypeCode = "other", DisplayName = "Other" }
        });
        GetRequiredService<IDocumentAppService>().GetReadScopeAsync()
            .Returns(new DocumentReadScopeDto { Version = 1, IsUnrestricted = true });
    }

    [Fact]
    public async Task A_sync_queues_the_compilation_and_the_outline_serves_what_it_wrote()
    {
        await SetProfileAsync(IndexCompilation.Summaries);

        await IndexAsync(Lease);
        CompileJobsQueued().ShouldBe(1);
        (await OutlineAsync()).DocumentSummary.ShouldBeNull();

        await CompileAsync();

        var outline = await OutlineAsync();
        outline.DocumentSummary.ShouldBe("the whole document");
        outline.Nodes.Select(n => (n.Title, n.Summary)).ShouldBe(
        [
            ("賃貸借契約書", "about 賃貸借契約書"),
            ("第1条（目的）", "about 賃貸借契約書 > 第1条（目的）"),
            ("第2条（賃料）", "about 賃貸借契約書 > 第2条（賃料）")
        ]);
        _model.Calls.ShouldAllBe(c => c.System.Contains("Write the summaries in Japanese"));

        var artifact = await SummariesAsync();
        artifact.ShouldNotBeNull();
        (artifact.Level, artifact.DocumentTypeId, artifact.InputTokenCount, artifact.OutputTokenCount, artifact.Note)
            .ShouldBe((ArtifactLevel.Document, LeaseType, 300L, 150L, (string?)null));
    }

    [Fact]
    public async Task The_mcp_outline_wraps_the_summaries_as_untrusted_text()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await IndexAsync(Lease);
        await CompileAsync();

        var outline = await IndexSearchTools.OutlineAsync(LeaseDoc.ToString(), GetRequiredService<IIndexSearchAppService>());

        outline.DocumentSummary.ShouldBe("<field>\nthe whole document\n</field>");
        outline.Nodes[0].Summary.ShouldBe("<field>\nabout 賃貸借契約書\n</field>");
    }

    [Fact]
    public async Task An_unchanged_document_is_not_compiled_again()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await IndexAsync(Lease);
        await CompileAsync();
        var calls = _model.Calls.Count;

        await IndexAsync(Lease);
        await CompileAsync();

        _model.Calls.Count.ShouldBe(calls);
        await CompileAsync(force: true);
        _model.Calls.Count.ShouldBe(calls * 2);
    }

    [Fact]
    public async Task Summaries_of_other_chunks_are_not_served_until_they_are_compiled_again()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await IndexAsync(Lease);
        await CompileAsync();

        // A disabled chunk changes what the summaries are built from, and queues their compilation.
        _jobs.ClearReceivedCalls();
        var chunk = (await ChunksAsync()).Single(c => c.HeadingPath!.Contains("第2条"));
        await GetRequiredService<IIndexChunkAppService>().DisableAsync(chunk.Id);
        CompileJobsQueued().ShouldBe(1);
        (await OutlineAsync()).DocumentSummary.ShouldBeNull();

        await CompileAsync();
        var outline = await OutlineAsync();
        outline.DocumentSummary.ShouldNotBeNull();
        outline.Nodes.Count.ShouldBe(2);

        // New text: the old summaries describe something else.
        await IndexAsync(Lease.Replace("末日", "25日"));
        (await OutlineAsync()).DocumentSummary.ShouldBeNull();
    }

    [Fact]
    public async Task Turning_summaries_off_removes_them_on_the_next_sync()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await IndexAsync(Lease);
        await CompileAsync();

        await SetProfileAsync(IndexCompilation.None);
        _jobs.ClearReceivedCalls();
        await IndexAsync(Lease);

        (await SummariesAsync()).ShouldBeNull();
        CompileJobsQueued().ShouldBe(0);
    }

    [Fact]
    public async Task Reclassified_with_the_same_text_the_summaries_move_with_their_document()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await GetRequiredService<IIndexProfileAppService>().SetAsync(new IndexProfileInput { DocumentTypeId = OtherType, Compilation = IndexCompilation.Summaries });
        await IndexAsync(Lease);
        await CompileAsync();
        var calls = _model.Calls.Count;

        await IndexAsync(Lease, OtherType);
        await CompileAsync();

        _model.Calls.Count.ShouldBe(calls);
        (await SummariesAsync())!.DocumentTypeId.ShouldBe(OtherType);
    }

    [Fact]
    public async Task A_document_no_longer_in_the_index_is_not_compiled()
    {
        await SetProfileAsync(IndexCompilation.Summaries);

        await CompileAsync();

        _model.Calls.ShouldBeEmpty();
        (await SummariesAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_document_removed_while_it_compiles_gets_no_summaries()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await IndexAsync(Lease);
        _model.DuringCall = () => LifecycleAsync(IndexDocumentJobKind.Remove);

        await CompileAsync();

        _model.Calls.ShouldNotBeEmpty();
        (await SummariesAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_document_out_of_retrieval_is_not_compiled()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await IndexAsync(Lease);
        await LifecycleAsync(IndexDocumentJobKind.Archive);

        await CompileAsync();

        _model.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_every_model_call_fails_the_job_throws_for_a_retry_and_writes_nothing()
    {
        await SetProfileAsync(IndexCompilation.Summaries);
        await IndexAsync(Lease);
        _model.FailOnCalls = [1, 2, 3];

        await Should.ThrowAsync<Exception>(() => CompileAsync());

        (await SummariesAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task The_estimate_counts_the_documents_whose_summaries_would_be_compiled()
    {
        await IndexAsync(Lease);
        var profiles = GetRequiredService<IIndexProfileAppService>();
        var input = new IndexProfileInput { DocumentTypeId = LeaseType, Compilation = IndexCompilation.Summaries };

        (await profiles.GetRebuildEstimateAsync(input)).SummaryDocumentCount.ShouldBe(1);
        (await profiles.GetRebuildEstimateAsync(new IndexProfileInput { DocumentTypeId = LeaseType })).SummaryDocumentCount.ShouldBe(0);

        await profiles.SetAsync(input);
        await IndexAsync(Lease);
        await CompileAsync();
        (await profiles.GetRebuildEstimateAsync(input)).SummaryDocumentCount.ShouldBe(0);
        (await profiles.GetListAsync()).Items.Single(r => r.DocumentTypeCode == "lease").Compilation.ShouldBe(IndexCompilation.Summaries);
    }

    private Task SetProfileAsync(IndexCompilation compilation)
        => GetRequiredService<IIndexProfileAppService>().SetAsync(new IndexProfileInput { DocumentTypeId = LeaseType, Compilation = compilation });

    private async Task IndexAsync(string markdown, Guid? typeId = null)
    {
        GetRequiredService<IDocumentIntegrationService>().FindReadyAsync(LeaseDoc).Returns(new DocumentIntegrationDto
        {
            Id = LeaseDoc,
            DocumentTypeId = typeId ?? LeaseType,
            Title = "賃貸借契約書",
            Language = "ja",
            Markdown = markdown
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

    private Task LifecycleAsync(IndexDocumentJobKind kind)
    {
        _eventTime = _eventTime.AddMinutes(1);
        return GetRequiredService<IndexDocumentJob>().ExecuteAsync(new IndexDocumentJobArgs
        {
            DocumentId = LeaseDoc,
            Kind = kind,
            EventTime = _eventTime
        });
    }

    private Task CompileAsync(bool force = false)
        => GetRequiredService<IndexCompileJob>().ExecuteAsync(new IndexCompileJobArgs { DocumentId = LeaseDoc, Force = force });

    private int CompileJobsQueued()
        => _jobs.ReceivedCalls().Count(c => c.GetArguments().FirstOrDefault() is IndexCompileJobArgs args && args.DocumentId == LeaseDoc);

    private Task<DocumentOutlineDto> OutlineAsync()
        => GetRequiredService<IIndexSearchAppService>().GetOutlineAsync(LeaseDoc);

    private Task<Artifact?> SummariesAsync()
        => WithUnitOfWorkAsync(() => GetRequiredService<IRepository<Artifact, Guid>>()
            .FirstOrDefaultAsync(a => a.DocumentId == LeaseDoc && a.TemplateName == ArtifactTemplateNames.Summaries));

    private Task<List<Chunk>> ChunksAsync()
        => WithUnitOfWorkAsync(async () => (await GetRequiredService<IChunkRepository>().GetListAsync()).OrderBy(c => c.Ordinal).ToList());
}

/// <summary>The ingestion host for tests, plus a summary model registered under Index's chat key, as a host would.</summary>
[DependsOn(typeof(VaultIndexIngestionTestModule))]
public class VaultIndexSummaryTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var model = new ScriptedSummaryChatClient();
        context.Services.AddSingleton(model);
        context.Services.AddKeyedSingleton<IChatClient>(VaultIndexConsts.ChatClientKey, model);
    }
}

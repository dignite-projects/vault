using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentPipelineRunAccessorTestModule : AbpModule
{
}

public class DocumentPipelineRunAccessorTests
    : VaultExtractApplicationTestBase<DocumentPipelineRunAccessorTestModule>
{
    private readonly DocumentPipelineRunAccessor _accessor;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentPipelineRunAccessorTests()
    {
        _accessor = GetRequiredService<DocumentPipelineRunAccessor>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task BeginOrStart_Uses_Pending_Run_For_Expected_Pipeline_When_JobArgs_RunId_Belongs_To_Another_Pipeline()
    {
        var document = CreateDocument();
        var textExtractionRun = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Parse);
        var classificationRun = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Classification);

        var actualRun = await _accessor.BeginOrStartAsync(
            document,
            textExtractionRun.Id,
            VaultExtractPipelines.Classification);

        actualRun.ShouldBe(classificationRun);
        classificationRun.Status.ShouldBe(PipelineRunStatus.Running);
        textExtractionRun.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    [Fact]
    public async Task BeginOrStart_Creates_Replacement_Run_With_JobArgs_RunId_When_Run_Is_Missing()
    {
        var document = CreateDocument();
        var missingRunId = Guid.NewGuid();

        var actualRun = (await _accessor.BeginOrStartAsync(
            document,
            missingRunId,
            VaultExtractPipelines.Classification)).ShouldNotBeNull();

        actualRun.Id.ShouldBe(missingRunId);
        actualRun.PipelineCode.ShouldBe(VaultExtractPipelines.Classification);
        actualRun.Status.ShouldBe(PipelineRunStatus.Running);
    }

    [Fact]
    public async Task A_redelivered_job_does_not_execute_a_run_that_has_ended()
    {
        var document = CreateDocument();
        var run = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Parse);
        await _pipelineRunManager.BeginAsync(document, run);
        await _pipelineRunManager.CompleteAsync(document, run);

        (await _accessor.BeginOrStartAsync(document, run.Id, VaultExtractPipelines.Parse)).ShouldBeNull();

        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    [Fact]
    public async Task A_queued_run_a_later_attempt_superseded_is_skipped_instead_of_executed()
    {
        var document = CreateDocument();
        var first = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Classification);
        var second = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Classification);

        (await _accessor.BeginOrStartAsync(document, first.Id, VaultExtractPipelines.Classification)).ShouldBeNull();

        first.Status.ShouldBe(PipelineRunStatus.Skipped);
        first.StatusMessage.ShouldBe("Superseded by attempt 2.");
        second.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    /// <summary>
    /// The operator retried a failed parse while ABP still held the failed job for its own retry. Executing both, the
    /// one finishing second failed on the Markdown the first wrote and left the document Failed.
    /// </summary>
    [Fact]
    public async Task The_retry_of_a_failed_job_does_not_execute_once_an_operator_retry_queued_a_later_attempt()
    {
        var document = CreateDocument();
        var failed = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Parse);
        await _pipelineRunManager.BeginAsync(document, failed);
        await _pipelineRunManager.FailAsync(document, failed, "provider timeout");
        var retry = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Parse);

        (await _accessor.BeginOrStartAsync(document, failed.Id, VaultExtractPipelines.Parse)).ShouldBeNull();

        failed.Status.ShouldBe(PipelineRunStatus.Failed);
        failed.StatusMessage.ShouldBe("provider timeout");
        retry.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    [Fact]
    public async Task The_retry_of_the_latest_failed_run_executes_it_again()
    {
        var document = CreateDocument();
        var run = await _pipelineRunManager.QueueAsync(document, VaultExtractPipelines.Parse);
        await _pipelineRunManager.BeginAsync(document, run);
        await _pipelineRunManager.FailAsync(document, run, "provider timeout");

        (await _accessor.BeginOrStartAsync(document, run.Id, VaultExtractPipelines.Parse)).ShouldBe(run);

        run.Status.ShouldBe(PipelineRunStatus.Running);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: Guid.NewGuid(),
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}

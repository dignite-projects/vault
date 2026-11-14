using System.Linq;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Dignite.Vault.Extract.Documents.Pipelines.Segmentation;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// The pipeline's slow stages run on background job workers of their own. ABP resolves a dedicated worker's job types
/// only when the workers start, so an args type with no job behind it would stop the host there rather than here.
/// </summary>
public class BackgroundJobWorkers_Tests : VaultExtractApplicationTestBase<VaultExtractApplicationTestModule>
{
    [Fact]
    public void Each_slow_stage_has_a_worker_of_its_own()
    {
        var workers = GetRequiredService<IOptions<AbpBackgroundJobWorkerOptions>>().Value.WorkerConfigurations;

        workers.Select(w => (w.LockName, Types: string.Join(", ", w.JobArgsTypes.Select(t => t.Name)))).ShouldBe(
        [
            ("VaultExtract:Parse", nameof(DocumentParseJobArgs)),
            ("VaultExtract:Classification",
                $"{nameof(DocumentClassificationJobArgs)}, {nameof(DocumentSegmentationJobArgs)}, {nameof(DocumentCabinetSuggestionJobArgs)}"),
            ("VaultExtract:FieldExtraction", nameof(DocumentFieldExtractionJobArgs))
        ]);
    }

    [Fact]
    public void Every_dedicated_job_type_is_a_registered_job()
    {
        var workers = GetRequiredService<IOptions<AbpBackgroundJobWorkerOptions>>().Value.WorkerConfigurations;
        var jobs = GetRequiredService<IOptions<AbpBackgroundJobOptions>>().Value;

        foreach (var type in workers.SelectMany(w => w.JobArgsTypes))
        {
            jobs.GetJob(type).ShouldNotBeNull();
        }
    }

    /// <summary>
    /// A queued job is stored under its name, and ABP abandons a job whose name it no longer knows. ABP reads the name
    /// from the args type's <see cref="BackgroundJobNameAttribute"/> and falls back to the args type's full name, which a
    /// rename or a namespace move changes.
    /// </summary>
    [Fact]
    public void Every_job_is_stored_under_a_name_of_its_own()
    {
        var jobs = GetRequiredService<IOptions<AbpBackgroundJobOptions>>().Value.GetJobs()
            .Where(j => j.ArgsType.Assembly == typeof(VaultExtractApplicationModule).Assembly)
            .ToList();

        jobs.Count.ShouldBe(10);
        jobs.ShouldAllBe(j => j.JobName.StartsWith("VaultExtract.") && j.JobName != j.ArgsType.FullName);
    }
}

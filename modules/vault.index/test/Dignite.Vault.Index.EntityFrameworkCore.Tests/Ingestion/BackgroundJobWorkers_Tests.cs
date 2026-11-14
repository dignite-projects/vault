using System.Linq;
using Dignite.Vault.Index.Compilation;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// Index's jobs run on background job workers of their own. ABP resolves a dedicated worker's job types only when the
/// workers start, so an args type with no job behind it would stop the host there rather than here.
/// </summary>
public class BackgroundJobWorkers_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    [Fact]
    public void Indexing_and_compilation_have_workers_of_their_own()
    {
        var workers = GetRequiredService<IOptions<AbpBackgroundJobWorkerOptions>>().Value.WorkerConfigurations;
        var jobs = GetRequiredService<IOptions<AbpBackgroundJobOptions>>().Value;

        workers.Select(w => (w.LockName, Types: string.Join(", ", w.JobArgsTypes.Select(t => t.Name)))).ShouldBe(
        [
            ("VaultIndex:Documents", $"{nameof(IndexDocumentJobArgs)}, {nameof(IndexBackfillJobArgs)}"),
            ("VaultIndex:Compile", nameof(IndexCompileJobArgs))
        ]);
        foreach (var type in workers.SelectMany(w => w.JobArgsTypes))
        {
            jobs.GetJob(type).JobName.ShouldStartWith("VaultIndex.");
        }
    }
}

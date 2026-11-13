using System;
using Volo.Abp.BackgroundJobs;

namespace Dignite.Vault.Index.Compilation;

/// <summary>The job name is a serialized string: renaming it orphans queued jobs.</summary>
[BackgroundJobName("VaultIndex.Compile")]
public class IndexCompileJobArgs
{
    public Guid DocumentId { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>Compile even when the artifacts are fresh: an operator's recompile.</summary>
    public bool Force { get; set; }
}

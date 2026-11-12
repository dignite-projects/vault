using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Runs;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// Index's own DbContext, provider-neutral (tests run it on SQLite). Its connection string is
/// <see cref="VaultIndexDbProperties.ConnectionStringName"/>, so a host can put Index in another database than
/// Extract; with none configured, ABP resolves it to <c>Default</c>.
/// </summary>
[ConnectionStringName(VaultIndexDbProperties.ConnectionStringName)]
public class VaultIndexDbContext : AbpDbContext<VaultIndexDbContext>, IVaultIndexDbContext
{
    public DbSet<IndexProfile> IndexProfiles { get; set; } = default!;
    public DbSet<IndexedDocument> IndexedDocuments { get; set; } = default!;
    public DbSet<Chunk> Chunks { get; set; } = default!;
    public DbSet<Artifact> Artifacts { get; set; } = default!;
    public DbSet<IndexRun> IndexRuns { get; set; } = default!;
    public DbSet<IndexGeneration> IndexGenerations { get; set; } = default!;

    public VaultIndexDbContext(DbContextOptions<VaultIndexDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ConfigureVaultIndex();
    }
}

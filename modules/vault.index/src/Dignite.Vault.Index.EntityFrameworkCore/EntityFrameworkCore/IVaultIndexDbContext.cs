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

[ConnectionStringName(VaultIndexDbProperties.ConnectionStringName)]
public interface IVaultIndexDbContext : IEfCoreDbContext
{
    DbSet<IndexProfile> IndexProfiles { get; }
    DbSet<IndexedDocument> IndexedDocuments { get; }
    DbSet<Chunk> Chunks { get; }
    DbSet<Artifact> Artifacts { get; }
    DbSet<IndexRun> IndexRuns { get; }
    DbSet<IndexGeneration> IndexGenerations { get; }
}

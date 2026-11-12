using Dignite.Vault.Index;
using Dignite.Vault.Index.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Host.Data;

/// <summary>
/// Migrations for Vault Index, kept apart from <see cref="VaultExtractHostDbContext"/> because Index has its own
/// connection string (<see cref="VaultIndexDbProperties.ConnectionStringName"/>): its vector and full-text storage
/// needs SQL Server 2025 with Full-Text Search or Azure SQL, which the database behind <c>Default</c> need not be.
/// With no <c>VaultIndex</c> connection string configured, ABP resolves it to <c>Default</c> and both sets of
/// tables land in one database. Like the Extract context, this one only builds and applies migrations; the module
/// reads and writes through its own <see cref="VaultIndexDbContext"/>.
/// </summary>
[ConnectionStringName(VaultIndexDbProperties.ConnectionStringName)]
public class VaultIndexHostDbContext : AbpDbContext<VaultIndexHostDbContext>
{
    /// <summary>
    /// The dimension of Index's vector column in this host's schema. Changing it means a new migration, and an embedding
    /// model that returns vectors of the new length.
    /// </summary>
    public const int VectorDimensions = 1024;

    public VaultIndexHostDbContext(DbContextOptions<VaultIndexHostDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ConfigureVaultIndex();
        builder.ConfigureVaultIndexSqlServer(VectorDimensions);
    }
}

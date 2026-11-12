using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// The SQL Server provider's own tables, on the same connection string as <see cref="VaultIndexDbContext"/>. ABP
/// enlists every DbContext of one connection string in the unit of work's single transaction, so a document's chunk
/// rows and its vectors are replaced together or not at all.
/// </summary>
[ConnectionStringName(VaultIndexDbProperties.ConnectionStringName)]
public class VaultIndexSqlServerDbContext : AbpDbContext<VaultIndexSqlServerDbContext>
{
    public DbSet<ChunkVectorRecord> ChunkVectors { get; set; } = default!;

    public VaultIndexSqlServerDbContext(DbContextOptions<VaultIndexSqlServerDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var dimensions = LazyServiceProvider?.LazyGetService<IOptions<VaultIndexSqlServerOptions>>()?.Value.VectorDimensions
                         ?? VaultIndexSqlServerOptions.DefaultVectorDimensions;
        builder.ConfigureVaultIndexSqlServer(dimensions);
    }
}

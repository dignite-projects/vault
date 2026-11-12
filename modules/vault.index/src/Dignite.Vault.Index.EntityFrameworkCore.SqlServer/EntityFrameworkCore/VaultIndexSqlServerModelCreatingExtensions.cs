using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace Dignite.Vault.Index.EntityFrameworkCore;

public static class VaultIndexSqlServerModelCreatingExtensions
{
    /// <summary>
    /// The SQL Server storage tables. A host's Index migrations context calls this next to
    /// <c>ConfigureVaultIndex()</c>, with the same <paramref name="vectorDimensions"/> it configures at run time.
    /// </summary>
    public static void ConfigureVaultIndexSqlServer(this ModelBuilder builder, int vectorDimensions)
    {
        Check.NotNull(builder, nameof(builder));
        Check.Range(vectorDimensions, nameof(vectorDimensions), 1, 1998);

        builder.Entity<ChunkVectorRecord>(b =>
        {
            b.ToTable(VaultIndexDbProperties.DbTablePrefix + "IndexChunkVectors", VaultIndexDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.HasKey(x => new { x.GenerationId, x.ChunkId }).IsClustered(false);
            b.Property(x => x.Embedding).HasColumnType($"vector({vectorDimensions})").IsRequired();

            // The storage PoC: an exact vector search scans the rows its read-scope filter leaves, so they are stored
            // together. Clustering by (tenant, generation, type) turns a type-scoped search into one range scan, and
            // keeps the vectors out of the chunk table, whose Guid key the full-text index needs.
            b.HasIndex(x => new { x.TenantId, x.GenerationId, x.DocumentTypeId, x.ChunkId }).IsClustered();
            b.HasIndex(x => x.DocumentId);
        });
    }
}

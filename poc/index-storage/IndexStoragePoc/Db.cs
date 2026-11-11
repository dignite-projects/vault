using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace IndexStoragePoc;

/// <summary>The chunk row as the design describes it: tenant, document, type and owner for filtering, the text, one vector.</summary>
public class Chunk
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid DocumentTypeId { get; set; }
    public Guid? OwnerId { get; set; }
    public int Ordinal { get; set; }
    public string Text { get; set; } = "";

    [Column(TypeName = "vector(1024)")]
    public SqlVector<float> Embedding { get; set; }
}

public class IndexRun
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public int ChunkCount { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
}

public class DocumentField
{
    public Guid? TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid DocumentTypeId { get; set; }
    public string Name { get; set; } = "";
    public string? StringValue { get; set; }
    public decimal? NumberValue { get; set; }
}

/// <summary>A ranked full-text hit from CONTAINSTABLE / FREETEXTTABLE, read through SqlQuery.</summary>
public class KeywordHit
{
    public Guid ChunkId { get; set; }
    public int Rank { get; set; }
}

/// <summary>
/// Stands in for Index's own DbContext. The tenant filter is a global query filter the way ABP's IMultiTenant filter is,
/// so every query below shows whether a shape keeps it.
/// </summary>
public sealed class PocDbContext(DbContextOptions<PocDbContext> options) : DbContext(options)
{
    public Guid? CurrentTenantId { get; set; }

    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<IndexRun> Runs => Set<IndexRun>();
    public DbSet<DocumentField> DocumentFields => Set<DocumentField>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Chunk>(e =>
        {
            e.ToTable("VaultIndexChunks");
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
        b.Entity<IndexRun>(e =>
        {
            e.ToTable("VaultIndexRuns");
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
        b.Entity<DocumentField>(e =>
        {
            e.ToTable("VaultIndexDocumentFields");
            e.HasKey(x => new { x.DocumentId, x.Name });
            e.Property(x => x.NumberValue).HasPrecision(28, 8);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }

    public static PocDbContext Create(string connectionString, Guid? tenantId, bool log = false)
    {
        var builder = new DbContextOptionsBuilder<PocDbContext>()
            .UseSqlServer(connectionString, o => o.CommandTimeout(600));
        if (log)
        {
            builder.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information);
        }

        return new PocDbContext(builder.Options) { CurrentTenantId = tenantId };
    }
}

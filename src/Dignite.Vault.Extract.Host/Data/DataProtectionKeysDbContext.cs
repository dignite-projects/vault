using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Host.Data;

/// <summary>
/// The ASP.NET Core Data Protection key ring, in the host's own database, so every host instance protects and unprotects
/// with the same keys: login and external-login cookies, antiforgery tokens, and the signed Blob Management upload links
/// of a submission. Kept per machine, as before, a link issued by one instance is refused by another.
/// <para>
/// A plain <see cref="DbContext"/>, not an ABP one: Data Protection reads and writes it from its own scope, outside any
/// unit of work and before a tenant is known. Its one table is created by <see cref="VaultExtractHostDbContext"/>'s
/// migrations, which map it the same way (<see cref="ConfigureDataProtectionKeys"/>).
/// </para>
/// </summary>
public class DataProtectionKeysDbContext : DbContext, IDataProtectionKeyContext
{
    public const string TableName = VaultExtractHostDbContext.DbTablePrefix + "DataProtectionKeys";

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = default!;

    public DataProtectionKeysDbContext(DbContextOptions<DataProtectionKeysDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureDataProtectionKeys(modelBuilder);
    }

    /// <summary>The key table's mapping, shared with the migration context so the two cannot drift.</summary>
    public static void ConfigureDataProtectionKeys(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DataProtectionKey>(b => b.ToTable(TableName));
    }
}

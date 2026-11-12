using System;
using System.Threading;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// Runs only when <c>VAULT_INDEX_TEST_SQLSERVER</c> holds a connection string to a SQL Server 2025 (or Azure SQL) with
/// Full-Text Search, such as the storage PoC's container; skipped otherwise, so the suite stays runnable without one.
/// The retrieval legs use vector distance and full-text ranking, which SQLite does not have.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SqlServerTestDatabase.ServerConnectionString))
        {
            Skip = "Set VAULT_INDEX_TEST_SQLSERVER to a SQL Server 2025 connection string to run.";
        }
    }
}

/// <summary>A throwaway database on the configured server, created once per test run with the Index schema and full-text index.</summary>
public static class SqlServerTestDatabase
{
    public const int Dimensions = 4;

    private const string DatabaseName = "VaultIndexSqlServerTests";

    private static readonly Lazy<string> Created = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable("VAULT_INDEX_TEST_SQLSERVER");

    public static string ConnectionString => Created.Value;

    private static string Create()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = DatabaseName };
        var connectionString = builder.ConnectionString;

        using (var context = new SchemaContext(new DbContextOptionsBuilder<SchemaContext>().UseSqlServer(connectionString).Options))
        {
            context.Database.EnsureDeleted();
            var creator = context.GetService<IRelationalDatabaseCreator>();
            creator.Create();
            creator.CreateTables();
        }

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        foreach (var sql in new[]
                 {
                     "CREATE FULLTEXT CATALOG VaultIndexCatalog",
                     "CREATE FULLTEXT INDEX ON VaultIndexChunks ([Text] LANGUAGE 1041) KEY INDEX PK_VaultIndexChunks ON VaultIndexCatalog WITH CHANGE_TRACKING AUTO"
                 })
        {
            using var command = new SqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }

        return connectionString;
    }

    /// <summary>Both models in one context, the way a host's migrations context combines them.</summary>
    private sealed class SchemaContext(DbContextOptions<SchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.ConfigureVaultIndex();
            builder.ConfigureVaultIndexSqlServer(Dimensions);
        }
    }
}

[DependsOn(
    typeof(VaultIndexTestBaseModule),
    typeof(VaultIndexEntityFrameworkCoreSqlServerModule))]
public class VaultIndexSqlServerTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        if (string.IsNullOrWhiteSpace(SqlServerTestDatabase.ServerConnectionString))
        {
            return;
        }

        var connectionString = SqlServerTestDatabase.ConnectionString;
        Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = connectionString;
            options.ConnectionStrings[VaultIndexDbProperties.ConnectionStringName] = connectionString;
        });
        Configure<AbpDbContextOptions>(options => options.UseSqlServer());
        Configure<VaultIndexSqlServerOptions>(options => options.VectorDimensions = SqlServerTestDatabase.Dimensions);
    }
}

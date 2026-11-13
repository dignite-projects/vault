using System;
using System.Threading;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
/// A throwaway database on the configured server, created once per test run with the Index schema and the full-text
/// index the host's migrations create. Each test class picks its database, so their vector dimensions can differ.
/// </summary>
public sealed class SqlServerTestDatabase
{
    /// <summary>The store's own tests: tiny hand-made vectors.</summary>
    public static readonly SqlServerTestDatabase Store = new("VaultIndexSqlServerTests", 4);

    /// <summary>The retrieval evaluation: real embeddings, of the dimension the host stores (1024 unless overridden).</summary>
    public static readonly SqlServerTestDatabase Evaluation = new(
        "VaultIndexEvaluation",
        int.TryParse(Environment.GetEnvironmentVariable("VAULT_INDEX_EVAL_DIMENSIONS"), out var dimensions) ? dimensions : 1024);

    private readonly Lazy<string> _created;

    private SqlServerTestDatabase(string name, int dimensions)
    {
        Name = name;
        Dimensions = dimensions;
        _created = new Lazy<string>(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable("VAULT_INDEX_TEST_SQLSERVER");

    public string Name { get; }

    public int Dimensions { get; }

    public string ConnectionString => _created.Value;

    private string Create()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = Name };
        var connectionString = builder.ConnectionString;

        using (var context = new SchemaContext(new DbContextOptionsBuilder<SchemaContext>().UseSqlServer(connectionString).Options, Dimensions))
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
                     // As the host's FullTextSearch and EnrichmentFullText migrations leave it.
                     "CREATE FULLTEXT INDEX ON VaultIndexChunks ([Text] LANGUAGE 1041, [Keywords] LANGUAGE 1041, [Questions] LANGUAGE 1041) KEY INDEX PK_VaultIndexChunks ON VaultIndexCatalog WITH CHANGE_TRACKING AUTO"
                 })
        {
            using var command = new SqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }

        return connectionString;
    }

    /// <summary>Both models in one context, the way a host's migrations context combines them.</summary>
    private sealed class SchemaContext(DbContextOptions<SchemaContext> options, int dimensions) : DbContext(options)
    {
        public int Dimensions { get; } = dimensions;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.ConfigureVaultIndex();
            builder.ConfigureVaultIndexSqlServer(Dimensions);
        }

        // One model per dimension: EF caches the model by context type unless told otherwise.
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.ReplaceService<IModelCacheKeyFactory, DimensionModelCacheKeyFactory>();
    }

    private sealed class DimensionModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context.GetType(), ((SchemaContext)context).Dimensions, designTime);
    }

    /// <summary>Applies a database's connection string and dimensions to an ABP test application.</summary>
    public void Configure(ServiceConfigurationContext context)
    {
        var connectionString = ConnectionString;
        context.Services.Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = connectionString;
            options.ConnectionStrings[VaultIndexDbProperties.ConnectionStringName] = connectionString;
        });
        context.Services.Configure<AbpDbContextOptions>(options => options.UseSqlServer());
        context.Services.Configure<VaultIndexSqlServerOptions>(options => options.VectorDimensions = Dimensions);
    }
}

[DependsOn(
    typeof(VaultIndexTestBaseModule),
    typeof(VaultIndexEntityFrameworkCoreSqlServerModule))]
public class VaultIndexSqlServerTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        if (!string.IsNullOrWhiteSpace(SqlServerTestDatabase.ServerConnectionString))
        {
            SqlServerTestDatabase.Store.Configure(context);
        }
    }
}

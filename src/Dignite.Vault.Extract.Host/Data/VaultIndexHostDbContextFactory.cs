using Dignite.Vault.Index;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dignite.Vault.Extract.Host.Data;

/// <summary>
/// Design time only (<c>dotnet ef migrations add ... --context VaultIndexHostDbContext --output-dir Migrations/VaultIndex</c>).
/// Resolves the connection string the way ABP does at run time: <c>VaultIndex</c>, else <c>Default</c>.
/// </summary>
public class VaultIndexHostDbContextFactory : IDesignTimeDbContextFactory<VaultIndexHostDbContext>
{
    public VaultIndexHostDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.secrets.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString(VaultIndexDbProperties.ConnectionStringName)
                               ?? configuration.GetConnectionString("Default");

        var builder = new DbContextOptionsBuilder<VaultIndexHostDbContext>()
            .UseSqlServer(connectionString);

        return new VaultIndexHostDbContext(builder.Options);
    }
}

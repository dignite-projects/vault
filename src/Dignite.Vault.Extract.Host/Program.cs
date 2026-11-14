using System;
using Dignite.Vault.Extract.Host.Data;
using Serilog;
using Serilog.Events;
using Volo.Abp.Data;

namespace Dignite.Vault.Extract.Host;

public class Program
{
    public async static Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Async(c => c.File("Logs/logs.txt"))
            .WriteTo.Async(c => c.Console())
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Host.AddAppSettingsSecretsJson();
            // appsettings.secrets.json is added after the environment and the command line, so it would override both.
            // Add them again: environment and command line win over every file, which is what a container, a deployment
            // or the Aspire AppHost (src/Dignite.Vault.AppHost, whose connection strings arrive as environment variables)
            // expects. Running the host on its own still reads the secrets file for whatever the environment leaves unset.
            builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
            builder.Host
                .UseAutofac()
                .UseSerilog((context, services, loggerConfiguration) =>
                {
                    if (IsMigrateDatabase(args))
                    {
                        loggerConfiguration
                            .MinimumLevel.Override("Volo.Abp", LogEventLevel.Warning)
                            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                            .WriteTo.Async(c => c.Console(standardErrorFromLevel: LogEventLevel.Error));
                    }
                    else
                    {
                        loggerConfiguration
                            .ReadFrom.Configuration(context.Configuration)
                            .ReadFrom.Services(services)
                            .WriteTo.Async(c => c.AbpStudio(services));
                    }
                });
            if (IsMigrateDatabase(args))
            {
                builder.Services.AddDataMigrationEnvironment();
            }
            await builder.AddApplicationAsync<VaultExtractHostModule>();
            var app = builder.Build();
            await app.InitializeApplicationAsync();

            if (IsMigrateDatabase(args))
            {
                await app.Services.GetRequiredService<VaultExtractHostDbMigrationService>().MigrateAsync();
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Migration completed.");
                Console.ForegroundColor = previous;
                return 0;
            }

            Log.Information("Starting Dignite.Vault.Extract.Host.");
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            if (ex is HostAbortedException)
            {
                throw;
            }

            Log.Fatal(ex, "Dignite.Vault.Extract.Host terminated unexpectedly!");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static bool IsMigrateDatabase(string[] args)
    {
        return args.Any(x => x.Contains("--migrate-database", StringComparison.OrdinalIgnoreCase));
    }
}

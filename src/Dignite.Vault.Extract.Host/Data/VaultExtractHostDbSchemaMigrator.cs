using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Host.Data;

public class VaultExtractHostDbSchemaMigrator : ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;

    public VaultExtractHostDbSchemaMigrator(
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task MigrateAsync()
    {
        /* We intentionally resolve DbContexts from IServiceProvider
         * to properly get the connection string of the current tenant
         * in the current scope.
         */

        await _serviceProvider
            .GetRequiredService<VaultExtractHostDbContext>()
            .Database
            .MigrateAsync();

        // Vault Index has its own connection string (falling back to Default), so its migrations are applied
        // through their own context, against whichever database that resolves to.
        await _serviceProvider
            .GetRequiredService<VaultIndexHostDbContext>()
            .Database
            .MigrateAsync();
    }
}

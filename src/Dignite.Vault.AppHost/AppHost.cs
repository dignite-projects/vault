// The local development environment, one command: SQL Server 2025 with full-text search for both databases, the
// schema migrations, the host and the Angular dev server, with the Aspire dashboard showing the host's traces and
// metrics. LLM providers and keys still come from the host's appsettings.secrets.json.
var builder = DistributedApplication.CreateBuilder(args);

// Vault Index stores vectors and searches full text, so its database needs SQL Server 2025 with full-text search;
// the official image has no full-text search. One persistent container keeps both databases between runs.
var sql = builder.AddSqlServer("sql")
    .WithDockerfile("../../etc/docker/sqlserver-fts")
    .WithDataVolume("dignite-vault-sql")
    .WithLifetime(ContainerLifetime.Persistent);
var extractDatabase = sql.AddDatabase("vault-extract-db", "VaultExtract");
var indexDatabase = sql.AddDatabase("vault-index-db", "VaultIndex");

// The host's --migrate-database run: migrations and seed data (the admin user, the OpenIddict clients) for both
// databases, then it exits.
var migrator = builder.AddProject<Projects.Dignite_Vault_Extract_Host>("migrator", launchProfileName: null)
    .WithArgs("--migrate-database")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithReference(extractDatabase, connectionName: "Default")
    .WithReference(indexDatabase, connectionName: "VaultIndex")
    .WaitFor(extractDatabase)
    .WaitFor(indexDatabase);

// Not proxied: the OpenIddict issuer, App:SelfUrl and the SPA's environment all name https://localhost:44348.
var host = builder.AddProject<Projects.Dignite_Vault_Extract_Host>("host")
    .WithReference(extractDatabase, connectionName: "Default")
    .WithReference(indexDatabase, connectionName: "VaultIndex")
    .WithEndpoint("https", endpoint => endpoint.IsProxied = false)
    .WaitForCompletion(migrator);

// npm installs once, at the repository root (a second install inside angular/ bundles a second Angular runtime), so
// Aspire only runs the start script. ng serve takes its port from PORT, so the endpoint sets it rather than inheriting
// whatever the shell has; the host's CORS and OpenIddict redirect URIs name http://localhost:4200.
builder.AddJavaScriptApp("angular", "../../angular", "start")
    .WithNpm(install: false)
    .WithHttpEndpoint(port: 4200, env: "PORT", isProxied: false)
    .WaitFor(host);

builder.Build().Run();

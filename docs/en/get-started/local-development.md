```json
//[doc-seo]
{
    "Description": "Run Dignite Vault Extract from source on a developer machine: prerequisites, LLM and database configuration, migrations, the Angular SPA, sidecars and proxy regeneration."
}
```

# Local Development

This guide runs the Vault Extract repository on a developer machine: the .NET host, the Angular operator UI and their database, either all together through the Aspire AppHost or one by one. To add the modules to an application of your own instead, see [Installation](installation.md).

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet) | 11.0 (`global.json` pins `11.0.100`, rolling forward to the latest feature band) | |
| [Node.js](https://nodejs.org) | `^22.22.3` or `^24.15.0` | Required by Angular 22 |
| SQL Server | 2025 | The AppHost runs one in a container. Running the host alone, LocalDB, Express and Developer all work; the committed `appsettings.json` points at `(LocalDb)\MSSQLLocalDB`. Vault Index's tables need 2025 for their `vector` column, and its keyword search needs full-text search, which LocalDB lacks; with an older SQL Server for Extract, point the `VaultIndex` connection string at a 2025 database ([Deployment](../deployment/deployment.md#connection-strings)) |
| An LLM provider | Any OpenAI-compatible endpoint | Mandatory; see [Backend Configuration](#backend-configuration). A local Ollama works |
| [ABP CLI](https://abp.io/docs/latest/cli) | Matching ABP 11.0 | `dotnet tool install -g Volo.Abp.Studio.Cli`; used for `abp install-libs` and proxy generation |
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | Any recent | For the AppHost's SQL Server container, and the optional PaddleOCR sidecar |

## Backend Configuration

Create `src/Dignite.Vault.Extract.Host/appsettings.secrets.json`. The host loads it after `appsettings.json` and `appsettings.{Environment}.json` (ABP's `AddAppSettingsSecretsJson()`), and environment variables and command-line arguments override it; the file is git-ignored, and `dotnet publish` leaves it out. Keep real credentials there or in environment variables, never in the committed `appsettings.json` or `appsettings.Development.json`.

An LLM provider is **required**. The host fails at startup while `Vault:Extract` is unconfigured, because classification and field extraction have no non-LLM fallback (the committed `appsettings.json` ships a `"YOUR_API_KEY"` placeholder, which the startup check rejects). A minimal configuration:

```json
{
  "ConnectionStrings": {
    "Default": "Server=YOUR_DB_SERVER;Database=Extract-Dev;User ID=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=true"
  },
  "StringEncryption": {
    "DefaultPassPhrase": "any-random-string-here"
  },
  "Vault": {
    "Extract": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "sk-...",
      "ChatModelId": "gpt-6-luna",
      "VisionOcrModelId": "gpt-6-luna",
      "ReasoningEffort": "None"
    }
  }
}
```

- With LocalDB, the committed connection string already works and you can leave `ConnectionStrings` out.
- The committed `Vault:Index` asks the same endpoint and key for `text-embedding-3-small`. On a provider without that model, set `Vault:Index:EmbeddingModelId` to one of its embedding models; see [Embedding model](../configuration/ai-provider.md#embedding-model-index).
- `VisionOcrModelId` is required because the host's default OCR provider is [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md); it must name a vision-capable model.
- `TitleGeneratorModelId` and `StructuredModelId` are optional and default to `ChatModelId`.
- `ReasoningEffort` is for a reasoning model such as `gpt-6-luna`; leave it out for a provider without reasoning. On SiliconFlow, set `"UseLegacyMaxTokens": true` instead.
- For Azure OpenAI, OpenRouter, Ollama and other providers, see [AI Provider](../configuration/ai-provider.md). For a zero-cost local option, run Ollama and point `Endpoint` at its `/v1` endpoint with any non-empty `ApiKey`.

> **OpenIddict certificates.** In Development mode temporary signing and encryption certificates are generated automatically; no `.pfx` file is needed.

## Running Everything with the Aspire AppHost

`src/Dignite.Vault.AppHost` starts the whole development environment with one command:

```bash
npm install
cd src/Dignite.Vault.Extract.Host && abp install-libs && cd ../..
dotnet run --project src/Dignite.Vault.AppHost
```

- **SQL Server 2025 with full-text search**, built from `etc/docker/sqlserver-fts` (the official image has no full-text search; the first run builds it, which takes a few minutes). The container is persistent and keeps its data in the `dignite-vault-sql` volume, with the databases `VaultExtract` and `VaultIndex`.
- **`migrator`**, the host run with `--migrate-database`, which migrates and seeds both databases and exits.
- **`host`**, once the migration has finished, on `https://localhost:44348`.
- **`angular`**, the SPA's `npm start`, on `http://localhost:4200`. The AppHost does not run `npm install`: install once at the repository root, as above.
- **The Aspire dashboard** on `https://localhost:17188` (the console prints the login link), with every resource's console output and the host's traces and metrics.

The SQL Server container outlives the AppHost so the next start skips its initialization; stop it with `docker stop` on the `sql-…` container, and delete the `dignite-vault-sql` volume to start from empty databases.

The AppHost hands the host its connection strings as environment variables, which override `appsettings.secrets.json`, so the host uses the container's databases whatever the secrets file says. The LLM settings still come from `appsettings.secrets.json` ([Backend Configuration](#backend-configuration)). To work against another database instead, run the host on its own as below.

## Running the Host Alone

Install the client-side libraries once after cloning, and again when they change:

```bash
cd src/Dignite.Vault.Extract.Host
abp install-libs
```

Create the database schema and seed the initial data. The command migrates, prints `Migration completed.` and exits; run it again after pulling a change that adds a migration:

```bash
cd src/Dignite.Vault.Extract.Host
dotnet run --migrate-database
```

Then start the host:

```bash
cd src/Dignite.Vault.Extract.Host
dotnet run
```

The API listens on `https://localhost:44348`; Swagger UI is at `https://localhost:44348/swagger`.

> In Development the seed also creates a **Sample Document** type in the Host layer, with one field of each field type and one processed sample document, so the field designer and the egress paths have something to show. It lives in the host project only; the modules ship no document types.

## Running the Angular UI

There are two Angular CLI workspaces: the host SPA in the repository-root `angular/`, and the `vault-extract` library in `modules/vault.extract/angular/`. The host compiles the library from source (`tsconfig.base.json` `paths`). Both are npm workspaces of the root `package.json`, so a single `npm install` at the repository root installs them together into one `node_modules`:

```bash
npm install
cd angular
npm start
```

> Do not run `npm install` inside a module's `angular/` folder on its own. A second `node_modules` there makes the host bundle a second copy of Angular for the library code, which breaks dependency injection at runtime only. CI fails the build when that happens (`npm run check:single-angular`).

The SPA is served at `http://localhost:4200`. The operator UI is under **Vault Extract** in the main menu (`/vault-extract/...`).

Default credentials, seeded by `--migrate-database`:

| Field | Value |
| --- | --- |
| Username | `admin` |
| Password | `1q2w3E*` |

## Optional Sidecars (Docker)

`docker-compose.yml` defines the PaddleOCR sidecar (port 8866: PP-StructureV3 on CPU), needed only when you switch the host to the [PaddleOCR](../text-extraction/ocr-paddleocr.md) provider:

```bash
docker compose --profile paddleocr up -d paddleocr
```

It answers `{"status":"success"}` on `http://localhost:8866/ping` once ready; its first start downloads about 600 MB of model weights. Stop it with `docker compose down`.

The OpenTelemetry dashboard comes with the AppHost. Running the host alone, point `OpenTelemetry:Otlp:Endpoint` at any OTLP backend; see [Observability](../deployment/observability.md).

## Regenerating the HTTP Client Proxies

After adding or changing an API endpoint, regenerate the Angular proxies. The host must be running:

```bash
cd modules/vault.extract/angular
npm run generate-proxy
```

This runs `abp generate-proxy -t ng` against `https://localhost:44348` and rewrites the files under `modules/vault.extract/angular/projects/vault-extract/src/lib/proxy/`. Commit the regenerated files together with the API change.

## Running the Tests

```bash
dotnet test modules/vault.extract/test/Dignite.Vault.Extract.Application.Tests/Dignite.Vault.Extract.Application.Tests.csproj
```

The core test projects are ABP integration tests over in-memory SQLite; each project under `modules/vault.extract/test/` runs on its own, and the whole core suite takes about a minute. The Angular library's tests run from `modules/vault.extract/angular/`:

```bash
npx ng test vault-extract --watch=false
```

## Troubleshooting

### The Host Stops at Startup with "Extract requires an LLM provider"

`Vault:Extract:Endpoint`, `ApiKey` or `ChatModelId` is missing, or `ApiKey` is still `YOUR_API_KEY`. Check that `appsettings.secrets.json` sits in `src/Dignite.Vault.Extract.Host` next to `appsettings.json`. A message about `VisionOcrModelId` means the vision model id is missing.

### Images Come Back with Empty Markdown

The default OCR provider needs `Vault:Extract:VisionOcrModelId` to name a **vision-capable** model. A text-only model returns nothing useful for an image. See [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md).

### Port Conflicts

If Docker cannot bind a port, another process already uses it:

```bash
# Windows
netstat -ano | findstr ":8866 :18888 :4317"

# Linux / WSL
ss -tlnp | grep -E '8866|18888|4317'
```

### Database Migration Errors

Migrations need `CREATE TABLE` and `CREATE INDEX` rights on the target database. LocalDB's default user has them; on a shared SQL Server, grant the application user `db_owner` on the Vault Extract database.

### PaddleOCR Is Slow on the First Request

PaddleOCR loads the PP-StructureV3 model on the first request, which takes 30 to 60 seconds after a cold start. Later requests are fast.

## See Also

- [Installation](installation.md)
- [AI Provider](../configuration/ai-provider.md)
- [Text Extraction](../text-extraction/text-extraction.md)
- [Deployment](../deployment/deployment.md)

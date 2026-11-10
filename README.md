# Dignite Vault Extract

> **Dignite Vault Extract = any content requiring IDP (Intelligent Document Processing) — scans / photos / PDF images / Office files / digital-born documents → trustworthy structured data.**
> A **channel layer**, not an end-product. It doesn't consume, doesn't own, doesn't dive into business — it hands Markdown + structured metadata to downstream RAG platforms, business systems, and AI clients via REST / EventBus / MCP server / Webhook (planned).

For the full positioning, architecture rules, OUT-of-scope list, Markdown-first contract, multi-stage ETO event contract, and security covenant, see [CLAUDE.md](./CLAUDE.md). It is the truth source — this README only stages the entry points; everything operational lives in [docs/](./docs/en/index.md).

## Data flow

```
content requiring IDP: scans / photos / PDF images / Office files / digital-born documents
    ↓
[Dignite Vault Extract channel]: OCR + Markdown + system metadata + type-bound field extraction
    ↓ (REST / EventBus / MCP server / Webhook — planned)
    ├─→ downstream RAG platform
    ├─→ business systems (finance / CLM / HR / ERP)
    ├─→ AI clients (Claude Desktop / Cursor / any MCP client)
    └─→ any consumer (build your own subscriber)
```

## Solution structure

```
vault/
├── modules/
│   └── vault.extract/   # Channel implementation — ABP layers (Abstractions / Domain.Shared / Domain / Application / EntityFrameworkCore / HttpApi / Mcp)
│                        # plus the text-extraction providers, and angular/ (the @dignite/ng.vault-extract library)
├── src/                 # Host application — provider wiring (OCR + AI) and middleware (ASP.NET Core API)
├── angular/             # Host SPA (operator UI); compiles the module libraries from source
├── etc/                 # Docker, ABP Studio run profile, scripts, PaddleOCR sidecar
└── docs/                # Documentation: setup, configuration, egress contracts, deployment (design decisions go to GitHub Issues, not here)
```

Business modules (contract management / invoice management / HR records / etc.) are **not** in this repo — they belong on the downstream consumer side per the channel philosophy.

## Use it as a module

Vault Extract is distributed as an ABP application module: the `Dignite.Vault.Extract.*` NuGet packages for the server and the `@dignite/ng.vault-extract` npm package for the Angular operator UI. This repository's host (`src/Dignite.Vault.Extract.Host/`) is one host built from them. To add the modules to an ABP application of your own, see [Installation](./docs/en/get-started/installation.md).

## Quick start (run this repository)

| Requirement | Version |
|-------------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet) | 10.0 |
| [Node.js](https://nodejs.org) | `^22.22.3` or `^24.15.0` (Angular 22) |
| SQL Server | 2019+ (LocalDB works for development) |
| [ABP CLI](https://abp.io/docs/latest/cli) | matching ABP 10.7 |
| An LLM provider | **mandatory** — any OpenAI-compatible endpoint; a local Ollama works |
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | optional — PaddleOCR sidecar and the local OpenTelemetry dashboard |

**1. Configure the LLM provider.** Create `src/Dignite.Vault.Extract.Host/appsettings.secrets.json`. It is git-ignored and never published; do not put credentials in the committed `appsettings.json` or `appsettings.Development.json`.

```json
{
  "Vault": {
    "Extract": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "sk-...",
      "ChatModelId": "gpt-4o-mini",
      "VisionOcrModelId": "gpt-4o-mini"
    }
  }
}
```

The host fails fast at startup while `ApiKey` is still the committed placeholder, because classification and field extraction have no non-LLM fallback. `VisionOcrModelId` must name a vision-capable model: the host's default OCR provider is Vision-LLM. See [AI Provider](./docs/en/configuration/ai-provider.md).

**2. Run the backend.**

```bash
cd src/Dignite.Vault.Extract.Host
abp install-libs
dotnet run --migrate-database   # first run, and after pulling a new migration; seeds admin / 1q2w3E*
dotnet run
```

API: `https://localhost:44348`. Swagger: `https://localhost:44348/swagger`.

**3. Run the Angular UI.**

```bash
npm install       # once, at the repo root: one install for every Angular workspace
cd angular
npm start
```

SPA: `http://localhost:4200`. Sign in with `admin` / `1q2w3E*`.

The full walkthrough — database connection, sidecars, proxy regeneration, tests, troubleshooting — is in [Local Development](./docs/en/get-started/local-development.md).

## Where to go next

Start at the **[documentation index](./docs/en/index.md)**.

| You want to… | Read |
|--------------|------|
| Run this repository on your machine | [Local Development](./docs/en/get-started/local-development.md) |
| Add the modules to your own ABP application | [Installation](./docs/en/get-started/installation.md) |
| Define what gets extracted | [Document Types and Fields](./docs/en/documents/document-types.md) |
| Choose an OCR provider | [Text Extraction](./docs/en/text-extraction/text-extraction.md) |
| Integrate a downstream system | [Integration Events](./docs/en/egress/integration-events.md), [REST API](./docs/en/egress/rest-api.md) |
| Connect an AI client | [MCP Server](./docs/en/egress/mcp-server.md) |
| Deploy to production | [Deployment](./docs/en/deployment/deployment.md), [Deployment Checklist](./docs/en/deployment/deployment-checklist.md) |

Release notes, including breaking changes and their migrations, are in [CHANGELOG.md](./CHANGELOG.md).

External references:

* [ABP Framework Documentation](https://abp.io/docs/latest)
* [Application (Single Layer) Startup Template](https://abp.io/docs/latest/solution-templates/application-single-layer)

## License

Dignite Vault Extract is licensed under the [GNU Lesser General Public License v3.0](./LICENSE).

# Dignite Vault Extract

> **Positioning (one sentence)**: Dignite Vault Extract = the **channel layer** that turns any content requiring IDP (Intelligent Document Processing) — scans, photos, PDF images, Office files, and other document content — into trustworthy structured data.
> **It does not consume, own, or reach into business logic** — it exposes outputs to downstream consumers: RAG platforms, business systems, AI clients, etc.

An ABP framework project. Most rules under `.claude/rules/` carry a `paths:` glob and are **auto-loaded when you edit matching code**. This file keeps only what you must know *before* touching any code; the mechanics live in those rule files.

It describes the current design and the rules whose violation breaks something concrete (a security hole, a cross-tenant leak, a wire or database break, a failed CI check). It does not decide future design questions: when one comes up, decide it on its merits.

## Project layout

- **modules/vault.extract/** - the channel implementation (ABP module stack: `src/`, `test/`) and its Angular library (`angular/`, published as `@dignite/ng.vault-extract`)
- **modules/vault.parse/** - Dignite Vault Parse: the text-extraction library (OCR + Markdown providers behind `ITextExtractor`). No ABP layering, no egress of its own
- **src/Dignite.Vault.Extract.Host/** - Host application
- **angular/** - Host SPA (operator UI); compiles the module libraries from source through `tsconfig.base.json` `paths`
- **docs/** - Operations / configuration / API documentation; design decisions live in GitHub Issues, not under docs/

The repo is the Vault suite monorepo (#716): one version for every module in the root `common.props`. Module references are checked in CI by `etc/scripts/check-module-references.mjs`: `vault.parse` references nothing outside itself, `vault.extract` may reference `vault.parse` but never `vault.index`, and `vault.index` reaches Extract only through `Abstractions` / `Application.Contracts` / `HttpApi.Client`. All Angular workspaces share **one** `npm install` at the repo root; never install inside a module's `angular/`, or the host bundles a second Angular runtime (CI: `check:single-angular`).

> **Naming convention**: the C# type / module prefix is **`VaultExtract`** — the namespace `Dignite.Vault.Extract` minus the company token, mirroring ABP's `Volo.Abp.Identity` → `AbpIdentity*Module` (e.g. `VaultExtractHttpApiModule`, `VaultExtractDbContext`, `VaultExtractErrorCodes`). **`Extract` alone is the extraction *verb*, never a type prefix** — `ExtractedField` / `*Extractor` / `TextExtraction*` / `FieldExtraction*` / `ExtractAsync` stay as-is. **Serialized string contracts are frozen even where they still read `Extract`**: error codes `"Extract:*"`, the `/Localization/Extract/` resource, config sections `Vault:Extract*`, the `"extract-documents"` blob container — rename the holder class, never the persisted string value (changing one is a wire / DB / i18n break).

## Architecture

- **Core (`modules/vault.extract/`)**: `Abstractions` holds the integration-event ETOs and depends on no other project in this repo; above it sits the standard ABP module stack. **All channel capability lives in `Application`** (BackgroundJob / Workflow / PipelineRun lifecycle / Document read-write). `HttpApi` (REST) and `Mcp` are thin egress adapters over application services — no business logic, and no protocol concern leaking into `Application`.
- **Text extraction (`modules/vault.parse/`)**: `Application` sees only the contracts in `Dignite.Vault.Parse.Abstractions`; the host picks the providers. See `.claude/rules/text-extraction.md`.
- **Host (`src/Dignite.Vault.Extract.Host/`)**: a container only. It declares `[DependsOn]`, configures providers, and registers three keyed `IChatClient`s (`TitleGenerator` / `Structured` / `Vision`). It registers **no `IEmbeddingGenerator`** (the channel does no vectorization) and implements no business logic. **Middleware is configured only here.**

## OUT of scope

What the Extract module does not contain, and where it lives instead:

- **Retrieval and RAG** (vectorization, vector storage, search over content, chat / Q&A, agent orchestration, an MCP *client*): the planned `vault.index` module, or the consumer.
- **Business logic** (business field schemas, industry templates, approval or renewal workflows, system-specific connectors, contract / invoice / HR modules): downstream consumers build these in their own repos against the egress contracts. Customers define their own fields through mechanism B.
- **LLM / OCR provider choice and API keys**: host deployment configuration. End customers are business users and never see them.

## Document type system (two independent single layers)

A document type is the container for type-bound fields. There are two layers: the Host layer (defined by the Host admin, used only by Host documents) and the tenant layer (defined per tenant, used only by that tenant's documents).

- **No cross-layer mixing**: classification candidates and field extraction match one layer by exact `Document.TenantId`. Host types are not visible to tenants
- **The same TypeCode in two layers is two legitimate rows**: downstream consumes by the `(TenantId, DocumentTypeCode)` tuple; TypeCode is not a global identity
- **The module ships no document types**: admins create them through `IDocumentTypeAppService`

## Field architecture

Fields are either system common fields (typed columns on `Document`, produced by the pipeline: `Title` / `Markdown` / `DocumentTypeId` / review fields / `LifecycleStatus` / `Language`) or type-bound fields (mechanism B: extracted by a schema that the Host or tenant configures). The module ships no field definitions.

How `Document` is shaped today (after changing `Document.cs` / egress DTOs / `TextExtractionResult`, run the `abp-document-boundary-check` skill):

- **`Markdown` is its only text field.** The first parse writes it once (`Document.SetMarkdown` enforces this); afterwards only an operator correction (#555) or a re-parse (#660) replaces it. Derived text (summary / outline / plain text) is projected by the consumer via `MarkdownStripper.Strip` and is not persisted. `Title` is a display snapshot derived from Markdown.
- **It holds no business-specific fields** (contract amount / invoice number / ID-card name). Downstream consumers keep those in their own aggregate roots.
- **`Document.FlexFields` (#558) is a typed bag**: every key is a `Field.Name` backed by a persisted field definition, so its keys are enumerable.

> Details: `.claude/rules/field-architecture.md` (system-field table, mechanism B, classification) and `.claude/rules/sub-document-segmentation.md` (container documents and derived sub-documents).

## Markdown-first

Markdown is the sole text payload of the egress: REST, MCP and events expose `Document.Markdown` and no parallel plain-text field, and downstream consumers depend on that. How providers produce it is in `.claude/rules/text-extraction.md`.

## Egress contracts

Three live channels: **REST API**, **MCP server** and **EventBus** (ABP DistributedEventBus). **Webhook** is planned, not implemented.

- **Events carry thin payloads** (ID + key metadata; downstream pulls the details): `DocumentUploadedEto` → `DocumentTextExtractedEto` → `DocumentClassifiedEto` → `DocumentReadyEto`, plus the lifecycle events `DocumentDeletedEto` / `DocumentRestoredEto` / `DocumentPermanentlyDeletedEto`
- **Only `DocumentReadyEto` is gated**: it fires once the document has no blocking review reason. `ReviewReasonPolicy.Blocking` is the single place that declares which reasons block
- **Delivery is at-least-once** (ABP transactional outbox). Downstream dedups by `EventTime`; the channel keeps no event state table

> Details: `.claude/rules/integration-events.md`.

## Security conventions (all internal LLM call paths)

These apply to every internal LLM call: classification, field extraction, title generation, draft suggestions, VisionLlm OCR. Anti-patterns and correct implementations are in `.claude/rules/llm-call-anti-patterns.md`; read it before adding an LLM call site, because its `paths:` may not match the file you are editing.

- **Fail-closed authorization**: any query path triggered by an LLM, or whose parameters come from LLM output, explicitly calls `IAuthorizationService.CheckAsync(...)` (`[Authorize]` does not fire on MCP / reflection / tool-dispatch paths), caps the result set with `Take(N)`, and uses no raw SQL
- **PromptBoundary**: user-derived free text (title / party name / summary / document content) is wrapped by `PromptBoundary.WrapField(...)` before it enters an LLM prompt or an LLM-facing output
- **Descriptions / instructions are compile-time constants**: never concatenate user-controlled strings into LLM-facing descriptions or instructions at runtime
- **Tenant isolation**: never `Disable<IMultiTenant>()` / `IgnoreQueryFilters()` on an LLM-triggered path
- **Bounded payloads** (#491): every text crossing an LLM boundary has a ceiling. Where cutting the tail would corrupt the result (field extraction / segmentation), the ceiling **gates** the call: skip it, raise a review signal, reach a terminal state, and never rethrow into the background-job retry loop. Elsewhere, truncate with `TextTruncator.AtCharBoundary` and announce the cut. Egress ceilings are compile-time `const`, so they cannot be widened at runtime

## ABP conventions

These are normative: code that violates one is a defect to fix, not a precedent to copy. https://abp.io/docs/latest is the reference for anything not listed here.

- **`virtual`**: every public/protected method on application services and domain services (`*Manager`) is `virtual`, because packaged modules are overridden by consumers. Helpers are `protected virtual`, not `private`.
- **Time**: `Clock.Now` / `IClock`, never `DateTime.Now` / `UtcNow`.
- **DI**: marker interfaces (`ITransientDependency` / `ISingletonDependency` / `IScopedDependency`), never `AddScoped` / `AddTransient` / `AddSingleton`. An interface with several implementers is auto-exposed only to the class whose name ends with the interface name (minus the `I`); anything else needs `[ExposeServices]` (#564).
- **Data access**: application services use `IRepository<T>` (or a custom repository, only where a custom query is needed) — never a `DbContext`. Child entities are reached through their aggregate root (the one documented exception is `DocumentPipelineRun`, see `background-jobs.md`).
- **Errors / i18n**: rule violations throw `BusinessException` with a namespaced code; user-facing text lives in `*.Domain.Shared/Localization/{Resource}/{lang}.json`.
- **Authorization**: permission-based, never hard-coded role checks. The documents domain has its own access-rule table and must not use `[Authorize]` — read `.claude/rules/authorization.md` before touching it.
- **Multi-tenancy**: never hand-write `TenantId` predicates (ABP's global filter does it); `CurrentTenant.Change(...)` only inside a `using`.
- **Tests**: ABP integration-style (test base + in-memory SQLite), Shouldly assertions, NSubstitute for LLM / OCR / blob doubles. One project: `dotnet test modules/<module>/test/<Project>/<Project>.csproj` (`<module>` is `vault.extract` or `vault.parse`); Angular: `npx ng test vault-extract --watch=false` from `modules/vault.extract/angular/`.

## Working rules

1. Development follows the conventions above and the path-scoped rules under `.claude/rules/`. They auto-load by `paths:` only when a matching file is touched, so before changing a mechanism read its rule file even if the file you are editing does not match (e.g. `background-jobs.md` before any BackgroundJob / JobArgs change)
2. **GitHub Issues are this project's design record.** A decision that changes a contract visible outside its module (an egress contract, a module's public API or the module-reference rules, a serialized string contract) is written up in an Issue with its reasoning. Everything else, including edits to this file and to `.claude/rules/`, is recorded in the commit message

# Dignite Vault Extract

> **Positioning (one sentence)**: Dignite Vault Extract = the **channel layer** that turns any content requiring IDP (Intelligent Document Processing) — scans, photos, PDF images, Office files, and other document content — into trustworthy structured data.
> **It does not consume, own, or reach into business logic** — it exposes outputs to downstream consumers: RAG platforms, business systems, AI clients, etc.

An ABP framework project. Most rules under `.claude/rules/` carry a `paths:` glob and are **auto-loaded when you edit matching code**. This file keeps only what you must know *before* touching any code; the mechanics live in those rule files.

## Project layout

- **modules/vault.extract/** - the channel implementation (ABP module stack: `src/`, `test/`) and its Angular library (`angular/`, published as `@dignite/ng.vault-extract`)
- **modules/vault.parse/** - Dignite Vault Parse: the text-extraction library (OCR + Markdown providers behind `ITextExtractor`). No ABP layering, no egress of its own
- **src/Dignite.Vault.Extract.Host/** - Host application
- **angular/** - Host SPA (operator UI); compiles the module libraries from source through `tsconfig.base.json` `paths`
- **docs/** - Operations / configuration / API documentation; design decisions live in GitHub Issues, not under docs/

The repo is the Vault suite monorepo (#716): one version for every module in the root `common.props`. Module references are checked in CI by `etc/scripts/check-module-references.mjs`: `vault.parse` references nothing outside itself, `vault.extract` may reference `vault.parse` but never `vault.index`, and `vault.index` reaches Extract only through `Abstractions` / `Application.Contracts` / `HttpApi.Client`. All Angular workspaces share **one** `npm install` at the repo root; never install inside a module's `angular/`, or the host bundles a second Angular runtime (CI: `check:single-angular`).

> **Naming convention**: the C# type / module prefix is **`VaultExtract`** — the namespace `Dignite.Vault.Extract` minus the company token, mirroring ABP's `Volo.Abp.Identity` → `AbpIdentity*Module` (e.g. `VaultExtractHttpApiModule`, `VaultExtractDbContext`, `VaultExtractErrorCodes`). **`Extract` alone is the extraction *verb*, never a type prefix** — `ExtractedField` / `*Extractor` / `TextExtraction*` / `FieldExtraction*` / `ExtractAsync` stay as-is. **Serialized string contracts are frozen even where they still read `Extract`**: error codes `"Extract:*"`, the `/Localization/Extract/` resource, config sections `Vault:Extract*`, the `"extract-documents"` blob container — rename the holder class, never the persisted string value (that is a wire / DB / i18n break → open an Issue first).

## Architecture

- **Core (`modules/vault.extract/`)**: `Abstractions` holds the integration-event ETOs and depends on no other project in this repo; above it sits the standard ABP module stack. **All channel capability lives in `Application`** (BackgroundJob / Workflow / PipelineRun lifecycle / Document read-write). `HttpApi` (REST) and `Mcp` are thin egress adapters over application services — no business logic, and no protocol concern leaking into `Application`.
- **Text extraction (`modules/vault.parse/`)**: `Application` sees only the contracts in `Dignite.Vault.Parse.Abstractions`; the host picks the providers. See `.claude/rules/text-extraction.md`.
- **Host (`src/Dignite.Vault.Extract.Host/`)**: a container only. It declares `[DependsOn]`, configures providers, and registers three keyed `IChatClient`s (`TitleGenerator` / `Structured` / `Vision`). It registers **no `IEmbeddingGenerator`** (the channel does no vectorization) and implements no business logic. **Middleware is configured only here.**

## OUT of scope (explicitly not done)

Before touching these boundaries, stop and open an Issue to discuss:

- **RAG application layer**: ❌ vectorization / vector storage / retrieval engine / Chat·RAG Q&A·NL search / Agent·Workflow orchestration / MCP **client** (server only, no calling external MCP tools) / standardized chunking
- **Business layer**: ❌ preset schemas for business field extraction (contract amount / invoice number, etc. — customers configure via mechanism B) / preset industry-vertical import templates / business workflows (approval / renewal) / business-system-specific connectors / business modules (contract·invoice·HR management, etc.). Downstream builds these in its own repos against the egress contracts; no business-module dependency may be added inside Core
- **Configuration layer**: ❌ letting end customers configure the LLM / OCR provider or API key. These are host deployment configuration: customers are business users, not technical users

## Document type system (two independent single layers)

A document type is the container for type-bound fields. There are two layers: the Host layer (defined by the Host admin, used only by Host documents) and the tenant layer (defined per tenant, used only by that tenant's documents).

- **No cross-layer mixing**: classification candidates and field extraction match one layer by exact `Document.TenantId`. Host types are not visible to tenants
- **The same TypeCode in two layers is two legitimate rows**: downstream consumes by the `(TenantId, DocumentTypeCode)` tuple; TypeCode is not a global identity
- **No built-in document types**: admins manage all types through `IDocumentTypeAppService`; there is no startup registration and no seed contributor

## Field architecture

Fields are either system common fields (typed columns on `Document`, produced by the pipeline: `Title` / `Markdown` / `DocumentTypeId` / review fields / `LifecycleStatus` / `Language`) or type-bound fields (mechanism B: extracted by a schema that the Host or tenant configures). Core **presets no business field definitions**.

**Document field-extension hard constraints** (must hold before changing `Document.cs` / egress DTOs / `TextExtractionResult`; after such a change run the `abp-document-boundary-check` skill, or apply its checklist by hand):

1. **There is forever only one text-typed field: `Markdown`** (written once by the first parse — `Document.SetMarkdown` enforces it; afterwards only an operator correction (#555) or a re-parse (#660) replaces it). Any derived text (Summary / Outline / plain text) is projected on the consumer side via `MarkdownStripper.Strip`, **not persisted**. `Title` is a display snapshot derived from Markdown, not a new text payload.
2. **Non-text fields are judged by "generic truth source vs. business-specific"**: generic ones (e.g. `PageBlocks`) may be added to `Document` (still requires an Issue to discuss shape); business-specific ones (contract amount / invoice number / ID-card name) are stored by downstream consumers in their own aggregate roots.
3. **Forbidden**: an **unconstrained** `Dictionary<string,object>` extension bag on `Document` / `TextExtractionResult` / event payloads, and any parallel plain-text field. `Document.FlexFields` (#558) is the one **allowed** bag, because every key is a `Field.Name` backed by a persisted field definition. A *second* bag, or a key with no definition, is still forbidden.

> Details: `.claude/rules/field-architecture.md` (system-field table, mechanism B, classification) and `.claude/rules/sub-document-segmentation.md` (container documents and derived sub-documents).

## Markdown-first (mandatory)

Markdown is the **sole text payload**, from the provider to the egress:

- **Providers must output Markdown**: `ITextExtractor` / `IMarkdownTextProvider` / `IOcrProvider` implementations never fall back to a plain-text path. Plain-text-to-Markdown wrapping is done inside the provider, and `OcrResult` / `TextExtractionResult` **expose no RawText field**
- **Out-of-band signals are orthogonal to Markdown**: coordinates / confidence / page metadata / form key-value, if ever needed, are added as **named, strongly-typed, nullable** fields (e.g. `PageBlocks`), with **a separate Issue per signal**

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
2. **Record boundary decisions in an Issue before the implementing PR**: a boundary change is one that alters a contract visible outside its module — an egress contract, a module's public API or the module-reference rules, a serialized string contract (see the naming convention above), or a normative rule in this file or `.claude/rules/` itself. The channel boundaries are the usual cases (OCR pipeline / egress contracts / field architecture / document-type system / Markdown-first / security conventions). Such a change gets a GitHub Issue that states the decision and its reasoning, opened before the PR that implements it; a throwaway spike that informs the decision may come first, but it is not merged. Implementation details — bug fixes, wording corrections, a new provider behind an existing contract — are recorded directly in the commit message

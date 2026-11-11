# Dignite Vault Extract

Vault Extract is a channel layer: it turns scans, photos, PDFs and Office files into Markdown plus structured fields, and hands them to downstream consumers (RAG platforms, business systems, AI clients) over REST, MCP and events. It holds no business logic.

This file describes the current design and the rules whose violation breaks something concrete. It does not decide future design questions: when one comes up, decide it on its merits. The mechanics of each subsystem are in `.claude/rules/`, loaded by `paths:` when a matching file is edited. Before changing a mechanism, read its rule file even if the file you are editing does not match.

## Layout

- `modules/vault.extract/`: the channel (ABP module stack in `src/`, tests in `test/`) and its Angular library in `angular/` (`@dignite/ng.vault-extract`)
- `modules/vault.parse/`: the text-extraction library (OCR and Markdown providers behind `ITextExtractor`)
- `src/Dignite.Vault.Extract.Host/`: the host application
- `angular/`: the host SPA
- `docs/`: operations, configuration and API documentation

Things that fail in CI or at runtime if ignored:

- **Module references** (`etc/scripts/check-module-references.mjs`): `vault.parse` references nothing outside itself; `vault.extract` may reference `vault.parse`, never `vault.index`.
- **One `npm install`, at the repo root.** Installing inside a workspace makes the host bundle a second Angular runtime (`check:single-angular`).
- **One version for every module**, in the root `common.props`.

## Naming

- The C# type and module prefix is `VaultExtract` (`VaultExtractHttpApiModule`, `VaultExtractDbContext`). `Extract` alone is the verb and stays in names like `ExtractedField`, `*Extractor`, `ExtractAsync`.
- **Serialized strings are frozen, even where they still read `Extract`**: error codes `"Extract:*"`, the `/Localization/Extract/` resource, config sections `Vault:Extract*`, the `"extract-documents"` blob container, and the ETO wire names. Renaming one breaks deployed data, clients or translations. Rename the class that holds it, not the string.

## Architecture

- All channel capability lives in `Application`: background jobs, workflows, pipeline runs, document reads and writes. `HttpApi` and `Mcp` are thin adapters over application services.
- `Abstractions` holds the integration-event ETOs and references no other project in this repo.
- `Application` sees text extraction only through `Dignite.Vault.Parse.Abstractions`. The host picks the providers.
- The host is a container: it wires modules and providers and registers three keyed `IChatClient`s (`TitleGenerator`, `Structured`, `Vision`). **Middleware is configured only in the host.** LLM and OCR providers and their API keys are host deployment configuration; end customers never see them.
- Extract does no vectorization or retrieval and registers no `IEmbeddingGenerator`. That belongs to the planned `vault.index` module. Business modules (contracts, invoices, HR) are built by downstream consumers in their own repos.

## Documents, types and fields

- **Document types are two separate layers**: Host types (for Host documents) and tenant types (for that tenant's documents). Classification and field extraction match one layer by exact `Document.TenantId`; there is no union across layers. The same TypeCode in two layers is two rows, so downstream identifies a type by `(TenantId, DocumentTypeCode)`.
- **Fields** are either system fields (typed columns on `Document`, produced by the pipeline) or type-bound fields, extracted by a schema that the Host or tenant configures. The module ships no document types and no field definitions.
- **`Document.Markdown` is the only text field, and the only text payload on the egress.** The first parse writes it once; afterwards only an operator correction or a re-parse replaces it. Derived text is projected by the consumer and is not persisted.
- `Document` holds no business-specific fields. `Document.FlexFields` is a typed bag: every key is a `Field.Name` backed by a persisted field definition. After changing `Document.cs` or an egress DTO, run the `abp-document-boundary-check` skill.

## Egress

- Three channels: REST, MCP server, and the ABP distributed event bus.
- Events carry thin payloads: `DocumentUploadedEto` → `DocumentTextExtractedEto` → `DocumentClassifiedEto` → `DocumentReadyEto`, plus lifecycle events for delete, restore and permanent delete.
- Only `DocumentReadyEto` is gated: it fires once no blocking review reason is set. `ReviewReasonPolicy.Blocking` is the single place that declares which reasons block.
- Delivery is at-least-once through the transactional outbox. Consumers deduplicate by `EventTime`.

## Security on LLM call paths

These apply to every internal LLM call and to anything an LLM can trigger. Read `.claude/rules/llm-call-anti-patterns.md` before adding an LLM call site.

- **Check authorization in the method body**: `[Authorize]` does not fire on MCP, reflection or tool-dispatch paths. Cap every result set with `Take(N)`. No raw SQL.
- **Wrap user-derived text** (titles, names, document content) with `PromptBoundary` before it enters a prompt or an LLM-facing output.
- **Tool descriptions and instructions are compile-time constants.** A user-controlled string in them is a prompt-injection vector.
- **Never pierce the tenant filter** (`Disable<IMultiTenant>()`, `IgnoreQueryFilters()`) on an LLM-triggered path.
- **Every text crossing an LLM boundary has a ceiling.** Where cutting the tail would corrupt the result (field extraction, segmentation), the ceiling gates the call and ends the job in a terminal state. Elsewhere, truncate with `TextTruncator.AtCharBoundary` and announce the cut.

## ABP conventions

Code that violates one of these is a defect, not a precedent. https://abp.io/docs/latest covers the rest.

- **`virtual`**: every public or protected method on application services and domain services, because consumers override packaged modules. Helpers are `protected virtual`.
- **Time**: `Clock.Now`, never `DateTime.Now` / `UtcNow`.
- **DI**: marker interfaces (`ITransientDependency` etc.), never `AddScoped` / `AddTransient` / `AddSingleton`. An interface with several implementers is exposed only to a class whose name ends with the interface name; anything else needs `[ExposeServices]`, or it is silently never resolved.
- **Data access**: application services use repositories, never a `DbContext`. Child entities are reached through their aggregate root.
- **Errors**: a rule violation throws `BusinessException` with a namespaced code. User-facing text lives in `*.Domain.Shared/Localization/`.
- **Authorization**: permission-based. The documents domain has its own rule table and must not use `[Authorize]`; read `.claude/rules/authorization.md` first.
- **Multi-tenancy**: no hand-written `TenantId` predicates. `CurrentTenant.Change(...)` only inside a `using`.

## Commands

- One test project: `dotnet test modules/<module>/test/<Project>/<Project>.csproj` (`<module>` is `vault.extract` or `vault.parse`). Tests are ABP integration style on in-memory SQLite, with Shouldly and NSubstitute.
- Angular: `npx ng test vault-extract --watch=false` from `modules/vault.extract/angular/`.

## Decision record

A decision that changes a contract visible outside its module is recorded where the contract's consumers read it: the pages under `docs/` and `CHANGELOG.md`. Everything else is recorded in the commit message. Neither needs a GitHub Issue.

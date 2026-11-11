---
description: "Dignite Vault Extract internal LLM call-site anti-patterns: fail-closed security gate / PromptBoundary / compile-time-constant descriptions / MCP schema collection-parameter pitfall"
paths:
  - "modules/vault.extract/src/**/*Workflow*.cs"
  - "modules/vault.extract/src/**/Pipelines/**/*.cs"
  - "modules/vault.extract/src/**/*Extraction*.cs"
  - "modules/vault.extract/src/**/*Mcp*/**/*.cs"
  - "modules/vault.extract/src/**/*Prompt*.cs"
  - "modules/vault.extract/src/**/*ChatClient*.cs"
  - "modules/vault.extract/src/**/*VisionLlm*/**/*.cs"
---

# LLM call anti-patterns

These apply to every LLM entry point: classification, field extraction, title generation, segmentation, draft suggestions, VisionLlm OCR, the MCP tools and resources, and any query whose parameters come from LLM output. A, B and E are security issues, C is an MCP schema bug, D is a cost and availability issue.

## A: an extraction agent with context providers attached

The input to a structured extraction is only the current document's Markdown. A `TextSearchProvider` or `ChatHistoryProvider` attached through `ChatClientAgentOptions.AIContextProviders` injects text from other documents, and a field value gets extracted from the wrong document.

Build the messages yourself, as `DocumentClassificationWorkflow` and `FieldExtractionWorkflow` do:

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt + "\n\n" + PromptBoundary.BoundaryRule),
    new(ChatRole.User, PromptBoundary.WrapDocument(truncated))
};
var response = await _chatClient.GetResponseAsync(messages, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, ct);
```

## B: an LLM-triggered query with no fail-closed check

`[Authorize]` at the HTTP boundary does not cover reflection or tool dispatch, so the check inside the method body is the only one. Five ways to get it wrong:

1. **No check in the body.** A client with only MCP credentials can ask for documents it has no right to.
2. **Piercing the tenant boundary**: `DataFilter.Disable<IMultiTenant>()` / `IgnoreQueryFilters()` on an LLM-reachable path, or mapping the endpoint outside `UseMultiTenancy()`. The global filter is the boundary. It is driven by the token's tenant claim, so no hand-written `TenantId` predicate is needed.
3. **An unbounded result set.** A broad keyword returns thousands of rows into the model's context. Always `Take(N)`.
4. **User input concatenated into a tool description or instructions.** That text is part of the model's decision context, so a nickname or document name becomes a prompt-injection vector. Descriptions and instructions are compile-time constants.
5. **Raw SQL built from LLM output.** It is an injection surface and bypasses ABP's permission, audit, soft-delete and tenant filters.

```csharp
[McpServerTool(Name = "vault_extract_search_documents")]
[Description("Search Dignite Vault Extract documents by structured criteria.")]   // compile-time constant
private static async Task<string> SearchAsync(string? keyword, IServiceProvider sp, CancellationToken ct = default)
{
    // 1. explicit check, fail closed (the tools delegate to an application service whose body performs it;
    //    for the documents domain that is DocumentAccessChecker, see authorization.md)
    // 2. tenant: the ambient IMultiTenant filter
    var q = await sp.GetRequiredService<IDocumentRepository>().GetQueryableAsync();
    // 3. business filter + cap
    var rows = await sp.GetRequiredService<IAsyncQueryableExecuter>().ToListAsync(
        q.Where(d => keyword == null || d.Title.Contains(keyword)).OrderByDescending(d => d.CreationTime).Take(MaxResultRows), ct);
    // 4. user-derived free text is wrapped; system fields are not
    return JsonSerializer.Serialize(rows.Select(r => new { r.Id, title = PromptBoundary.WrapField(r.Title) }));
}
```

## E: switching tenant before the authorization check

This applies to any path that accepts a caller-supplied tenant id and calls `ICurrentTenant.Change(...)`, such as the MCP `tenantId` parameter.

A check that runs after the switch is evaluated against the target tenant. ABP's role-based grants key on the role name plus the ambient `TenantId`, so a caller whose token carries a role name that also exists in the target tenant (every tenant's seeded `admin`) passes `CheckPolicyAsync` there.

Gate the switch before it happens, with checks that depend only on the caller's real identity. `McpTenantScope.ResolveAsync` / `ResolveRequiredAsync` run the admission checks and return the tenant id without touching `ICurrentTenant`; the switch is a separate synchronous `Enter` call. The split is required: a single awaited "resolve and switch" method would scope nothing for the caller, because of how `AsyncLocal` flows (see the `McpTenantScope` type doc).

```csharp
var explicitTenantId = await McpTenantScope.ResolveAsync(tenantId, serviceProvider);
using var tenantScope = McpTenantScope.Enter(explicitTenantId, serviceProvider);
var result = await documentAppService.GetListAsync(input);
```

Admission order: `VaultExtractMcpOptions.AllowExplicitTenantScope` (default `false`) → `IMcpTenantAccessValidator.IsAllowedAsync` (default deny-all) → only then the `ITenantStore` existence check. If the existence check ran first, a denied caller could tell "denied" from "unknown tenant" and enumerate tenant ids.

## C: an LLM-facing collection parameter typed as an interface or array

Autofac treats every collection relationship type (`IEnumerable<T>`, `IReadOnlyList<T>`, `IList<T>`, `ICollection<T>`, `IReadOnlyCollection<T>`, `T[]`) as a resolvable service. The MCP SDK excludes any parameter that is a service from the input schema and injects it from DI. So the parameter silently disappears for the LLM, an empty collection is injected, and the feature behind it is broken with no error.

```csharp
public static async Task<...> SearchAsync(
    string documentTypeCode,
    // ❌ IReadOnlyList<FieldFilter>? and FieldFilter[]? are excluded from the schema
    List<FieldFilter>? fieldFilters = null)           // ✅ a concrete List<T> stays in it
```

Only a test that goes through schema generation under Autofac catches this: `McpServerTool.Create(method, target, new McpServerToolCreateOptions { Services = autofacServiceProvider })`, asserting on `ProtocolTool.InputSchema.properties`. See `DocumentSearchTool_Tests.Mcp_input_schema_exposes_fieldFilters_and_all_llm_parameters`.

## D: an unbounded payload into a prompt or out to an MCP client

B caps how many rows a query returns; D caps how large one payload is. Four ways to get it wrong:

1. **A whole document in a prompt with no ceiling.** Markdown is not bounded by the upload limit: a text-dense DOCX or XLSX expands to many times its uploaded size. The token cost lands on the host, and each wrap and serialization step holds another full copy in memory.
2. **Letting the provider's context-window error fail the job and rethrow.** An oversized body is permanent, so every retry sends the same body and the document never reaches a terminal state.
3. **Returning an uncapped body to an MCP client.**
4. **Silently truncating where the tail matters.** An amount can sit anywhere in a document; cutting the tail turns a missed extraction into an apparently successful one.

Each path picks one bound:

| Path | Does the tail matter? | Bound |
|---|---|---|
| `DocumentClassificationWorkflow` | no | truncate at `MaxTextLengthPerExtraction` |
| `DocumentParseBackgroundJob.TryGenerateTitleAsync` | no | truncate at `MaxTitleGenerationMarkdownLength` |
| `CabinetSuggestionWorkflow` | no | truncate at `MaxTextLengthPerExtraction` |
| `DocumentSegmentationJob` | yes | gate at `MaxSegmentationMarkdownLength` → `SegmentationIncomplete` |
| `FieldExtractionService` | yes | gate at `MaxFieldExtractionMarkdownLength` → `FieldExtractionIncomplete` |
| Field schema (`Σ FieldDefinition.Prompt`) | yes | reject the configuration write at the per-type `MaxFieldSchemaPromptLength`; assert again at the call site |
| MCP `vault_extract_get_document` / `documents/{id}` | no | truncate at `VaultExtractMcpConsts.MaxDocumentMarkdownChars` and announce it |

```csharp
// Gate: no call above the ceiling, a review signal instead, and a terminal outcome (no rethrow).
if (markdown.Length > _behaviorOptions.MaxFieldExtractionMarkdownLength)
    return await DeclineOversizedAsync(documentId, tenantId, documentTypeId, markdown.Length);

// Truncate: surrogate-safe, and announced.
var clipped = TextTruncator.AtCharBoundary(body, VaultExtractMcpConsts.MaxDocumentMarkdownChars);
return new DocumentDetailResult
{
    Markdown = PromptBoundary.WrapDocument(clipped),   // truncate first, wrap second: the tags must survive the cut
    MarkdownTruncated = clipped.Length < body.Length,
    MarkdownTotalChars = body.Length
};
```

- A raw `text[..n]` can split a surrogate pair; use `TextTruncator.AtCharBoundary`.
- Prompt ceilings are host configuration (`VaultExtractBehaviorOptions`). Egress ceilings are `const` (`VaultExtractMcpConsts`), so they cannot be widened at runtime.
- An oversized field schema affects every document of its type, so it is rejected when the configuration is written: on create, update, restore and pack import.

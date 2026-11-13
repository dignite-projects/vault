```json
//[doc-seo]
{
    "Description": "Configure the LLM provider behind Dignite Vault Extract: the keyed IChatClient registrations, model choice, non-OpenAI providers and the Vault:ExtractBehavior knobs."
}
```

# AI Provider

Dignite Vault Extract delegates every chat-completion call to `Microsoft.Extensions.AI`. AI configuration is split into two disjoint configuration sections:

| Section | Owns | Consumed by |
| --- | --- | --- |
| `Vault:Extract` | Provider wiring: endpoint, credentials, model ids | The host only. `VaultExtractHostModule.ConfigureAI` reads it once at startup and registers the keyed `IChatClient` instances |
| `Vault:ExtractBehavior` | Workflow behavior: prompt language, truncation and size ceilings | The Application layer, through `IOptions<VaultExtractBehaviorOptions>`. `VaultExtractApplicationModule.ConfigureServices` binds the section |
| `Vault:Index` | The embedding and rerank models behind Vault Index | The host only. See [Embedding model](#embedding-model-index) and [Rerank model](#rerank-model-index) |

The split keeps credentials (`ApiKey`) out of any `IOptions<>` that reaches application code, and lets operators tune behavior without touching the provider. Every LLM feature (classification, field extraction, sub-document segmentation, title generation, cabinet suggestion and the admin-side drafting helpers) shares the same provider registration.

> Dignite Vault Extract is a channel layer. It has no chat, RAG or agentic tool-calling path (removed in [#166](https://github.com/dignite-projects/vault/issues/166)). Its LLM call sites are background pipeline workflows plus a few admin-facing drafting helpers. A downstream RAG or chat consumer registers its own `IChatClient` against its own provider.

## Required before first run

An LLM provider is **mandatory**. Classification and field extraction have no non-LLM fallback, so a host without a provider cannot classify documents or extract fields. To make this unmissable, `VaultExtractHostModule.ConfigureAI` **fails at startup** when `Vault:Extract:Endpoint`, `Vault:Extract:ApiKey` or `Vault:Extract:ChatModelId` is missing, or when `ApiKey` is still the committed `"YOUR_API_KEY"` placeholder. The exception message points to this page.

Supply credentials in `src/Dignite.Vault.Extract.Host/appsettings.secrets.json` (git-ignored, loaded last by ABP's `AddAppSettingsSecretsJson()` and never published) or in environment variables. The shipped host has no `UserSecretsId`, so .NET user secrets do not apply to it. Do not edit the committed `appsettings.json` or `appsettings.Development.json`: the placeholder is exactly what the startup guard checks for, and a real key there would leak into source control. The cheapest first-run path is a local [Ollama](#trying-alternative-providers) endpoint; any OpenAI-compatible provider works.

> The guard lives in the shipped host. A host that embeds the Vault Extract modules and writes its own `ConfigureAI` owns its own validation.

## Provider wiring (`Extract`)

```json
"Vault": {
  "Extract": {
    "Endpoint": "https://api.openai.com/v1",
    "ApiKey": "YOUR_API_KEY",
    "ChatModelId": "gpt-6-luna",
    "VisionOcrModelId": "gpt-6-luna",
    "ReasoningEffort": "None"
  }
}
```

| Key | Required | Description |
| --- | --- | --- |
| `Endpoint` | Yes | API base URL. Any OpenAI-compatible `/v1` endpoint works (Azure OpenAI, Ollama, OpenRouter, vLLM, and so on) |
| `ApiKey` | Yes | API key for the provider |
| `ChatModelId` | Yes | Fallback model for the title-generator and structured clients when their own model ids are not set |
| `TitleGeneratorModelId` | No | Model for the title-generator client. Defaults to `ChatModelId` |
| `StructuredModelId` | No | Model for the structured-output client. Defaults to `ChatModelId`. This is the accuracy-sensitive client; keep it on a strong model |
| `VisionOcrModelId` | With Vision-LLM OCR | Model for the vision client. It must be vision-capable and **does not** fall back to `ChatModelId`. Required only while the [Vision-LLM OCR provider](../text-extraction/ocr-vision-llm.md) is enabled, which is the shipped host's default |
| `ReasoningEffort` | With a reasoning model | `None`, `Low`, `Medium`, `High` or `ExtraHigh`, sent as `reasoning_effort` on every call of every client. A reasoning model spends its reasoning tokens out of the same output cap that field extraction and Vision-LLM OCR size for their visible output, so `None` keeps those caps meaning what they say. Unset sends nothing: leave it out for a provider without reasoning, which may reject the parameter. The committed `appsettings.json` does not set it, so a provider switch does not inherit it |
| `UseLegacyMaxTokens` | On SiliconFlow | `true` sends the output cap as the legacy `max_tokens` instead of `max_completion_tokens`. SiliconFlow ignores `max_completion_tokens` and falls back to its own cap of about 4,096 tokens, which cuts long field extractions and transcriptions short. Default `false`; leave it off for OpenAI and Azure OpenAI, whose reasoning models refuse `max_tokens` |

The shipped host builds a single `OpenAIClient` for all keyed clients. Its HTTP connections are recycled every five minutes (`PooledConnectionLifetime`) and its network timeout is raised to 300 seconds, because field extraction sends the complete document and can legitimately run long.

## Embedding model (`Index`)

Vault Index, which the shipped host carries while it is under development, turns each Ready document's Markdown into chunks and embeds them. The host registers one keyed `IEmbeddingGenerator<string, Embedding<float>>` under `"VaultIndex.Embedding"` from `Vault:Index`:

```json
"Vault": {
  "Index": {
    "EmbeddingModelId": "text-embedding-3-small",
    "EmbeddingDimensions": 1024
  }
}
```

| Key | Required | Description |
| --- | --- | --- |
| `EmbeddingModelId` | To index | The embedding model. Without it no generator is registered and Index indexes nothing: no document is marked failed, and the first `--migrate-database` after a model is added backfills every Ready document |
| `EmbeddingEndpoint` | No | API base URL of the embedding provider. Defaults to `Vault:Extract:Endpoint` |
| `EmbeddingApiKey` | No | Key for that provider. Defaults to `Vault:Extract:ApiKey` |
| `EmbeddingDimensions` | Depends on the model | Sent as `dimensions` on every request. Set it for a model that can shorten its vectors (OpenAI's `text-embedding-3-*`). Set it to an empty string for a model that rejects the parameter: SiliconFlow answers `BAAI/bge-m3` with HTTP 400 when it is sent |

**The model must return 1024-dimension vectors.** The shipped host's schema stores them in a `vector(1024)` column (`VaultIndexHostDbContext.VectorDimensions`); Index refuses vectors of any other length and ends the document in a failed state. `text-embedding-3-small` and `text-embedding-3-large` reach 1024 through `EmbeddingDimensions`; `BAAI/bge-m3` returns 1024 natively. Another length needs a host that changes the constant and adds a migration.

**Changing the model is not a configuration change yet.** Index records the model that produced the serving vectors and refuses a different one (`VaultIndex:EmbeddingModelChanged`) rather than mixing two vector spaces. Rebuilding under a new model comes with a later Index release.

On SiliconFlow:

```json
"Vault": {
  "Index": {
    "EmbeddingModelId": "BAAI/bge-m3",
    "EmbeddingDimensions": "",
    "RerankModelId": "BAAI/bge-reranker-v2-m3"
  }
}
```

## Rerank model (`Index`)

Search can reorder its leading candidates with a rerank model, a cross-encoder that reads the query and each candidate together. On the [evaluation set](../deployment/index-status.md#measuring-retrieval) it raises the share of questions whose answering section comes first from 0.76 to 0.96. Without one, search returns the fused order of its two legs.

| Key | Required | Description |
| --- | --- | --- |
| `RerankModelId` | To rerank | The rerank model, such as `BAAI/bge-reranker-v2-m3`. Unset leaves reranking off |
| `RerankEndpoint` | No | API base URL; `/rerank` is appended. Defaults to `Vault:Extract:Endpoint` |
| `RerankApiKey` | No | Key for that provider. Defaults to `Vault:Extract:ApiKey` |

The host calls the rerank API that Cohere introduced and Jina, SiliconFlow and vLLM serve in the same shape (`POST /rerank` with `model`, `query` and `documents`, answered by `results` of `index` and `relevance_score`). OpenAI has no rerank API, so the committed `appsettings.json` leaves it off. A call takes about half a second for 30 candidates on SiliconFlow and times out after 15 seconds. When it fails, search logs a warning and returns the fused order with `reranked: false` rather than failing.

`Vault:IndexSearch` tunes the ranking:

| Key | Default | Description |
| --- | --- | --- |
| `KeywordWeight` | `1` | The keyword leg's weight in reciprocal rank fusion |
| `VectorWeight` | `1` | The vector leg's weight |
| `RerankCandidateCount` | `30` | Fused candidates the reranker reads (at most 100) |

Keep the weights equal. Down-weighting the keyword leg helped paraphrased questions on the evaluation set without a reranker, but at a keyword weight of 0.5 or less a chunk only the keyword leg finds ranks below every vector candidate. That is how an invoice number or a name gets lost in a large corpus. A reranker fixes the paraphrase case without that cost.

## Enrichment model (`Index`)

An index profile can have an LLM add to each chunk a few keywords and the questions it answers ([Index Profiles](../deployment/index-status.md#index-profiles)). Without an enrichment model, the profile's enrichment is saved but not applied, and the switches on the profile page are disabled.

| Key | Required | Description |
| --- | --- | --- |
| `EnrichmentModelId` | To enrich | A chat model that honors JSON schema output, such as `Qwen/Qwen3-30B-A3B-Instruct-2507`. Unset leaves enrichment off. There is no fallback to `ChatModelId`: enrichment runs for every batch of chunks of every document, so pick a model for that volume |

It uses Extract's endpoint and key and its `UseLegacyMaxTokens` setting. One call reads 10 chunks; on the evaluation set it took about 670 input and 1,020 output tokens per document for both kinds. The model is told the document's language, which Extract detects; asked only to follow each passage, a model can take kanji-heavy Japanese for Chinese. A call that fails or returns something unreadable leaves its chunks without enrichment and is noted on the index run; the document is still indexed.

## Picking a Chat Model

The only capability the backend calls need is **structured JSON output** (`response_format: json_schema`). Function calling, tool use, long-context retrieval and multi-turn coherence play no part.

| Capability | Used by | Failure mode when weak |
| --- | --- | --- |
| Structured JSON output | Classification, field extraction, segmentation, cabinet suggestion, field drafting and prompt polishing, slug suggestion | Malformed JSON or a schema violation: classification goes to the review queue, the affected field values are dropped, a helper returns nothing and the UI falls back |

For production, prefer a model that is strict about schema compliance. A model that "almost gets JSON right" costs you silent gaps: rejected values are discarded and the document goes to manual review.

| Tier | Examples (OpenAI-compatible providers) | Trade-off |
| --- | --- | --- |
| **Recommended** | `gpt-6-luna` (with `ReasoningEffort`), `Qwen/Qwen3-32B`, `deepseek-ai/DeepSeek-V3` | Schema-bound output is reliable and field extraction returns useful values most of the time |
| **Budget** (development only) | `Qwen/Qwen3-8B`, small Llama variants | The JSON shape is usually right, field values less so (numeric coercion failures, missed fields). Good enough to smoke-test the wiring |

The title-generator client can run on a smaller, cheaper model than the structured client. It must still honor `ChatResponseFormat.ForJsonSchema`, because slug suggestion also uses it.

## Keyed Clients

`VaultExtractHostModule.ConfigureAI` registers keyed `IChatClient` instances only. **There is no default (non-keyed) `IChatClient` registration**, so every consumer injects the client for its workload with `[FromKeyedServices(...)]`:

| DI key (value) | Defined in | Required |
| --- | --- | --- |
| `VaultExtractConsts.TitleGeneratorChatClientKey` (`"extract-title-generator"`) | `Dignite.Vault.Extract.Application` | Always |
| `VaultExtractConsts.StructuredChatClientKey` (`"extract-structured"`) | `Dignite.Vault.Extract.Application` | Always |
| `VisionLlmOcrConsts.VisionChatClientKey` (`"extract-vision-ocr"`) | `Dignite.Vault.Parse.Ocr.VisionLlm` | Only with the Vision-LLM OCR provider |
| `VaultIndexConsts.EnrichmentChatClientKey` (`"VaultIndex.Enrichment"`) | `Dignite.Vault.Index.Domain.Shared` | Only with `Vault:Index:EnrichmentModelId` |

The vision key lives in the OCR provider project, not in `VaultExtractConsts`: an OCR provider sits below the Application layer and must not depend on it.

Every client is registered with `UseOpenTelemetry()` and `UseLogging()`. None uses `UseFunctionInvocation` (Vault Extract calls no tools) or `UseDistributedCache` (every prompt is derived from document or admin input and is unique per call, so a cache would always miss).

> **Provider-switch gotcha.** When you point `Endpoint` at a non-OpenAI provider, override every model id key together in the environment-specific configuration. `ChatModelId` alone is not enough when the provider does not recognize a model id inherited from the base `appsettings.json`.

## Where It Is Used

| Caller | Keyed client | Notes |
| --- | --- | --- |
| `DocumentClassificationWorkflow` | Structured | [Classification](../pipeline/classification.md) |
| `FieldExtractionWorkflow` | Structured | Host and tenant fields alike ([document types and fields](../documents/document-types.md)) |
| `DocumentSegmentationWorkflow` | Structured | [Sub-documents](../documents/sub-documents.md) |
| `CabinetSuggestionWorkflow` | Structured | Background cabinet suggestion for a document uploaded without one ([cabinets](../documents/cabinets.md)) |
| `FieldDraftSuggestionAppService` | Structured | Admin helper: drafts a field definition |
| `FieldPromptPolishAppService` | Structured | Admin helper: rewrites a field's extraction prompt |
| `DocumentParseBackgroundJob` (title generation) | Title generator | Generates a short title from the extracted Markdown |
| `SlugSuggestionAppService` | Title generator | Admin helper: suggests an English URL slug (schema-bound) |
| `VisionLlmOcrProvider` | Vision | [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md) |
| `IndexChunkEnricher` | Enrichment (Index) | Keywords and answerable questions per chunk, when a profile asks for them |

One `Vault:Extract` block serves all of them. To use a different model per workflow, add more keyed registrations in your own `ConfigureAI`; split a key only when production telemetry shows a real cost or quality reason.

## Trying Alternative Providers

- **Azure OpenAI**: set `Endpoint` to `https://<resource>.openai.azure.com/openai/deployments/<deployment>/` and use the deployment name as the model id. This is API-key authentication; for Microsoft Entra ID see [Going off-protocol](#going-off-protocol-non-openai-providers).
- **Ollama (local)**: run `ollama serve`, set `Endpoint` to `http://localhost:11434/v1`, set `ApiKey` to any non-empty token (the `/v1` shim ignores it, but the OpenAI client rejects an empty key), and use a locally pulled model id.
- **SiliconFlow**: set `Endpoint` to `https://api.siliconflow.cn/v1`, use SiliconFlow model ids (for example `deepseek-ai/DeepSeek-V3` for the structured client and `Qwen/Qwen3-VL-32B-Instruct` for vision), and set `UseLegacyMaxTokens` to `true`.
- **Any OpenAI-compatible gateway** (OpenRouter, vLLM, LM Studio, and so on) works the same way; only the `Vault:Extract` keys change. If responses run past the cap the log reports, the gateway probably ignores `max_completion_tokens`: try `UseLegacyMaxTokens`.

## Going Off-Protocol (Non-OpenAI Providers)

The `Vault:Extract` keys describe **OpenAI-protocol** providers only, because `ConfigureAI` builds the clients from `OpenAIClient`. To target a provider with a different wire protocol (native Anthropic, native Google Gemini, AWS Bedrock, Azure OpenAI with Microsoft Entra ID, native Ollama without the `/v1` shim), **replace `ConfigureAI` in your host project**. Application code consumes the keyed `IChatClient` instances and does not change.

`Vault:Extract` is irrelevant in that case; read your provider's settings from a configuration shape that matches its credential model. `Vault:ExtractBehavior` still applies.

### Invariants the New Wiring Must Preserve

| Required | Why |
| --- | --- |
| `AddKeyedChatClient` registrations for `VaultExtractConsts.TitleGeneratorChatClientKey` and `VaultExtractConsts.StructuredChatClientKey` (plus `VisionLlmOcrConsts.VisionChatClientKey` with Vision-LLM OCR) | Call sites inject by key. A missing key fails service resolution on first use |
| `.UseOpenTelemetry()` on every client | The `gen_ai.*` spans and token metrics depend on this decorator |
| `.UseLogging()` on every client (recommended) | Per-call request and response logging at `Debug` helps diagnose prompt problems |

Not required: `UseFunctionInvocation`, `UseDistributedCache`, or an `IEmbeddingGenerator` registration (the channel does no vectorization; that is the downstream RAG platform's job).

### Sketch: Azure OpenAI with Microsoft Entra ID

Replaces API-key authentication with `DefaultAzureCredential`, recommended for Azure deployments with a managed identity or workload identity. Add the `Azure.AI.OpenAI` and `Azure.Identity` packages to the host project:

```csharp
using Azure.AI.OpenAI;
using Azure.Identity;

private void ConfigureAI(ServiceConfigurationContext context, IConfiguration configuration)
{
    var azureClient = new AzureOpenAIClient(
        new Uri(configuration["AzureOpenAI:Endpoint"]!),
        new DefaultAzureCredential());

    var titleDeployment = configuration["AzureOpenAI:TitleDeployment"]
        ?? configuration["AzureOpenAI:ChatDeployment"]!;
    context.Services.AddKeyedChatClient(
        VaultExtractConsts.TitleGeneratorChatClientKey,
        _ => azureClient.GetChatClient(titleDeployment).AsIChatClient())
        .UseOpenTelemetry()
        .UseLogging();

    var structuredDeployment = configuration["AzureOpenAI:StructuredDeployment"]
        ?? configuration["AzureOpenAI:ChatDeployment"]!;
    context.Services.AddKeyedChatClient(
        VaultExtractConsts.StructuredChatClientKey,
        _ => azureClient.GetChatClient(structuredDeployment).AsIChatClient())
        .UseOpenTelemetry()
        .UseLogging();
}
```

### Sketch: Native Ollama

Uses `OllamaSharp` directly instead of the OpenAI `/v1` shim. Add the `OllamaSharp` package to the host project:

```csharp
using OllamaSharp;

var endpoint = new Uri(configuration["Ollama:Endpoint"]!);

context.Services.AddKeyedChatClient(
    VaultExtractConsts.TitleGeneratorChatClientKey,
    _ => new OllamaApiClient(endpoint, configuration["Ollama:TitleModelId"]!))
    .UseOpenTelemetry()
    .UseLogging();

context.Services.AddKeyedChatClient(
    VaultExtractConsts.StructuredChatClientKey,
    _ => new OllamaApiClient(endpoint, configuration["Ollama:StructuredModelId"]!))
    .UseOpenTelemetry()
    .UseLogging();
```

For the full list of `IChatClient` providers and their factory calls, see the [Microsoft.Extensions.AI provider list](https://learn.microsoft.com/agent-framework/agents/providers/).

## Cross-cutting LLM behavior (`ExtractBehavior`)

These options describe *how* Vault Extract calls the model. They are bound from `Vault:ExtractBehavior` to `VaultExtractBehaviorOptions` and are host configuration: a host may raise a ceiling to process bigger documents or lower it to cap spend.

```json
"Vault": {
  "ExtractBehavior": {
    "DefaultLanguage": "ja",
    "MaxTextLengthPerExtraction": 8000,
    "MaxFieldExtractionMarkdownLength": 200000
  }
}
```

| Key | Default | Description |
| --- | --- | --- |
| `DefaultLanguage` | `"ja"` | Output language of the **classification** prompt only. The other paths keep their own language strategy: a generated title follows the document's language, field values keep the document's wording, and a slug is always English |
| `MaxDocumentTypesInClassificationPrompt` | `50` | Maximum candidate types in the classification prompt. Extra types are dropped after sorting by descending `Priority` |
| `MaxTextLengthPerExtraction` | `8000` | Maximum Markdown characters sent to **classification** and **cabinet suggestion**, which only need the opening of a document. Longer text is cut at a character boundary |
| `MaxTitleGenerationMarkdownLength` | `4000` | Maximum Markdown characters sent to title generation |
| `MaxCabinetsInSuggestionPrompt` | `50` | Maximum candidate cabinets in the cabinet-suggestion prompt |
| `MinCabinetSuggestionConfidence` | `0.6` | Cabinet suggestion abstains below this confidence and leaves the document without a cabinet |
| `MaxSegmentsPerDocument` | `50` | Maximum slices one document may be split into. A proposal above it is not split; the document gets a review signal instead |
| `MaxSegmentationMarkdownLength` | `200000` | Maximum Markdown characters for sub-document detection. Segmentation reads the whole document, so a longer one is not segmented and gets a review signal |
| `MaxFieldExtractionMarkdownLength` | `200000` | Maximum Markdown characters for field extraction. Field extraction cannot truncate (a field may sit anywhere), so a longer document is declined with the blocking `FieldExtractionIncomplete` review reason instead of calling the model |
| `MaxFieldSchemaPromptLength` | `32000` | Maximum total prompt characters across the active fields of one document type. Creating, updating, restoring or importing a field is rejected when it would push the type over this budget |
| `MaxFieldExtractionOutputTokens` | `16384` | Most output tokens a field-extraction call asks the **structured** model for. Vault Extract estimates a worst case from the field schema and sends it only up to this value, because a provider refuses a request that asks for more than its model can produce, and refuses it again on every retry. Set it to your structured model's maximum output tokens (128,000 for OpenAI's `gpt-6-luna`). On a reasoning model the cap also covers reasoning tokens unless `Vault:Extract:ReasoningEffort` is `None`. A response that needs more stops at the limit; the log names this key, and the fields that did not finish come back empty |

The ceilings follow one rule: where cutting the tail would silently corrupt the result (field extraction, segmentation), the ceiling **gates** the call and raises a review signal; where it would not (classification, title, cabinet suggestion), the text is truncated.

## OpenTelemetry Signals

`ConfigureAI` adds `Microsoft.Extensions.AI`'s `UseOpenTelemetry()` decorator to every keyed client. To collect the signals, register an exporter and add the source and meter names:

```csharp
context.Services.AddOpenTelemetry()
    .WithTracing(b => b
        .AddSource("Experimental.Microsoft.Extensions.AI")
        .AddSource("Microsoft.Extensions.AI")
        .AddSource("Experimental.Microsoft.Agents.AI")
        .AddSource("Microsoft.Agents.AI")
        .AddSource("Dignite.Vault.Extract.*"))
    .WithMetrics(b => b
        .AddMeter("Experimental.Microsoft.Extensions.AI")
        .AddMeter("Microsoft.Extensions.AI")
        .AddMeter("Experimental.Microsoft.Agents.AI")
        .AddMeter("Microsoft.Agents.AI")
        .AddMeter("Dignite.Vault.Extract.*"));
```

The shipped host's `VaultExtractHostModule.ConfigureOpenTelemetry` adds the same names when `OpenTelemetry:Enabled` is `true`; see [Observability](../deployment/observability.md).

| Source / Meter | Emitted by | Covers |
| --- | --- | --- |
| `Experimental.Microsoft.Extensions.AI` (Activity and Meter) | The `UseOpenTelemetry` decorator on each keyed client | GenAI semantic conventions: `chat {model}` spans, `gen_ai.client.operation.duration`, `gen_ai.client.token.usage` |
| `Experimental.Microsoft.Agents.AI` (Activity and Meter) | The agent runtime (`ChatClientAgent.RunAsync<T>`) used by classification | Agent-level spans around each `RunAsync` call |
| `Dignite.Vault.Extract.*` (reserved) | Nothing in Vault Extract today | Reserved for module-specific telemetry, including a downstream consumer's own meters |

## See Also

- [Options and Features](options.md)
- [Classification](../pipeline/classification.md)
- [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md)
- [Observability](../deployment/observability.md)

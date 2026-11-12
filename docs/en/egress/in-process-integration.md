```json
//[doc-seo]
{
    "Description": "Build a module that derives data from Dignite Vault Extract documents in the same host: the document integration service for ingestion and the read scope for serving users."
}
```

# In-Process Integration

A module installed in the **same host** as Dignite Vault Extract can keep its own data derived from Extract's documents — Vault Index keeps chunks and vectors, for example. It needs two things the other egress channels do not give it: a way to read documents from a background job, where there is no user, and a way to show a user only the documents Extract would let that user read. Extract provides one contract for each. Both are plain .NET calls inside the host; neither is an HTTP endpoint or an MCP tool.

| Contract | Called from | Identity | Returns |
| --- | --- | --- | --- |
| `IDocumentIntegrationService` | The module's event handlers and background jobs (ingestion) | None | Ready documents and their ids |
| `IDocumentAppService.GetReadScopeAsync` | The module's own query path, on behalf of a signed-in user | The caller's | What that user may read |
| `IDocumentAppService.GetIdsByFieldsAsync` | The same query path, when the user filters by extracted fields | The caller's | The documents whose fields match |

Both live in the `Dignite.Vault.Extract.Application.Contracts` package. Keep each on its own side: a query path that reads documents through the integration service skips Extract's read rule.

## Ingestion: `IDocumentIntegrationService`

Namespace `Dignite.Vault.Extract.Documents.Integration`. It is an ABP [integration service](https://abp.io/docs/latest/framework/api-development/integration-services): ABP does not expose it over HTTP unless a host sets `AbpAspNetCoreMvcOptions.ExposeIntegrationServices`, and Vault Extract's own controllers do not implement it.

| Method | Returns |
| --- | --- |
| `FindReadyAsync(Guid id)` | The document if it is Ready under the current tenant, otherwise `null` |
| `GetReadyIdsAsync(GetReadyDocumentIdsInput input)` | One page of the ids of the current tenant's Ready documents |

"Ready" means what [`DocumentReadyEto`](integration-events.md) announces: the document passed the [Ready gate](integration-events.md#the-ready-gate), has a confirmed type and is not a container. Nothing outside that set is returned, which is why the service needs no user and performs no authorization check. Both methods read under the ambient tenant, so switch to the event's `TenantId` first (`using (CurrentTenant.Change(eto.TenantId))`).

`FindReadyAsync` returns a `DocumentIntegrationDto`:

| Property | Type | Description |
| --- | --- | --- |
| `Id` | `Guid` | The document |
| `TenantId` | `Guid?` | `null` for a Host document |
| `DocumentTypeId` | `Guid` | The confirmed type. Key derived data by this id, not by the type code, which can be renamed |
| `OwnerId` | `Guid?` | The uploader; the owner the read scope's `OwnerId` is compared with. `null` for a row created with no user |
| `Title` | `string?` | Display title |
| `Language` | `string?` | ISO 639-1 / IETF tag; `null` when not detected |
| `Markdown` | `string` | The whole text. Apply your own ceiling before sending it to an LLM |
| `ExtractedFields` | `Dictionary<string, JsonElement>?` | Field values by name, in the same shape as the [REST API](rest-api.md)'s `extractedFields` |

`null` from `FindReadyAsync` means there is nothing to apply now: the id is unknown, the document is in the recycle bin, or it was Ready when its event was published and has since gone back to review. Keep what you derived from its last Ready version; a deletion reaches you separately as `DocumentDeletedEto`.

`GetReadyIdsAsync` pages by a **keyset cursor**, for a first backfill or a rebuild. Pass the last id of the previous page as `AfterId` (`null` for the first page) and `MaxResultCount` as the page size (1 to 1000), and stop at an empty page. A cursor rather than an offset keeps the walk from skipping a document when an earlier one leaves the Ready set while you are walking it.

```csharp
Guid? afterId = null;
while (true)
{
    var page = await _documentIntegrationService.GetReadyIdsAsync(
        new GetReadyDocumentIdsInput { AfterId = afterId, MaxResultCount = 200 });
    if (page.Items.Count == 0)
    {
        break;
    }

    foreach (var id in page.Items)
    {
        // enqueue an ingestion job for id
    }

    afterId = page.Items[^1];
}
```

The integration service is not behind the `VaultExtract.Enable` feature, because it is called with no user. It tells you nothing by itself: learn what to pull from the [integration events](integration-events.md), and treat a repeated `DocumentReadyEto` as "pull it again".

## Serving a user: `GetReadScopeAsync`

`IDocumentAppService.GetReadScopeAsync()` returns the calling user's read scope over the current tenant's documents, as a `DocumentReadScopeDto`:

| Property | Type | Description |
| --- | --- | --- |
| `Version` | `int` | The rule version; `1` today |
| `IsUnrestricted` | `bool` | The user reads every document of the tenant (`Documents.ReadAll`) |
| `DocumentTypeIds` | `List<Guid>` | The types the user reads through a per-type grant |
| `OwnerId` | `Guid?` | The user's id when they read their own uploads; `null` otherwise |

Under version 1, a document is readable when `IsUnrestricted` is `true`, **or** its type id is in `DocumentTypeIds`, **or** its owner equals `OwnerId`. An empty list with a `null` owner reaches nothing. Store each derived row's `DocumentTypeId` and `OwnerId` (from `DocumentIntegrationDto`) and filter by that expression on every query.

- **Accept only the versions you know.** `Version` changes whenever that reading changes. Refuse any other version rather than guess: an older module reading a newer rule could show a user more than Extract would.
- **Resolve it on every query.** It reflects grants as they are now, so a revoked grant takes effect on the next query.
- **It throws `AbpAuthorizationException` when the user lacks `Documents.Default`.** Such a user reaches nothing through your module either. The method also sits behind the `VaultExtract.Enable` feature, like the rest of Extract's application services.
- **Answer "not found", not "forbidden",** for a row outside the scope, as Extract's own read paths do.

The rule itself is described in [Document Type Permissions](../configuration/document-type-permissions.md).

## Filtering by extracted fields: `GetIdsByFieldsAsync`

Keep no copy of field values in your module. When a user narrows your query by extracted fields, ask Extract which documents match and filter your rows by document id:

```csharp
var matched = await _documentAppService.GetIdsByFieldsAsync(new GetDocumentIdsByFieldsInput
{
    DocumentTypeCode = "lease",
    FieldFilters = [new DocumentFieldFilter { Name = "monthly_rent", Min = "150000" }],
    MaxResultCount = 10_000
});
```

`DocumentTypeCode` is required, and `FieldFilters` takes 1 to 10 conditions with the same rules as the document list's ([REST API](rest-api.md#listing-and-filtering-documents)). `Ids` holds the matching documents in ascending id order, only Ready ones (released, typed, not a container) and only those the calling user may read; `HasMore` says more matched than `MaxResultCount` (1 to 10,000, default 10,000).

- **Refuse, do not truncate, when `HasMore` is `true`.** Searching the first 10,000 ids silently drops the rest. Vault Index refuses with `VaultIndex:FieldFilterTooBroad`.
- **It throws like the document list:** `AbpAuthorizationException` without `Documents.Default`, and a `BusinessException` (`Extract:UnknownExtractedField`) for a type code or field the current layer does not define.
- **Still apply the read scope to your own rows.** The ids are already within it; filtering by both costs nothing and keeps your rows safe if the two calls see different grants.

## See Also

- [Integration Events](integration-events.md) — the events that tell you when to pull
- [Document Type Permissions](../configuration/document-type-permissions.md) — the read rule the scope expresses
- [ABP Integration Services](https://abp.io/docs/latest/framework/api-development/integration-services)

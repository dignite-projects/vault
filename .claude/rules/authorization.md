---
description: "Authorization in Vault Extract: the documents domain's access-rule table (no [Authorize]), and the ABP-default pattern for everything else"
paths:
  - "modules/vault.extract/src/**/*Permission*.cs"
  - "modules/vault.extract/src/**/*AppService*.cs"
  - "modules/vault.extract/src/**/*Controller*.cs"
  - "modules/vault.extract/src/**/DocumentAccess*.cs"
  - "src/**/*Controller*.cs"
---

# Authorization

## Documents domain

`DocumentAppService`, `DocumentExportAppService`, `DocumentReprocessingAppService`, `DocumentStatisticsAppService`, `DocumentPipelineRunAppService` and `CabinetAppService.DeleteAsync` do not use ordinary ABP authorization. The full rule table is in `docs/en/configuration/document-type-permissions.md`.

1. **No `[Authorize]` on these services**, on a method or on the class. MCP / reflection / tool-dispatch paths never run attributes. An attribute also fires before the method body, so it would deny a per-type grant holder or the document's own uploader before the body can apply the rest of the rule. `DocumentTypeAccess_Tests` enforces this.

2. **Every operation starts with `DocumentAccessChecker`:**

   ```csharp
   await _documentAccess.CheckEntryAsync();                                   // before the load
   await _documentAccess.CheckAsync(DocumentAccessRule.Edit, DocumentAccessSubject.Of(document));
   var scope = await _documentAccess.ResolveScopeAsync(DocumentAccessRule.Read);   // row sets only
   ```

   A new operation is a new row on `DocumentAccessRule`. `CheckPolicyAsync` on a `Documents.*` permission in this domain is a bug: it skips the ownership and per-type arms.

3. **One subject, one row.** A row's role-level arm is a set of permissions; to let a second all-types permission admit an operation, add it to that set, not a second check at the call site. A reclassification has two subjects, so it is `Edit` on the document plus `DeclareType` on the target type. Re-parse is the same pair with `DeclareType` on `DocumentAccessSubject.None`.

4. **Ownership goes through the row's owner arm** (`DocumentOwnerArm`: `Never` / `Always` / `UnlessUnderReview`). A hand-written `CreatorId != CurrentUser.Id` cannot express those three cases.

5. **Rights are computed on the server.** `DocumentListItemDto` / `DocumentDto` carry a `rights` object from the same checker; nothing else re-derives the rule.

6. **Queries over `Document` are narrowed by `DocumentAccessScope`**, through the required `ReadScope` of `DocumentQueries.ApplyMetadataFilter`. A new query, or a repository method that returns or names documents, takes it.

## Everything else (ABP default)

- Permission names are constants in `VaultExtractPermissions`, registered in `VaultExtractPermissionDefinitionProvider` with localized display names.
- `[Authorize(VaultExtractPermissions.X.Y)]` on the app-service class and per method (see `CabinetAppService`). Where an attribute cannot express the check, `CheckPolicyAsync(...)` throws and `IsGrantedAsync(...)` branches.
- No hard-coded role names. Identity comes from `CurrentUser`, never from client input.

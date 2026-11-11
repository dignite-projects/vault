---
description: "ABP Angular UI patterns and best practices"
paths:
  - "**/angular/**/*.ts"
  - "**/angular/**/*.html"
  - "**/*.component.ts"
---

# Angular UI

ABP's Angular docs: https://abp.io/docs/latest/framework/ui/angular/overview. For anything not listed here, copy the nearest existing component.

## Workspaces

- `angular/` is the host SPA (project `host`), the only runnable app. `modules/vault.extract/angular/` holds the library `vault-extract` (imported as `@dignite/ng.vault-extract`, with sub-entry-points `/documents` and `/config`) and a `dev-app` stub that exists only for proxy generation.
- The host compiles the library from source through `angular/tsconfig.base.json` `paths`.
- **Run `npm install` once at the repo root, never inside a workspace.** A second install makes the host bundle a second Angular runtime for the library code (`npm run check:single-angular` in `angular/` catches it).

## Generated proxies

```bash
cd modules/vault.extract/angular
npm run generate-proxy
```

The host API must be running at `https://localhost:44348`. `projects/vault-extract/src/lib/proxy/` is overwritten on every run, so nothing in it is edited by hand. Hand-written code that must survive regeneration lives outside it: the FormData upload wrapper in `lib/services/`, the re-export adapter in `public-api.ts`, and the enum contract spec.

## Conventions in this codebase

- Every component is standalone with `ChangeDetectionStrategy.OnPush`. There is no `NgModule`.
- Services come from `inject()`. State is held in signals; under OnPush, an async result that is not written to a signal does not re-render.
- Subscriptions end with `takeUntilDestroyed(this.destroyRef)`.
- Templates use block control flow (`@if`, `@for ... track`), not `*ngIf` / `*ngFor`.
- `ListService` is declared in the component's `providers` and obtained with `inject()`.

## Permissions

- Permission names come from the typed `EXTRACT_PERMISSIONS` constant, never a string literal.
- A component reads a permission once into a field with `permissionService.getGrantedPolicy(...)` and the template tests it with `@if`.
- Routes use the functional guards `[authGuard, permissionGuard]` from `@abp/ng.core` with `data: { requiredPolicy: ... }`.
- What a user may do with a particular document comes from the `rights` object on the document DTO, computed on the server. The client does not re-derive it.

## Modals

- **Simple create / edit form**: the list component owns the form and a signal such as `editing = signal<Dto | 'create' | null>(null)`, and renders the modal inline. See `CabinetListComponent`. The backdrop closes the modal only when both mousedown and click land on the backdrop itself; otherwise dragging a text selection out of an input closes it.
- **Complex workflow** (preview calls, multi-step): a dedicated standalone modal component, with the parent holding a nullable signal for the open target. See `FieldReextractionModalComponent`.

## Routes

A library exports a route array (`DOCUMENTS_ROUTES`) whose entries use `loadComponent`. The host mounts it with `loadChildren` in `app.routes.ts`.

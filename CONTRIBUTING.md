# Contributing to Dignite Vault Extract

Thank you for considering a contribution. This page covers the practical workflow; the architectural contract lives in [CLAUDE.md](./CLAUDE.md) and is the truth source for what belongs in this repository (and what is explicitly out of scope).

## Questions, ideas, and bugs

Not every contribution starts with code. Pick the right channel so the issue tracker stays scoped to actionable work — this mirrors the [issue chooser](https://github.com/dignite-projects/vault/issues/new/choose) and the [Discussions welcome post](https://github.com/dignite-projects/vault/discussions/417):

- **Usage / configuration / "is this in scope?" questions** → [Discussions → Q&A](https://github.com/dignite-projects/vault/discussions/categories/q-a), **not** an Issue.
- **Early ideas, before a formal proposal** → [Discussions → Ideas](https://github.com/dignite-projects/vault/discussions/categories/ideas). Boundary-touching ideas should be floated here first, then promoted to an Issue once there is direction (see [Issue-first principle](#issue-first-principle)).
- **Reproducible bugs / concrete change proposals** → [open an Issue](https://github.com/dignite-projects/vault/issues/new/choose) using a template.
- **Security vulnerabilities** → never a public issue; use [private vulnerability reporting](https://github.com/dignite-projects/vault/security/advisories/new) (see [SECURITY.md](./SECURITY.md)).

## Development environment

Follow [README → Getting started](./README.md#getting-started-local-development). In short: .NET SDK 11, Node.js 22+, SQL Server (LocalDB works), optionally Docker Desktop for the PaddleOCR sidecar and the local OpenTelemetry dashboard. An OpenAI-compatible LLM API key is mandatory — see [AI provider](./docs/en/configuration/ai-provider.md).

After cloning, activate the shared git hooks once:

```bash
git config core.hooksPath .githooks
```

## Running tests

### Backend (xUnit)

All backend test projects are part of the root solution:

```bash
dotnet test Dignite.Vault.slnx
```

Or run individual projects:

```bash
# Vault Extract module
dotnet test modules/vault.extract/test/Dignite.Vault.Extract.Domain.Tests
dotnet test modules/vault.extract/test/Dignite.Vault.Extract.Application.Tests
dotnet test modules/vault.extract/test/Dignite.Vault.Extract.EntityFrameworkCore.Tests
dotnet test modules/vault.extract/test/Dignite.Vault.Extract.Mcp.Tests
dotnet test modules/vault.parse/test/Dignite.Vault.Parse.Ocr.VisionLlm.Tests
```

### Frontend (Vitest)

```bash
npm install                       # at the repo root - one install for every Angular workspace
cd modules/vault.extract/angular
npm test                          # ng test vault-extract
npm run lint                      # ng lint vault-extract
cd ../../../angular
npm run lint                      # ng lint host
```

## Code conventions

- **ABP conventions** — the "ABP conventions" section of [CLAUDE.md](./CLAUDE.md) is normative (`virtual` members, `IClock`, DI marker interfaces, repositories, no `DbContext` in application services), as are the path-scoped files under [`.claude/rules/`](./.claude/rules/). They are written for AI coding assistants but apply equally to human contributors.
- **Architecture rules** — [CLAUDE.md](./CLAUDE.md) defines the channel boundary: Markdown-first data flow, the two-layer document-type model, the exit contracts, and the security covenant for LLM call paths (`.claude/rules/llm-call-anti-patterns.md`).
- Middleware is configured **only** in the host application, never in core modules.

## Issue-first principle

A **boundary change** is one that alters a contract visible outside its module: an exit contract (REST / MCP / EventBus / Webhook, event payloads), a module's public API or the module-reference rules, a serialized string contract (error codes, localization resource names, configuration sections, blob container names), or a normative rule in CLAUDE.md or `.claude/rules/` itself. The channel boundaries are the usual cases: the OCR / text-extraction pipeline, exit contracts, the field architecture, the document-type tier system, the Markdown-first contract and the security covenant.

A boundary change proposed by an outside contributor must start with a GitHub Issue that states the decision and its reasoning, and reach consensus there **before** the PR that implements it is opened. A throwaway spike that informs the decision may come first, but it is not merged.

The maintainers do not open an Issue for their own boundary decisions. Those are recorded where the contract's consumers read them: the pages under [`docs/`](./docs/en/index.md) and [CHANGELOG.md](./CHANGELOG.md).

Implementation details (bug fixes, wording corrections, a new provider behind an existing contract) don't need an Issue — a descriptive commit message is enough.

Not accepted into the Extract module: business modules (contract / invoice / HR management), RAG features (vectorization, retrieval, chat; these belong to the planned `vault.index` module), and end-user LLM configuration. Downstream consumers build business modules in their own repositories against the exit contracts.

## Branch naming

Branches follow `<type>/<issue-number>-<short-description>` (include the issue number when the work has an associated Issue; omit it otherwise):

| Prefix | When to use |
|---|---|
| `feat/` | New feature |
| `fix/` | Bug fix |
| `chore/` | Build, release, dependency update |
| `docs/` | Documentation only |
| `refactor/` | Refactor with no functional change |
| `ci/` | CI/CD configuration |

Rules: all lowercase, words separated by `-`, no underscores or spaces.

```
feat/312-document-type-filter   ✓
fix/298-ocr-timeout-retry       ✓
chore/release-0.2.0             ✓  (no issue number needed)
Feature_DocumentType            ✗
```

A `post-checkout` hook warns immediately when a branch name is non-conforming, and a `pre-push` hook blocks the push until the branch is renamed (`git branch -m <new-name>`). See the setup step in [Development environment](#development-environment) to activate these hooks.

## Commit style

The repository uses [Conventional Commits](https://www.conventionalcommits.org/): `feat(scope): …`, `fix: …`, `docs: …`, `chore: …`, `refactor(scope): …`. Existing commit descriptions are mostly written in Chinese; both Chinese and English descriptions are accepted — pick whichever expresses the change most clearly.

## AI-assisted contributions

AI coding assistants are welcome. Whatever tools you use, the human who opens the PR is the author and is fully responsible for the contribution: it must meet the same review, testing, and architecture-rule bar (`.claude/rules/`, [CLAUDE.md](./CLAUDE.md)) as any other change — read and understand what you submit.

Listing an AI tool as a commit `Co-Authored-By` trailer is **not required**; the project's git history attributes authorship and accountability to people.

## Pull requests

- Target the `main` branch.
- CI (`.github/workflows/ci.yml`) must pass: it builds the core solution and the host, runs the backend test suites (Domain, Application, EntityFrameworkCore, MCP, OCR VisionLlm, and host), and builds / lints / tests the Angular workspace.
- Keep PRs scoped to one concern, and reference the related Issue when there is one (an outside contribution that changes a boundary needs one, see above).
- Use the PR template checklist (`.github/pull_request_template.md`).

## Versioning and releases

The project follows three-part [Semantic Versioning](https://semver.org/) (`MAJOR.MINOR.PATCH`), as declared in [CHANGELOG.md](./CHANGELOG.md). SemVer is not cosmetic here: the version **is** the stability signal to downstream consumers. A breaking change to an exit contract (REST / MCP / EventBus / Webhook, event payloads, `TextExtractionResult` shape) requires a `MAJOR` bump; a backward-compatible addition (e.g. a new nullable named field) is a `MINOR`; a fix that changes no contract is a `PATCH`.

- **Pre-1.0 (`0.y.z`)** — the exit contracts are still being shaped (Webhook is not yet shipped; the field architecture is evolving). Under `0.y.z`, consumers should expect contracts to change. Graduating to `1.0.0` is an earned milestone — all four exits present, the contracts validated against at least one real downstream consumer — not a default for the first release.
- **Pre-release suffixes** — use SemVer pre-release tags for previews on the way to a stable version: `0.1.0-preview.1` → `0.1.0-rc.1` → `0.1.0`. Both NuGet and npm understand their precedence (a suffixed version always ranks below the matching final version) and treat them as non-stable by default.
- **Do not use CalVer** (e.g. `2026.6.0`) — it communicates *when* a release was cut, not whether it is *safe to upgrade*, which is the opposite of what this project's positioning needs.

### Where the version lives

| Property | Segments | Purpose |
|----------|----------|---------|
| `<Version>` in [`common.props`](./common.props) | 3-segment SemVer | The NuGet package version and the value a `v*` tag must match. **This is the release version.** |
| `<AssemblyVersion>` | 4-segment | Keep coarse and stable (e.g. `0.1.0.0`); do not move it for every `MINOR`/`PATCH`, to avoid assembly-binding churn. The 4-segment `1.0.0.0` form belongs here — never as a package version or tag. |
| `<FileVersion>` | 4-segment | Diagnostic only; CI may stamp it from a build number or commit count. |
| `version` in [`modules/vault.extract/angular/projects/vault-extract/package.json`](./modules/vault.extract/angular/projects/vault-extract/package.json) | 3-segment SemVer | The npm package version. **Keep it in lockstep with `<Version>`** as a single product version, until the backend and frontend release cadences genuinely diverge. |

### Cutting a release

1. Move the CHANGELOG `[Unreleased]` section to `## [x.y.z] - YYYY-MM-DD`.
2. Confirm `<Version>` in `common.props` and the npm `version` match the intended release (tags do not drive the version — the release workflow reads it from `common.props`).
3. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. The release workflow (`.github/workflows/release.yml`) triggers on `v*` tags; `workflow_dispatch` only builds artifacts and does not create a GitHub Release.
4. The npm package publishes automatically, routed by channel like the NuGet packages: a pre-release version goes to **GitHub Packages** as `@dignite-projects/ng.vault-extract` (the scope GitHub Packages requires), a stable version goes to **npmjs** as `@dignite/ng.vault-extract`. A stable npm release additionally requires every `@dignite/*` dependency to exist on npmjs; `@dignite/ng.flex-fields` and `@dignite/ng.flex-fields-ckeditor` are published there since `10.0.0-rc.11`, which closed [#577](https://github.com/dignite-projects/vault/issues/577). The **NuGet** side has the matching gate: a stable `Dignite.Vault.Extract.*` must not depend on a pre-release `Dignite.Abp.*` / `Dignite.NotificationCenter.*` package (`NU5104`), which abp-modules `10.7.0` (the first stable) satisfies. npm Trusted Publishing cannot create a package, so a package's **first** stable version is published by hand (`npm publish <tgz> --access public`) and its Trusted Publisher configured on npmjs before the tag is pushed; the release run then skips the already-published version.
5. **Immediately open the next development version**: bump `<Version>` in `common.props` and the npm `version` to the next pre-release (e.g. `0.2.0-preview.2` → `0.2.0-preview.3`) in a standalone `chore(release): bump version` commit. Because the release version is read from `common.props` (not the tag), leaving it on the just-released value means accumulating `[Unreleased]` work still reports a published version and a `workflow_dispatch` build would re-emit artifacts that collide with the published package.

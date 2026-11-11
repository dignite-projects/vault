<!--
  Thanks for contributing to Dignite Vault Extract!
  Keep PRs scoped to one concern. See CONTRIBUTING.md and CLAUDE.md before opening.
-->

## Summary

<!-- What does this change do, and why? -->

## Related issue

<!--
  Link the issue this PR addresses (e.g. "Closes #123").
  Required for a boundary change — one that alters a contract visible outside its module:
  exit contracts (REST / MCP / EventBus / Webhook, event payloads), a module's public API or
  the module-reference rules, serialized string contracts, or a rule in CLAUDE.md /
  .claude/rules/. Those must reach consensus in an issue before this PR is opened
  (CONTRIBUTING.md → Issue-first principle).
-->

## Checklist

- [ ] Backend tests pass locally (`dotnet test Dignite.Vault.slnx`)
- [ ] Frontend checks pass if an Angular workspace was touched (`npm install` at the repo root, then `npm test && npm run lint` in `modules/vault.extract/angular` and `npm run lint` in `angular`)
- [ ] Follows the conventions in `.claude/rules/` and CLAUDE.md (dependency direction, ABP patterns, Markdown-first, LLM-call security covenant)
- [ ] Documentation updated if behavior, configuration, or exit contracts changed
- [ ] No middleware configured outside the host application
- [ ] For boundary changes: a GitHub Issue exists and consensus was reached before this PR was opened

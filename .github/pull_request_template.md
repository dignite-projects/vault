<!--
  Thanks for contributing to Dignite Vault Extract!
  Keep PRs scoped to one concern. See CONTRIBUTING.md and CLAUDE.md before opening.
-->

## Summary

<!-- What does this change do, and why? -->

## Related issue

<!--
  Link the issue this PR addresses, if there is one (e.g. "Closes #123").
  An outside contribution that makes a boundary change needs one. A boundary change alters a
  contract visible outside its module: exit contracts (REST / MCP / EventBus / Webhook, event
  payloads), a module's public API or the module-reference rules, serialized string contracts,
  or a rule in CLAUDE.md / .claude/rules/. It must reach consensus in an issue before this PR
  is opened (CONTRIBUTING.md → Issue-first principle). Maintainers record their own boundary
  decisions in docs/ and CHANGELOG.md instead.
-->

## Checklist

- [ ] Backend tests pass locally (`dotnet test Dignite.Vault.slnx`)
- [ ] Frontend checks pass if an Angular workspace was touched (`npm install` at the repo root, then `npm test && npm run lint` in `modules/vault.extract/angular` and `npm run lint` in `angular`)
- [ ] Follows the conventions in `.claude/rules/` and CLAUDE.md (dependency direction, ABP patterns, Markdown-first, LLM-call security covenant)
- [ ] Documentation updated if behavior, configuration, or exit contracts changed
- [ ] No middleware configured outside the host application
- [ ] For boundary changes: the affected pages under `docs/` and `CHANGELOG.md` are updated (an outside contribution also links the Issue where the change was agreed)

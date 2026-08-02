# Agent notes

<!-- wts-ai-docs:managed -->
## Documentation vault (wts-ai-docs v0.6.1)

This repo has a verified docs vault. Before making claims about the DB,
API, auth, or architecture — and before editing anything under `docs/`:

1. Read `docs/_index.md` first; it maps every domain doc.
2. Read `docs/_meta/vault-conventions.md` AND `vault-conventions-local.md`
   before editing docs — the local file wins on any conflict.
3. On "update docs" / "sync docs": follow `docs/_meta/docs-sync-prompt.md`
   (read `docs-sync-prompt-local.md` first if present).
4. Never edit machine-owned files (loud headers; hash-verified against
   `docs/_meta/vault-manifest.json`) — project rules go in the `-local`
   companions.
<!-- /wts-ai-docs:managed -->

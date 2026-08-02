#!/usr/bin/env node
// vault-lib.mjs — shared core for the wts-ai-docs vault tooling.
//
// Single implementation of content normalization, hashing, placeholder
// filling, the machine-owned file registry, and manifest I/O — consumed by
// bootstrap.mjs and vault-doctor.mjs (and, in Phase 2, upgrade/migrate) so
// the commands cannot drift on fill/hash logic.
//
// Zero runtime deps — Node built-ins only.

import { promises as fs } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';

// Bumped by the release process; the release process keeps this in sync with
// package.json and plugin.json (a version-sync test lands with the release task
// of this plan). Vendored copies of vault-doctor carry this constant so a
// consumer's doctor can tell when a manifest was written by a NEWER tool
// (and must skip hash verification instead of emitting false failures).
export const TOOL_VERSION = '0.6.1';

// Frozen normalization (spec: strip leading BOM, CRLF/CR -> LF, exactly one
// trailing LF). Every writer and verifier goes through this — CRLF noise on
// Windows checkouts must never read as "locally modified".
export function normalizeContent(text) {
  let t = text;
  if (t.charCodeAt(0) === 0xFEFF) t = t.slice(1);
  t = t.replace(/\r\n?/g, '\n');
  t = t.replace(/\n*$/, '\n');
  return t;
}

export function contentHash(text) {
  return 'sha256:' + createHash('sha256').update(normalizeContent(text), 'utf8').digest('hex');
}

export function fillPlaceholders(content, { today, version }) {
  return content
    .replaceAll('REPLACE_WITH_TODAY', today)
    .replaceAll('REPLACE_WITH_VERSION', version);
}

// Numeric per-segment compare; missing segments count as 0.
export function compareVersions(a, b) {
  const as = a.split('.').map(n => parseInt(n, 10) || 0);
  const bs = b.split('.').map(n => parseInt(n, 10) || 0);
  for (let i = 0; i < Math.max(as.length, bs.length); i++) {
    const d = (as[i] || 0) - (bs[i] || 0);
    if (d !== 0) return d < 0 ? -1 : 1;
  }
  return 0;
}

// The machine-owned set: files the upgrade mechanism owns in consumer repos.
// key    = repo-root-relative destination (forward slashes, always)
// source = path inside the wts-ai-docs package
// fill   = whether placeholders are filled at install time (writtenHash is
//          computed over the filled content; templateHash over the source).
// Project-owned files (domain docs, _index, _backlog, doc-ownership.yml,
// *-local.md companions) are deliberately NOT here — upgrades never touch
// them. generate-draft.mjs joins when Workstream C1 completes (spec Q5).
export const MACHINE_OWNED = [
  { key: 'scripts/match-docs.mjs',                  source: 'scripts/match-docs.mjs',            fill: false },
  { key: 'scripts/vault-doctor.mjs',                source: 'scripts/vault-doctor.mjs',          fill: false },
  { key: 'scripts/vault-lib.mjs',                   source: 'scripts/vault-lib.mjs',             fill: false },
  { key: 'scripts/check-docs-freshness.mjs',        source: 'scripts/check-docs-freshness.mjs',  fill: false },
  { key: 'docs/_meta/vault-conventions.md',         source: 'templates/vault-conventions.md',    fill: true },
  { key: 'docs/_meta/docs-sync-prompt.md',          source: 'templates/docs-sync-prompt.md',     fill: true },
  { key: 'docs/_meta/templates/domain-doc.md',      source: 'templates/domain-doc.md',           fill: true },
  { key: 'docs/_meta/templates/cheatsheet.md',      source: 'templates/cheatsheet.md',           fill: true },
];

// Meta docs that have a project-owned -local companion (used to scope
// "move your edits to <name>-local.md" guidance; other machine-owned .md
// files get generic upstream-repo guidance instead).
export const KEYS_WITH_LOCAL_COMPANION = new Set([
  'docs/_meta/vault-conventions.md',
  'docs/_meta/docs-sync-prompt.md',
]);

// Enforcement artifacts installed only when a repo opts in (manifest.ci /
// manifest.hooks). Hash-tracked like MACHINE_OWNED once installed.
// fill: 'ci-branches' means renderCiBranches(manifest.ciBranches) at write.
export const CONDITIONAL_OWNED = [
  { key: '.githooks/pre-commit',                  source: 'templates/ci/pre-commit-hook.example',   fill: false },
  { key: '.github/workflows/docs-freshness.yml',  source: 'templates/ci/docs-freshness.yml.example', fill: 'ci-branches' },
];

export function renderCiBranches(content, branches) {
  return content.replaceAll('REPLACE_WITH_CI_BRANCHES', `[${branches.join(', ')}]`);
}

// AGENTS.md managed-block markers (E1). Pinned to what BIS hand-authored
// before wts-ai-docs existed — adopt semantics depend on matching these
// exact strings so a field-authored AGENTS.md is recognized on migrate.
export const MANAGED_BLOCK_OPEN = '<!-- wts-ai-docs:managed -->';
export const MANAGED_BLOCK_CLOSE = '<!-- /wts-ai-docs:managed -->';

// upsertManagedBlock — replaces the content between the FIRST occurrence of
// MANAGED_BLOCK_OPEN/CLOSE with `blockBody` (which itself carries its own
// markers — see renderAgentsBlock), preserving every byte outside that span
// untouched. No markers found -> append "\n\n" + blockBody at the end,
// leaving existing content exactly as-is (never refuse, never rewrite what
// the consumer already owns). An open marker with NO matching close marker
// AFTER it is malformed (a human likely deleted the closing comment):
// appending would create a second open and make the next run's naive
// first-open/first-close scan ambiguous (it would splice out everything
// between the orphan open and the appended block's close, destroying any
// user content in between); guessing where the block should end would risk
// eating user content too. Neither is safe, so this returns `null` — a
// sentinel meaning "cannot safely upsert; leave the file byte-exact" —
// instead of guessing or destroying data. Pure string function; callers own
// I/O.
export function upsertManagedBlock(existing, blockBody) {
  const openIdx = existing.indexOf(MANAGED_BLOCK_OPEN);
  if (openIdx === -1) {
    return existing + '\n\n' + blockBody;
  }
  const closeIdx = existing.indexOf(MANAGED_BLOCK_CLOSE, openIdx + MANAGED_BLOCK_OPEN.length);
  if (closeIdx === -1) {
    return null;
  }
  const before = existing.slice(0, openIdx);
  const after = existing.slice(closeIdx + MANAGED_BLOCK_CLOSE.length);
  return before + blockBody + after;
}

// renderAgentsBlock — the full managed block (markers included) for
// AGENTS.md, versioned so a stale block visibly differs after an upgrade.
export function renderAgentsBlock(version) {
  return `${MANAGED_BLOCK_OPEN}
## Documentation vault (wts-ai-docs v${version})

This repo has a verified docs vault. Before making claims about the DB,
API, auth, or architecture — and before editing anything under \`docs/\`:

1. Read \`docs/_index.md\` first; it maps every domain doc.
2. Read \`docs/_meta/vault-conventions.md\` AND \`vault-conventions-local.md\`
   before editing docs — the local file wins on any conflict.
3. On "update docs" / "sync docs": follow \`docs/_meta/docs-sync-prompt.md\`
   (read \`docs-sync-prompt-local.md\` first if present).
4. Never edit machine-owned files (loud headers; hash-verified against
   \`docs/_meta/vault-manifest.json\`) — project rules go in the \`-local\`
   companions.
${MANAGED_BLOCK_CLOSE}`;
}

export function manifestPath(repoRoot) {
  return path.join(repoRoot, 'docs', '_meta', 'vault-manifest.json');
}

export function manifestTmpPath(repoRoot) {
  return manifestPath(repoRoot) + '.' + process.pid + '.tmp';
}

export async function readManifest(repoRoot) {
  const p = manifestPath(repoRoot);
  let raw;
  try {
    raw = await fs.readFile(p, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT') return null;
    throw new Error(`vault-manifest.json corrupted or unreadable at ${p}: ${err.message}`);
  }
  try {
    return JSON.parse(raw);
  } catch (err) {
    throw new Error(`vault-manifest.json corrupted or unreadable at ${p}: ${err.message}`);
  }
}

// Files-first, manifest-last is the caller's job; THIS write is atomic
// (temp file + rename) so a crash can't leave a half-written manifest.
export async function writeManifestAtomic(repoRoot, manifest) {
  const dest = manifestPath(repoRoot);
  const tmp = manifestTmpPath(repoRoot);
  await fs.mkdir(path.dirname(dest), { recursive: true });
  await fs.writeFile(tmp, JSON.stringify(manifest, null, 2) + '\n', 'utf8');
  await fs.rename(tmp, dest);
}

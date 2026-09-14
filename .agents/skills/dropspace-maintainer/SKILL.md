---
name: dropspace-maintainer
description: Maintain and release DropSpace; use for its app, updater, packaging, or website changes.
---

# DropSpace maintenance

Deliver the requested change through its authorized completion point. When the
user asks for release, continue through implementation, relevant validation,
protected PR/merge, publication and live website/API verification. Do not stop
at a code checkpoint or claim untested behavior is verified.

Read only the relevant reference:
- [Release and updater](references/release.md): versions, channels, packaging,
  immutable releases, CI, publication and live verification.
- [Native Island](references/native-island.md): media, widgets, settings,
  Windows integration and current known limitations.
- Repository PRODUCT.md/UX.md for product behavior; ARCHITECTURE.md for service
  boundaries; DECISIONS.md for persistence/privacy/dependency decisions.

Preserve unrelated work and all existing product functionality. File record
removal never mutates its referenced source. Keep private payloads and secrets
out of diagnostics. Use real platform evidence for Windows capability claims.

Run checks proportional to the change. Local fixture tests are authorized;
repair introduced failures and rerun affected checks without asking at each
step. Repeat broad passing checks only after changes that invalidate them.

## Skill synchronization

When behavior or maintenance procedures change, update this Skill and its
installed personal counterpart identified by frontmatter `dropspace-codex`.
Validate both, publish the repository copy with the product changes, and verify
the personal destination using the available installation workflow. If a
counterpart is unavailable, state the exact unresolved destination rather than
claiming synchronization. If no changes are needed, report that review result.

Completion reports distinguish shipped outcomes, passing checks, known limits,
and external blockers. Follow current user authorization; do not add approval
steps already covered by the request.

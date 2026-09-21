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

## Beta.26 current verification boundary

The current release target is `v0.3.0-beta.26`, with x64 Windows build 20348
as the minimum. Lyrics are not considered matched unless a provider candidate
has an identity and passes title/artist/album/duration validation; media session
identity, cache isolation, cancellation, and stale-result rejection must be
checked together. Clipboard and virtual-file paths retain bounded queues,
transactional commits, durable payload cleanup, and source-safe ownership.
Updater download/install waiters own cancellation independently and integrity,
trust, state-persistence, launcher, and rollback failures remain visible.
The deterministic release gate is a clean solution build plus passing Core,
Infrastructure and relevant App suites, website/script checks, and an explicit
report of any native or target-machine evidence unavailable in the current
environment. App tests run in an unpackaged VSTest host with its own Windows
App SDK bootstrap; a test-host activation failure is not proof that the actual
application or host lacks platform support.
Batch metadata/query spans both Space and Clipboard, and release packaging
validates artifact identity/version from the current release source; stale
artifacts must fail.

The latest user amendment D-064 removes shuffle/repeat expansion and its acceptance
requirements. Verify the 12 core capabilities in the Native Island reference and
retire stale sessions across restart, with stage-specific diagnostics. Publication
is reauthorized after real one-click acceptance and all release gates; the earlier
pause is superseded. Continue through protected merge and Beta26 publication, then
live website/API verification. Authorization does not establish a native pass.
Track new work in `docs/dev/netease-enhancement.md` and audit status in
`docs/dev/beta26-rc-audit.md`; the lyrics, media and
native/data sub-audits alongside it retain sample-level evidence and limits.
Do not turn provider HTTP success or native fixtures into claims of unexecuted
live-player, physical-monitor, accessibility or two-device acceptance.

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

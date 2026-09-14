---
name: dropspace-maintainer
description: Maintain DropSpace high-risk integration and shipping contracts. Use for drag/OLE/windowing, updater/release/site metadata, packaging/deployment, or cross-device/share security changes.
---

# DropSpace Maintainer

Use this skill as a **router**, not as a repository handbook. Read only the material needed for the current task.

## When to use this skill

Load it when the task changes, reviews, or validates one of these boundaries:

- Dynamic Island, native windowing, Smart Drag, OLE/drop handling, shell intake, hotkeys, system media/notification/volume integration
- updater behavior, version selection, release metadata, packaging, signing, GitHub Releases, CI/CD, GitHub Pages, website release API
- cross-device clipboard, DropLink, Nearby/Internet Share, or related network/security boundaries

Do **not** load it for an isolated copy/style/docs edit, a straightforward local refactor, or a feature that does not touch one of those boundaries. Follow `AGENTS.md` and the relevant repository docs instead.

## Read only what the task needs

Start from the current branch and current source. Then use the smallest relevant set:

| Task | Read |
| --- | --- |
| Service/project boundaries or architectural changes | `ARCHITECTURE.md`; `DECISIONS.md` when changing a durable decision |
| Data model, migrations, retention, persistence semantics | `DATA_MODEL.md`; `DECISIONS.md`; `PRIVACY.md` when retention/privacy changes |
| UI/UX and visual behavior | `DESIGN_SYSTEM.md`; `UX.md` |
| Windows shell, Island, drag/OLE, focus, DPI, media/system integrations | `references/windows-integration.md`; `WINDOWS_INTEGRATION.md` when present/relevant |
| Versioning, updater, packaging, release, website API, deployment | `references/release-delivery.md` |
| Clipboard sync, DropLink, Nearby/Internet Share, network/security | `references/network-sharing.md`; `PRIVACY.md` |
| Testing scope, evidence, and completion | `references/validation.md` |
| Phase/scope questions | `ROADMAP.md` only when the active phase materially changes the answer |

Do not preload the whole documentation set before a small edit.

## Ground truth

When sources disagree, prefer:

1. current source on the branch being changed
2. current tests and workflows
3. canonical repository docs
4. this skill and its references
5. old release notes, historical assumptions, or remembered implementation details

Read volatile state from its source instead of recording snapshots here. Examples:

- current app version: `RELEASE_VERSION`
- current minimum Windows build: the shared build/compatibility source, currently rooted in `Directory.Build.props`
- current release/tag/assets: GitHub Releases and release workflows
- current schema/feature status: source, migrations, tests, and canonical docs

Do not append per-Preview history to this skill.

## Working behavior

For implementation work:

- inspect the affected path and nearby tests first
- make the smallest coherent change that satisfies the request
- run affected tests/builds, fix failures caused by the requested change, and rerun them without asking for approval after every safe local step
- continue past the first compilable implementation when the request includes getting the behavior running or validated
- update canonical docs when a durable contract actually changes

Local tests and fixtures are expected to be disposable and have no production access. Running and repairing affected local tests is safe by default.

Do not turn a normal implementation request into a public release automatically. Publish/deploy when the user explicitly asks to ship/release/deploy, or when the requested task itself is a release/deployment operation. If shipping is in scope, continue through the applicable live verification rather than stopping at the first PR or green compile.

## Durable boundaries

Preserve these unless the task explicitly changes them:

- DropSpace is local-first. Removing a DropSpace record must not delete, move, or modify the referenced external source file.
- The product is Dynamic-Island-only; do not reintroduce the removed Notch mode accidentally.
- Optional Windows integrations must fail closed or degrade gracefully; they must not block core startup or Smart Drag.
- Do not claim release, deployment, signing, Windows-host, network, or browser success without evidence from that layer.
- Treat clipboard data, drag payloads, URLs, paths, metadata, network payloads, and release metadata as untrusted input. Keep work bounded and do not log sensitive payloads or secrets.
- Preserve public release history by default. Do not rewrite published tags or delete public Release assets as routine cleanup.

Domain-specific details live in the matching reference document; do not copy them back into this root file.

## Completion

### Source implementation

A requested code change is complete when:

- the requested behavior is implemented
- affected tests/builds pass, or exact remaining failures are reported
- failures introduced by the change have been fixed
- no known adjacent regression remains unreported
- durable docs/contracts are updated if they changed

### Shipping/deployment

When the task explicitly includes shipping, release, or deployment, read `references/release-delivery.md` and complete the relevant end-to-end checks. A source commit or PR alone is not proof that a public release or live site is correct.

### Investigation/review

For analysis-only work, stop at evidence and recommendations unless the user also requested mutation.

## Skill maintenance

Keep this skill small.

- Put routing, safe autonomy, durable boundaries, and completion criteria in `SKILL.md`.
- Put domain-specific details in `references/` and read them only when relevant.
- Prefer canonical repository docs over duplicating their contents here.
- Do not add release-by-release snapshots, bug diaries, or long task recipes.
- Update this skill only when a durable workflow/boundary or the routing itself changes; ordinary product changes do not require a Skill edit.
- Do not require synchronization to a separate personal Skill unless the user or an explicit repository workflow asks for it.

# DropSpace Agent Rules

These rules apply to the entire repository. User and system instructions take precedence.

## Skill routing

Use `$dropspace-maintainer` only when work touches a high-risk integration or shipping boundary: Dynamic Island/native windowing/drag-OLE, updater/release/site metadata, packaging/deployment, or cross-device/share security.

Do not load the maintainer skill for every repository edit. Isolated copy/style/docs work and ordinary local refactors should use the relevant repository docs directly.

## Read only what the task needs

Do not read a fixed stack of documents before every edit.

- Use `ARCHITECTURE.md` for project/service boundaries and `DECISIONS.md` when changing a durable architectural/product decision.
- Use `DATA_MODEL.md` for schema, migrations, persistence semantics, and retention-related model changes.
- Use `DESIGN_SYSTEM.md` and `UX.md` for visual/interaction work.
- Use `WINDOWS_INTEGRATION.md` plus the maintainer skill's Windows reference for shell, drag/OLE, focus, DPI, and system-integration work.
- Use `PRIVACY.md` for collection, storage, retention, logging, clipboard/network privacy, or trust-boundary changes.
- Use `ROADMAP.md` only when active-phase scope materially affects the task.
- Use the maintainer skill's release reference for updater, packaging, release, GitHub Pages, or website-release-data work.

Inspect current source and nearby tests before relying on remembered project history.

## Safe autonomy and completion

Local builds/tests use repository code and disposable fixtures, not production access. Run affected tests, fix failures caused by the requested change, and rerun them without asking for approval at each safe local step.

When the user asks for an implementation that should run, continue through implementation and relevant validation rather than stopping after the first patch or compile. Do not publish a public release or deploy production merely because source changed; release/deployment must be part of the requested task.

Report what is actually verified. Distinguish source changes, local tests, pushed commits, PRs, CI, Releases, deployment, and live verification.

## Product boundaries

1. Do not change the C#/.NET/WinUI 3/Windows App SDK/MVVM stack without explicit approval and a durable decision update.
2. All user data is local by default. Do not add telemetry upload, accounts, cloud storage, or unrelated network services without explicit product intent.
3. Removing a DropSpace record must never delete, move, or modify its referenced external source file.
4. Space and Clipboard must remain visibly distinct sources unless an explicit product decision changes that model.
5. Pinned is state on an item, not duplicated storage.
6. Do not claim reliable sensitive-content detection or source-app exclusion without evidence.
7. The product is Dynamic-Island-only; do not accidentally restore the removed Notch mode.

## Implementation discipline

1. Do not use placeholders, fake data, no-op handlers, or TODO UI to claim a feature is implemented.
2. Do not remove or disable existing functionality merely to make a bug or test disappear.
3. Fix root causes; do not swallow exceptions, bypass validation, or add arbitrary delays as a substitute for correctness.
4. Keep Windows/Win32 APIs behind narrow adapters.
5. Do not access database, clipboard, shell, or file system directly from Views/ViewModels when an existing service boundary owns that work.
6. Avoid speculative frameworks and unnecessary abstractions; follow current composition patterns.
7. Do not hard-code values that belong in typed settings, design tokens, policies, version sources, or shared constants.
8. Do not block the UI thread with file, thumbnail, image, database, clipboard, network, or media work.
9. Clipboard capture must remain event-driven and bounded; polling is prohibited.
10. Treat drag packages, clipboard contents, paths, URLs, metadata, network payloads, and release metadata as untrusted input.

## Dependencies

1. Before adding a package, justify why it is needed and consider the built-in/platform alternative.
2. Prefer platform/Microsoft libraries already accepted by the architecture unless there is a concrete reason not to.
3. Use versions compatible with the repository's verified SDK/runtime matrix; do not upgrade to “latest” by habit.
4. Update `DECISIONS.md` when a dependency materially changes architecture, trust, deployment, or long-term maintenance.

## Data and privacy

1. Schema changes require numbered migrations and migration coverage from supported prior schemas.
2. Never silently replace a corrupt or failed-migration database with an empty database.
3. App-owned payload paths must remain confined to the controlled payload root.
4. Logs, crash markers, notifications, and diagnostics must not contain raw clipboard payloads, secrets, or full user paths by default.
5. Preserve Pause semantics across restart and recheck pause state before durable clipboard commit.
6. Bound bytes, pixels, text length, queue depth, concurrency, retries, and time spent on untrusted work.
7. Clear-history changes must cover canonical data and owned derivatives according to documented policy.

## UI and design

1. Follow `DESIGN_SYSTEM.md`; prefer WinUI controls/theme resources over custom drawing when they satisfy the requirement.
2. Preserve high-contrast, reduced-motion, keyboard, focus, UI Automation, DPI, mixed-display, and text-scaling behavior relevant to the touched surface.
3. Use “Remove from DropSpace” for record deletion; do not imply that the external source file will be deleted.
4. Do not make essential actions hover-only or unlabeled icon-only controls.
5. Do not turn the app into a generic web-dashboard visual style.

## Windows capability claims

1. Verify uncertain Windows API behavior with current official documentation and, when behavior depends on other applications or real OS integration, with target-host evidence.
2. Clearly distinguish supported, best-effort, limited, and unsupported behavior.
3. External drag-out, tray lifecycle, hotkeys, startup, clipboard attribution, shell intake, system media/notification/audio integration, pickers, and multi-display positioning require Windows-specific validation proportional to the change.
4. Do not implement broad file-system watchers to pretend external references follow arbitrary moves.

## Build and testing

1. Run validation proportional to the change: targeted tests first, broader builds/tests when the affected surface warrants them.
2. Do not ignore build errors. Fix or explicitly report warnings/errors that matter to the requested change.
3. Add a regression test for a bug fix when practical and valuable.
4. Do not claim manual Windows/network/browser acceptance from source inspection or hosted CI alone.
5. Do not require an unrelated full test matrix for a tiny isolated change.

## Documentation

Update only the documentation whose contract actually changed:

- `DECISIONS.md` for durable architecture/product decisions
- `DATA_MODEL.md` for schema/model changes
- `WINDOWS_INTEGRATION.md` for changed Windows capability assumptions
- `PRIVACY.md` for collection, retention, logging, storage, or network/privacy changes
- `PRODUCT.md`, `FEATURES.md`, `UX.md`, and `ROADMAP.md` when shipped scope or behavior changes

Do not append volatile release snapshots to the maintainer skill. Current versions, minimum OS builds, release state, and feature status should be read from their authoritative source.

## Maintainer skill hygiene

The repository skill lives at `.agents/skills/dropspace-maintainer/SKILL.md`.

Update it only when its routing, durable high-risk boundaries, or definition of done changes. Put domain-specific durable guidance in its `references/` folder. Ordinary feature work does not require a Skill edit, and there is no recursive requirement to mirror every repository mutation into a separate personal Skill.

## Completion report

For a mutating task, report the relevant subset of:

- what changed
- validation run and result
- branch/commit/PR state
- release/deployment/live state when those were actually in scope
- known limitations or unverified evidence

Do not claim a layer succeeded unless it was verified at that layer.

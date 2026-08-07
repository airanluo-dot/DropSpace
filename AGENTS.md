# DropSpace Agent Rules

These rules apply to the entire repository. User and system instructions take precedence.

## Before changing anything

1. Read `PRODUCT.md`, the relevant feature/UX document, `ARCHITECTURE.md`, and current phase in `ROADMAP.md`.
2. Read `DECISIONS.md` before changing technology, persistence, privacy, lifecycle, or item semantics.
3. Check the worktree and preserve unrelated user changes.
4. Confirm the requested work belongs to the active phase; do not implement future phases early.

## Product boundaries

1. Do not change the C#/.NET/WinUI 3/Windows App SDK/MVVM stack without explicit approval and a new decision record.
2. All user data is local by default. Do not add network calls, telemetry upload, accounts, or cloud services without explicit approval.
3. Removing a DropSpace record must never delete, move, or modify its referenced source file.
4. Space and Clipboard must remain visibly distinct sources.
5. Pinned is state on an item, not duplicated storage.
6. Do not claim reliable sensitive-content detection or source-app exclusion.

## Implementation discipline

1. Do not use placeholders, fake data, no-op handlers, or TODO UI to claim a feature is implemented.
2. Do not remove or disable existing functionality to make a bug or test disappear.
3. Fix root causes; do not swallow exceptions, bypass validation, or add arbitrary delays.
4. Keep Windows/Win32 APIs behind narrow adapters.
5. Do not access database, clipboard, shell, or file system directly from Views/ViewModels.
6. Avoid deep inheritance and speculative frameworks; follow the composed `DropItem` model.
7. Do not hard-code values that belong in typed settings, design tokens, policies, or constants.
8. Do not block the UI thread with file, thumbnail, image, database, or clipboard work.
9. Clipboard capture must remain event-driven and bounded; polling is prohibited.
10. Any `async void` must be a platform event handler that delegates immediately and handles exceptions.

## Dependencies

1. Before adding any package, document why it is needed, benefits, maintenance/security cost, and built-in alternative.
2. Prefer Microsoft/platform libraries already accepted in `ARCHITECTURE.md`.
3. Pin compatible versions according to the verified Phase 0 SDK matrix; do not use unverified “latest”.
4. Update `DECISIONS.md` when a dependency materially changes architecture or deployment.

## Data and privacy

1. Every schema change requires a numbered migration and migration tests from all supported prior schemas.
2. Never silently replace a corrupt or failed-migration database with an empty database.
3. App-owned payload paths must be generated, relative, and contained under the controlled payload root.
4. Logs, crash markers, notifications, and diagnostics must not contain raw clipboard payloads, full paths, secrets, or URL query strings by default.
5. Preserve Pause across restart and check pause state again before durable clipboard commit.
6. Treat clipboard, drag packages, URLs, shortcuts, paths, and metadata as untrusted input.
7. Bound bytes, pixels, text length, queue depth, concurrency, retries, and time spent on untrusted work.
8. Clear-history changes must cover canonical data, search projections, app-owned payloads, thumbnails, and backups according to documented policy.

## UI and design

1. Follow `DESIGN_SYSTEM.md`; use WinUI controls and theme resources before custom drawing.
2. The UI must work without Mica/Acrylic, in high contrast, and with reduced motion.
3. Keyboard, focus, UI Automation, DPI, mixed-display, and text-scaling behavior are acceptance criteria, not later polish.
4. Use “Remove from DropSpace” for record deletion; never imply the source file will be deleted.
5. Do not make core actions hover-only or icon-only without accessible names.
6. Do not turn the interface into a web-dashboard visual style.

## Windows capability claims

1. Verify uncertain Windows API behavior in official documentation and, where behavior depends on other apps, with an executable spike/test.
2. Clearly mark supported, best-effort, limited, and unsupported behavior.
3. External drag-out, tray lifecycle, hotkeys, startup, clipboard attribution, pickers, and multi-display positioning require Windows integration tests.
4. Do not implement broad file-system watchers to pretend references follow arbitrary moves.

## Build and testing

1. After every phase and meaningful code change, run the documented build.
2. Do not ignore build errors. Review every warning and fix or document the reason.
3. Run tests proportional to the change, then verify the phase acceptance criteria.
4. Every bug fix needs a regression test when practical.
5. Migration, retention, clear, file missing, drag/drop, clipboard loops, pause races, restart, DPI, and display behavior receive dedicated coverage.
6. Do not mark a phase complete while required tests or acceptance criteria are failing.

## Documentation

1. Update `DECISIONS.md` for architecture/product changes and record status/trade-offs.
2. Update `DATA_MODEL.md` and migration plan with any model/schema change.
3. Update `WINDOWS_INTEGRATION.md` when a spike changes a capability assumption.
4. Update `PRIVACY.md` when data collection, retention, logging, storage, or network boundaries change.
5. Keep `PRODUCT.md`, `FEATURES.md`, `UX.md`, and `ROADMAP.md` consistent with shipped scope.

## Completion report

For each phase, report:

- What changed.
- Build and test commands/results.
- Acceptance criteria status.
- Known limitations or risks.
- Documentation/decision updates.

Do not begin the next phase unless the user asks or the current task explicitly includes it.

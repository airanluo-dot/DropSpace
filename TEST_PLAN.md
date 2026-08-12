# DropSpace Test Plan

## Test strategy

Test pure policies heavily, OS adapters with integration harnesses, and a small number of critical end-to-end flows. Manual compatibility testing remains necessary for cross-process drag, tray, display/DPI, and clipboard behavior.

## Quality gates

- Build succeeds; every warning is reviewed, fixed, or documented.
- Unit and integration suites pass.
- Schema migration fixtures pass from every supported prior version.
- No high-severity privacy/security finding remains unexplained.
- Critical manual matrix has evidence for the release candidate.

## Unit tests

### Domain and classification

- Item capabilities by source/kind/status.
- URL, color, JSON/code/path hints with false-positive cases.
- Search normalization and ranking.
- Duplicate-window/fingerprint policy.
- Retention age/count/pin ordering.
- File availability state transitions.
- Clear-range date/time-zone boundaries.

### Safety

- Record removal has no source-delete dependency/call.
- Payload relative-path validation rejects absolute/traversal/device paths.
- Log redactor removes raw text, paths, tokens, and URL queries.
- Size/pixel/text limits handle integer boundaries.
- Pause generation invalidates in-flight commits.

### ViewModels

- Command enablement and concurrency.
- Empty/loading/error/missing states.
- Focus/selection preservation after item removal.
- Settings validation/revert on failure.
- Overlay state transitions for empty/drag/compact/expanded/dismiss/mode-transition paths, including interruption and count preservation.
- One hundred Reveal/Hide and Compact/Expanded cycles return to stable Hidden state without retained scheduler state.
- Smart drag policy: click, stationary press, below-threshold movement, unknown/text/window sources, Explorer/Desktop left/right candidate drags, documented object-event promotion after an exact-item miss, duplicate WinEvent/UIA/mouse signals, Escape/completion and 1,000 sequential sessions.
- Smart accessibility adapter: `SetWinEventHook` registers the documented object-drag range on the observer message thread; callbacks remain bounded; COM initialization occurs on the actual classifier thread; nested text/image leaves can resolve through a bounded ancestor chain; and all returned COM objects have bounded lifetime.
- Overlay placement policy: one Smart physical offset at 100%, 125%, 150%, 175%, and 200% DPI; Dynamic Island and Notch display gaps; Classic/Disabled top-edge anchors; offset 340-DIP Expanded geometry remains inside the fixed host.

## Integration tests

### SQLite and payloads

- Create/open/restart CRUD.
- Transaction rollback on injected failure.
- Foreign keys and cascade behavior.
- WAL/read concurrency if enabled.
- Retention and clear remove search projections.
- Logical delete plus eventual payload/thumbnail cleanup.
- Orphan reconciliation/quarantine.
- Disk-full, read-only, missing-payload behavior.

### Migrations

- Empty → current.
- Each released schema fixture → current.
- Interrupted/failing migration preserves original and backup.
- Newer schema refuses unsafe downgrade.
- Idempotent startup after migration completion.

### Clipboard adapter/pipeline

- Text and image standard formats.
- Multiple formats and precedence.
- Clipboard locked/transient retry.
- Rapid 1,000-event burst with bounded memory.
- Delayed content replaced before read.
- Self-copy loop suppression.
- Consecutive-only identity sequences for text, images, single files and multi-file selections; `A,A` collapses, `A,B,A` persists all three, and behavior is independent of elapsed time.
- Concurrent identical notifications commit at most once; failed persistence remains retryable; Clear History and Pause/Resume reset stale suppression state.
- Pause/resume and clear races.
- App exit with queued candidates.

### File/drag adapters

- Multi-file/folder incoming package.
- Unsupported virtual/mixed package partial success.
- Outgoing package advertises storage items and copy operation.
- Missing/permission/network errors map to correct domain state.

## UI automation

Automate only stable critical flows:

1. Launch and single-instance activation.
2. Navigate Space/Clipboard/Pinned/Settings by keyboard.
3. Add a prepared file through test hook or controlled drop harness, pin, restart, remove record, verify source exists.
4. Inject clipboard text, find it, copy again, verify no duplicate loop.
5. Pause/resume and clear history.
6. Missing-item Locate/Replace with controlled picker abstraction where automation allows.
7. Theme change and core automation properties.
8. Dynamic Island/Notch setting persistence and immediate state-preserving transition.

External drag-out remains a manual/adapter-assisted compatibility test because end-to-end pointer automation across processes can be brittle.

## Manual test matrix

### Clipboard

- Sources: Notepad, browser, Office, terminal/editor, screenshot tool, password manager if safely available, Remote Desktop scenario.
- Payloads: short/large Unicode text, URL, colors, JSON, transparent/large image, file list in V1.1.
- Behaviors: rapid copy, source closes, clipboard busy, DropSpace hidden, session restart.

### Drag and drop

- In from Explorer/Desktop/network/OneDrive/removable drive.
- Out to Explorer/Desktop, browser upload, Office, VS Code, Photoshop/available editor.
- Single/multiple, file/folder, missing during drag, cancellation, elevated boundary.
- Record actual target version and result.

### File missing/resilience

- Move, rename, delete, permissions, unplug USB, disconnect NAS, offline OneDrive.
- Verify Missing vs Unavailable language and Locate/Replace.

### Displays and DPI

- 1 and 2+ displays.
- 100%, 125%, 150%, 200%; mixed scale.
- Move window between displays; disconnect the last-used display.
- Taskbar on each edge and auto-hide.
- In Smart mode, drag a real Explorer/Desktop file on each monitor and verify the temporary target appears on that pointer's display without first touching the top edge. Verify DragApproaching, DragReady, post-Drop Compact, Expanded, Dismissing, Dynamic Island, and Notch retain one physical vertical anchor with no jump or clipped Drop region. In Classic mode, verify the legacy edge target separately.
- Switch Dynamic Island/Notch at every scale and migrate between mixed-scale displays without blur, offset, or crash.
- Verify ordinary fullscreen suppresses passive presentation while an explicit file drag may reveal the target.

### Accessibility

- Keyboard only and visible focus.
- Narrator/UI Automation names, roles, states, reading order.
- High contrast themes, color filters, reduced motion, 200% text.
- Touch target sanity where hardware is available.

### Lifecycle

- Close-to-tray, reopen, Exit, Explorer restart, Windows sign-out/restart.
- Second activation while visible/hidden.
- Crash/injected failure during database write and migration.

### Packaging

- Download only `DropSpace.exe` to Desktop/Downloads and launch as a standard user with no .NET/Windows App SDK runtime prerequisite.
- Confirm file icon and Product Name/File Description/Internal Name/Original Filename/version metadata.
- Replace `DropSpace.exe` and confirm `%LOCALAPPDATA%\DropSpace` database/settings/payload state remains.
- Clean MSIX install as standard user.
- Upgrade from previous released package/schema.
- Offline start, repair, uninstall/reinstall.
- Verify documented data retention on uninstall.
- Confirm unsigned Preview SmartScreen behavior is documented; never disable or bypass Defender policy.

## Performance tests

Reference device specifications are recorded with results.

- Cold/warm launch to interactive.
- Idle CPU and working set while tray-hidden.
- Clipboard burst queue depth, dropped events, processing latency.
- Search P50/P95 at 1k/10k/50k fixtures.
- Scroll with 500 image/text rows and thumbnail cancellation.
- Large image peak decoded memory.
- Database/payload size after retention cleanup.
- 100 Overlay reveal/dismiss and compact/expanded cycles with window count, working set, handlers, and Composition resource observations.
- Hidden/idle CPU and GPU observation: no continuous `CompositionTarget.Rendering`, DispatcherTimer, high-frequency loop or polling. Smart mode's event-driven observer hooks must remain idle and never suppress input.
- Interrupt a width/height/radius/content spring in both directions and prove every channel settles with no retained frame subscription.

No performance number is accepted without hardware, build configuration, dataset, and measurement method.

## Security and privacy tests

- Known secret strings never appear in logs, crash markers, notifications, or diagnostic export.
- Clear history removes canonical rows, search results, payloads, and thumbnails.
- Pause race prevents durable commit after completion.
- Malformed relative payload paths cannot escape root.
- Crafted URL schemes are not auto-opened.
- Huge/malformed inputs cannot cause unbounded allocation or queue growth.
- App exclusions, when introduced, are tested for false attribution and accompanied by limitation copy.

## Release evidence

For each phase/release retain:

- Build/test command and result.
- Supported OS/SDK/package versions.
- Manual matrix with pass/fail/known limitation.
- Migration fixture results.
- Performance measurements.
- Accessibility and privacy review notes.
- Clean workflow SHA, test totals/TRX, exact EXE/MSIX sizes, Authenticode status, SHA-256 manifest, smoke marker result, release URL, and final default-branch SHA.

The portable smoke harness must prove that the published single `DropSpace.exe` starts, initializes Windows App SDK and SQLite, creates/writes the user AppData tree, registers `AddClipboardFormatListener`, writes `A,A,B,A` unique-token text plus a mixed set of two real `StorageFile` objects and one `StorageFolder` through the Windows clipboard, observes `WM_CLIPBOARDUPDATE`, proves the adjacent A is suppressed while the final non-consecutive A is persisted, reads and persists three `Source=Clipboard` file/folder references, verifies Pause/Resume and self-write suppression, removes its test rows, verifies zero classic activation hosts in the fresh Smart default, verifies both the observation-only mouse hook and documented object-drag WinEvent hook are registered, proves hidden top-center `WindowFromPoint` pass-through, proves temporary Compact/Expanded target discovery and synthetic `CF_HDROP`, executes 100 real Overlay lifecycle/resource cycles and 1,000 interruptible Island/Notch real XAML/HRGN geometry cycles with zero region failures and no retained frame subscription, redirects a second instance, and exits cleanly. A successful `dotnet publish` without this runtime probe is not release evidence.

The installer lifecycle harness runs only in an isolated Windows account/runner and must: compile a baseline and current Setup from the same AppId; silently install to a custom path; verify x64 EXE metadata, shortcuts and Installed Apps registration; start DropSpace and upgrade it through graceful maintenance shutdown without `/DIR`; preserve an AppData marker and chosen path; run the installed smoke; verify the default startup command; normally uninstall while preserving data and removing startup; reinstall and complete-uninstall while removing only `%LOCALAPPDATA%\DropSpace`; and prove an external sentinel representing an original referenced file remains. The script refuses to run when a pre-existing DropSpace data root exists.

Automation does not claim visual quality or Explorer/UIA provider coverage. Before Preview sign-off, a real Windows 11 desktop must verify ordinary clicks/text selection/window drags never reveal Smart mode; Explorer/Desktop file/folder/multi-select left/right drags do; Escape/high-speed/cancel recover; the idle top edge resolves to the underlying app; Drop Tray can coexist with the offset target; Compact/Expanded direct Drop still works; Classic mode switches live; Dynamic Island/Notch motion, mixed-DPI monitors and full-screen behavior remain correct.

## v0.1.0 Stable automated gates

- ReleaseVersion parsing/order and the complete Stable/Preview selection matrix, including Preview receiving Stable and no downgrade.
- Fresh Stable settings versus existing Preview-era migration; all existing Clipboard/retention/overlay/startup preferences preserved.
- Process-start automatic check once, repeatable manual check, disabled automatic check, and single-flight overlap.
- Exact/bounded update manifest failures: schema, tag, channel, version code, size, SHA, missing/duplicate/unexpected assets, malformed JSON, oversized input, and arbitrary URL field.
- Streaming update download success plus hash/size/interruption/cancellation/transport/path-containment failures; no partial payload reaches executable state.
- Official website release API schema/host/tag/asset validation and resilient website → mirror → GitHub REST fallback without public-network access in tests.
- Brand generator, nine ICO frames, resource ID 101, EXE/Setup embedded icon, MSIX asset matrix, and no retired icon reference.
- Installer `/UPDATE` graceful maintenance shutdown, custom-path/data/startup preservation, automatic restart marker, installed smoke, both uninstall modes, and external sentinel protection.

- Core coordinator test issues 500 concurrent revisions and proves maximum loader/apply concurrency of one, latest-revision convergence and recovery after a failed revision.
- Overlay state tests cover Compact direct drag → DragReady → Compact and Expanded drag/highlight/drop/leave without geometry collapse.
- Windows executable smoke probes Compact and Expanded center pixels with `WindowFromPoint`; each must be the visual root HWND or a WinUI descendant while the passive host is disabled.
- A synthetic `CF_HDROP` COM data object is sent through the registered visual native target in Compact and Expanded. Both must reach `AddPathsAsync`; Expanded must remain Expanded.
- A 200-cycle stress alternates removal from Expanded Overlay and Main projection while both are live, forces GC periodically, checks authoritative count/Main/Recent projections, requires zero AppDomain/unobserved-task deltas and proves the external sentinel still exists.
- Identity build validates the stable Name/Publisher/version and Share Target manifest. Unsigned CI does not install it; trusted registration is exercised only when Artifact Signing credentials exist.

Manual Windows 11 gates remain real Explorer/Desktop pointer delivery, Drop Tray on/off Shell ownership and direct suggestion ranking, Share UI activation with a trusted signed identity, visible Compact/Expanded feedback, last-item dismissal, mixed-DPI/multi-monitor input, animation feel and zero-pixel Hidden appearance.

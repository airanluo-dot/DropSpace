# DropSpace Test Plan

## Test strategy

Test pure policies heavily, OS adapters with integration harnesses, and a small number of critical end-to-end flows. Manual compatibility testing remains necessary for cross-process drag, tray, display/DPI, and clipboard behavior.

## Quality gates

- Build succeeds; every warning is reviewed, fixed, or documented.
- Unit and integration suites pass.
- Schema migration fixtures pass from every supported prior version.
- No high-severity privacy/security finding remains unexplained.
- Critical manual matrix has evidence for the release candidate.
- `en-US` and `zh-CN` `.resw` key sets are identical; every XAML resource identifier uses the app-owned override or an explicit `Window` application path, every imperative localizer key and package-manifest string resolves in the English base resource file, and source `.cs` and `.xaml` files contain no CJK hardcoded UI text. Portable publishing regenerates and explicitly bundles its packaging-free `DropSpace.resources.pri` before the runtime smoke without replacing WinUI's default resource index.

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
- Smart drag policy: click, stationary press, below-threshold movement, unknown threshold → generic candidate, Explorer/Desktop exact item → strong candidate, recognized blank Shell surface → generic candidate, unknown accessibility drag-start → strong candidate, probe verify/reject/timeout, strong-signal promotion while a probe is pending, release/Escape/completion, stale-session timeout/reject, duplicate WinEvent/mouse signals, and 1,000 sequential sessions.
- Smart accessibility adapter: `SetWinEventHook` registers the documented object-drag and system drag/drop ranges before the observer message pump is declared ready; callbacks remain bounded; deprecated root-wide `UiaAddEvent` registration cannot block startup; COM initialization occurs on the actual classifier thread; UIA/MSAA nested item leaves can resolve through bounded inspection; and all returned COM objects have bounded lifetime.
- Overlay placement policy: one Smart physical offset at 100%, 125%, 150%, 175%, and 200% DPI; Dynamic Island top gap; Classic/Disabled top-edge anchors; offset 340-DIP Expanded geometry remains inside the fixed host.
- Reliable/lossy signal lanes: a reliable burst is fully readable, a one-slot move lane keeps the newest position, and a completed critical lane reports a write failure instead of blocking.
- Schema-9 placement policy: an unconfigured monitor resolves Automatic, two monitors can retain different Custom coordinates, Reset removes only the selected entry, and transient clamping leaves saved DIP coordinates unchanged.
- Stable display identity: normalized target-path inputs yield the same persistent ID and runtime fallback IDs remain explicitly distinguishable.
- Direct placement session: physical pointer delta converts to DIP, release commits one final preview, and Escape restores the pre-edit snapshot.

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
- Virtual-file descriptor bounds, indexed stream materialization, cancellation, duplicate-safe names, staging containment, and whole-batch rollback.
- Outgoing package advertises storage items and copy operation.
- Missing/permission/network errors map to correct domain state.
- Query-only OLE classification for `CF_HDROP`, Shell IDList, `FileGroupDescriptorW` + indexed `FileContents`, plain text, and unsupported formats; no content read during verification.
- Ephemeral probe HWND creation with real hollow Region, physical monitor coordinates, `NOACTIVATE|TOOLWINDOW|TOPMOST`, one active instance, `DROPEFFECT_NONE`, 60 ms timeout, callback-posted revoke/destroy, double-dispose, mode-switch/shutdown cleanup, and stale-session isolation.

## UI automation

Automate only stable critical flows:

1. Launch and single-instance activation.
2. Navigate Space/Clipboard/Pinned/Settings by keyboard.
3. Add a prepared file through test hook or controlled drop harness, pin, restart, remove record, verify source exists.
4. Inject clipboard text, find it, copy again, verify no duplicate loop.
5. Pause/resume and clear history.
6. Missing-item Locate/Replace with controlled picker abstraction where automation allows.
7. Theme change and core automation properties.
8. Legacy settings containing the removed Overlay display-mode field migrate safely to the single Dynamic Island surface.
9. Settings persists System/English/Simplified Chinese display-language selection and emits the localized restart-required status without changing the live process resource context.

External drag-out remains a manual/adapter-assisted compatibility test because end-to-end pointer automation across processes can be brittle.

## Manual test matrix

### Localization (English and Simplified Chinese Windows 11)

- Run the complete critical flow on an English Windows 11 display-language installation and a Simplified Chinese Windows 11 display-language installation; record OS build, display-language setting, application build, and result.
- In each installation, choose **System default**, restart DropSpace, and verify that the main window, Dynamic Island, tray tooltip/menu, update states, dialogs/errors, and accessibility names resolve to the expected resource set.
- In each installation, explicitly choose **English**, restart, and verify the same surfaces are English; explicitly choose **Simplified Chinese**, restart, and verify the same surfaces are Simplified Chinese. This verifies the app-owned XAML override as well as imperative and native surfaces.
- Use Narrator/UI Automation on the display-language selector, navigation, item actions, and Dynamic Island controls; ensure no stale language or raw exception message is announced.
- CI runs the full Windows workload in `en-US` and `zh-CN` resource contexts and checks a resolved-resource smoke marker. GitHub-hosted runners do not constitute a claim that the Windows operating-system display language itself was changed; the two real Windows 11 installations remain required release evidence.

### Clipboard

- Sources: Notepad, browser, Office, terminal/editor, screenshot tool, password manager if safely available, Remote Desktop scenario.
- Payloads: short/large Unicode text, URL, colors, JSON, transparent/large image, file list in V1.1.
- Behaviors: rapid copy, source closes, clipboard busy, DropSpace hidden, session restart.

### Drag and drop

- In from Explorer/Desktop/network/OneDrive/removable drive plus WeChat, QQ, Feishu/Electron, Office/Outlook attachment, and at least one custom-drawn/Qt source where safely available.
- Out to Explorer/Desktop, browser upload, Office, VS Code, Photoshop/available editor.
- Single/multiple, file/folder, resolvable Shell item, virtual-only attachment, file/image/text/URL drag-out and Share, missing during drag, cancellation, right-button drag, elevated boundary.
- For every source, start away from the top edge and record threshold-to-Reveal latency, whether the probe verified/timed out, cursor feedback/flicker, source focus, taskbar/Alt+Tab presence, accepted item count, false reveal, cleanup, and final result. Confirm non-file text/window selection reverses speculative reveal and Classic is never enabled implicitly.
- Race matrix: timeout vs DragEnter, DragEnter vs release, rejection vs new session, monitor switch vs probe creation, Smart → Classic, shutdown while active, OLE callback during cleanup, and accessibility completion before/after pointer release.
- Record Windows build, source application/version, DropSpace build, display/DPI and result; Preview automation alone is not provider compatibility evidence.

### File missing/resilience

- Move, rename, delete, permissions, unplug USB, disconnect NAS, offline OneDrive.
- Verify Missing vs Unavailable language and Locate/Replace.

### Displays and DPI

- 1 and 2+ displays.
- 100%, 125%, 150%, 200%; mixed scale.
- Move window between displays; disconnect the last-used display.
- Taskbar on each edge and auto-hide.
- In Smart mode, drag a real Explorer/Desktop file on each monitor and verify the temporary target appears on that pointer's display without first touching the top edge. Verify DragApproaching, DragReady, post-Drop Compact, Expanded, and Dismissing retain one physical vertical anchor with no jump or clipped Drop region. In Classic mode, verify the legacy edge target separately.
- Exercise the Dynamic Island at every scale and migrate between mixed-scale displays without blur, offset, or crash.
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
- Smart candidate latency and false-reveal rate by evidence path; prove zero probe HWNDs at idle, at most one during a generic session, the configured 60 ms lifetime, and no handle/GDI/USER growth after 1,000 probe lifecycles.
- Interrupt a width/height/radius/content spring in both directions and prove every channel settles with no retained frame subscription.

No performance number is accepted without hardware, build configuration, dataset, and measurement method.

## Security and privacy tests

- Known secret strings never appear in logs, crash markers, notifications, or diagnostic export.
- Clear history removes canonical rows, search results, payloads, and thumbnails.
- Pause race prevents durable commit after completion.
- Malformed relative payload paths cannot escape root.
- Crafted URL schemes are not auto-opened.
- Huge/malformed inputs cannot cause unbounded allocation or queue growth.
- Malformed/oversized CIDA offsets, item counts and PIDLs fail closed; probe classification never calls `GetData` for virtual content and diagnostics contain no dragged path, filename or payload.
- Best-effort app exclusions are tested for false attribution and accompanied by limitation copy.
- Repeat 100 drag/cancel cycles, 10–30 ms cancellation, Exit→new-drag reversal, and 350 ms completion-grace supersession; assert stale session IDs cannot hide or commit newer work.
- Inject bounded pointer pressure while delivering reliable cancel/completion/probe signals; assert critical write failures stay zero during a healthy run and move replacements remain separately diagnosed.
- Verify Quick Panel default/custom hotkey conflict behavior, keyboard navigation, and file/image/text/URL drag-out.
- Verify Automatic/Custom placement on negative coordinates and mixed DPI; Arrow=1 DIP, Shift+Arrow=10, Enter=save, Esc=rollback, direct Adjust Position release/rollback, disconnect/update clamping never mutates saved values, and an unconfigured second monitor remains Automatic.

## Release evidence

For each phase/release retain:

- Build/test command and result.
- Supported OS/SDK/package versions.
- Manual matrix with pass/fail/known limitation.
- Migration fixture results.
- Performance measurements.
- Accessibility and privacy review notes.
- Clean workflow SHA, test totals/TRX, exact EXE/MSIX sizes, Authenticode status, SHA-256 manifest, smoke marker result, release URL, and final default-branch SHA.

The portable smoke harness must prove that the published single `DropSpace.exe` starts, initializes Windows App SDK and SQLite, creates/writes the user AppData tree, resolves the selected resource context across XAML and imperative strings, registers `AddClipboardFormatListener`, writes `A,A,B,A` unique-token text plus a mixed set of two real `StorageFile` objects and one `StorageFolder` through the Windows clipboard, observes `WM_CLIPBOARDUPDATE`, proves the adjacent A is suppressed while the final non-consecutive A is persisted, reads and persists three `Source=Clipboard` file/folder references, verifies Pause/Resume and self-write suppression, removes its test rows, verifies zero classic activation hosts in the fresh Smart default, verifies the observation-only mouse hook and at least one documented accessibility drag WinEvent source are registered, proves hidden top-center `WindowFromPoint` pass-through, proves temporary Compact/Expanded target discovery and synthetic `CF_HDROP`, creates an ephemeral probe and verifies its real hollow Region/native no-activate styles/single ownership/60 ms cleanup/double-dispose plus query-only `CF_HDROP`/Shell/virtual/text classification, executes 100 real Overlay lifecycle/resource cycles and 1,000 interruptible Dynamic Island real XAML/HRGN geometry transitions with zero region failures and no retained frame subscription, redirects a second instance, and exits cleanly. A successful `dotnet publish` without this runtime probe is not release evidence.

The installer lifecycle harness runs only in an isolated Windows account/runner and must: compile a baseline and current Setup from the same AppId; silently install to a custom path; verify x64 EXE metadata, shortcuts and Installed Apps registration; start DropSpace and upgrade it through graceful maintenance shutdown without `/DIR`; preserve an AppData marker and chosen path; run the installed smoke; verify the default startup command; normally uninstall while preserving data and removing startup; reinstall and complete-uninstall while removing only `%LOCALAPPDATA%\DropSpace`; and prove an external sentinel representing an original referenced file remains. The script refuses to run when a pre-existing DropSpace data root exists.

Automation does not claim visual quality or real Explorer/UIA/third-party provider coverage. Before Preview sign-off, a real Windows 11 desktop must verify ordinary clicks/text selection/window drags reverse speculative reveal without lasting obstruction; Explorer/Desktop file/folder/multi-select left/right drags and available WeChat/QQ/Feishu/Electron/Office sources are recorded; Escape/high-speed/cancel recover; the idle top edge resolves to the underlying app; the probe creates no visible flash/focus/taskbar/Alt+Tab entry or unacceptable cursor feedback; Drop Tray can coexist with the offset target; Compact/Expanded direct Drop still works; Classic mode switches live; Dynamic Island motion, mixed-DPI monitors and full-screen behavior remain correct.

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

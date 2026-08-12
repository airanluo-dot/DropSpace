# DropSpace Roadmap

## Current implementation snapshot

The v0.1.0 Stable production slice covers Phases 1–10 plus file capture, startup, branding, and the update foundation from Phases 12–13: application composition, persistence, Space, external drag-out, text/image/file Clipboard, unified search/Pinned, tray/privacy lifecycle, Dynamic Island/Notch, configurable limits, default-on startup, MSIX, portable x64, and Inno Setup. CI/release automation tests portable, installed `/UPDATE`, restart, upgrade and uninstall lifecycles, emits SHA-256 plus an exact update manifest, and publishes Stable or Preview from one SemVer source. Commercial signing remains optional and credential-gated.

Phase 0 boundary adapters are implemented rather than left as throwaway spikes. Their real-target manual matrix—especially hidden-zone Explorer/Desktop drag-in, Overlay drag-out, mixed-DPI geometry, fullscreen behavior, animation feel, and tray recreation after Explorer restart—remains explicit manual Preview evidence.

v0.2.0-preview.1 replaces the default permanent top-edge input band with an experimental, event-driven Smart drag candidate detector and temporary visual OLE target. The old band remains an explicit compatibility option. Real Explorer/Desktop provider coverage and false-positive evidence determine whether Smart mode can graduate beyond Preview.

## Delivery rule

Every phase ends with a buildable, runnable application and a recorded acceptance review. A phase may contain a disposable technical spike, but production behavior is not claimed until its tests pass.

## Phase 0 — Decisions and feasibility spikes

### Goal

Lock supported OS/SDK versions and prove the three risky Windows boundaries before feature construction.

### Features

- Minimal WinUI 3 packaged sample only.
- Spikes: clipboard event/read, external file drag-out, tray icon lifecycle.
- Record target-app compatibility and API limitations.

### Files/modules

Solution skeleton, `spikes/` or short-lived branch artifacts, `DECISIONS.md`, `WINDOWS_INTEGRATION.md`.

### Dependencies

Visual Studio workload, verified supported .NET/Windows App SDK, Windows 11 test machine.

### Tests

Manual matrix for Explorer/Desktop and at least two other drag targets; 1,000 clipboard-event burst harness; Explorer restart tray test.

### Acceptance criteria

- SDK/package builds cleanly on a fresh documented environment.
- Real drag-out succeeds to Explorer or risk is re-scoped before Phase 3.
- Clipboard event pipeline can read text/image without polling.
- Tray icon can add, activate, survive Explorer restart, and remove cleanly.

## Phase 1 — Application foundation

### Goal

Create production solution boundaries without business features.

### Features

App startup, DI composition, logging/redaction, single-instance redirect, theme resources, test projects, CI build command.

### Files/modules

All three production projects and test projects; app manifest/package config.

### Dependencies

Phase 0 version decisions.

### Tests

Core test boot, packaged debug launch, second-instance redirect, log redaction unit tests.

### Acceptance criteria

Clean build with warnings reviewed; launch/close works; no placeholder feature is presented as implemented.

## Phase 2 — Persistence and item core

### Goal

Establish the canonical item model before UI workflows depend on it.

### Features

SQLite v1 schema, repositories, migrations, payload store, settings versioning, recovery shell.

### Files/modules

Core models/policies; Infrastructure database, migrations, payload storage.

### Dependencies

Phase 1.

### Tests

CRUD, transaction rollback, migration from empty/previous fixture, corruption/read-only/disk-full simulation, payload containment.

### Acceptance criteria

Restart preserves fixtures; failed migration preserves original; no raw payload in logs.

## Phase 3 — Space vertical slice

### Goal

Deliver the first genuinely useful file staging workflow.

### Features

Space page, multi-file/folder drag-in, metadata, open/copy path, pin, remove record, empty/error states.

### Files/modules

Space View/ViewModel, drag-in adapter, file service, item row components.

### Dependencies

Phase 2.

### Tests

Core capability tests, mixed-batch integration, manual accessibility/keyboard, restart persistence.

### Acceptance criteria

Dropped references reappear after restart; remove never touches source; partial failures are clear.

## Phase 4 — External drag-out and file resilience

### Goal

Complete Space's defining cross-window workflow.

### Features

External drag-out, missing/unavailable states, Locate/Replace, async thumbnails, network/removable safeguards.

### Files/modules

Drag-out adapter, FileReferenceService, ThumbnailService/cache, details pane.

### Dependencies

Phase 0 drag proof and Phase 3.

### Tests

Target compatibility matrix, missing/move/delete, long path, OneDrive/offline, USB/network timeout, DPI thumbnail tests.

### Acceptance criteria

Explorer/Desktop drag-out passes; failure leaves record/source safe; UI never blocks on remote thumbnail.

## Phase 5 — Clipboard text vertical slice

### Goal

Add event-driven history with privacy controls before images increase risk.

### Features

Recording status, text capture, duplicate/self-loop protection, URL/color hints, pause/resume, retention, clear ranges.

### Files/modules

Clipboard services/pipeline, Clipboard View/ViewModel, classifiers, RetentionService.

### Dependencies

Phase 2 and Phase 0 clipboard proof.

### Tests

Burst, locked clipboard, delayed/stale content, self-copy loop, pause race, retention/clear integration.

### Acceptance criteria

No polling; bounded memory; pause prevents post-completion commits; clear removes search/canonical data.

## Phase 6 — Clipboard images

### Goal

Capture and reuse images within explicit resource limits.

### Features

Image payload storage, thumbnail/preview, copy again, export, byte/pixel/disk budgets.

### Files/modules

Image capture/codec adapter, payload store, preview/export UI.

### Dependencies

Phase 5 pipeline and Phase 4 thumbnail foundation.

### Tests

Large/corrupt/alpha images, memory profiling, disk full, export/copy round trip, deletion cleanup.

### Acceptance criteria

Oversized images are skipped safely; scrolling does not decode full images; clear removes originals and thumbnails.

## Phase 7 — Unified search and Pinned

### Goal

Make the fusion useful without blurring its sources.

### Features

Global search, source/type/status filters, Pinned view, ranking, no-result/loading/error states.

### Files/modules

Search repository/service, search UI, shared item projection, index migration if justified.

### Dependencies

Phases 3–6.

### Tests

Ranking/normalization, performance fixtures, deletion during result, pinned retention, accessibility labels.

### Acceptance criteria

P95 search under 100 ms for 10,000 metadata/text test items on reference device; every result displays source.

## Phase 8 — Tray, close behavior, and privacy polish

### Goal

Support trustworthy long-running operation.

### Features

Tray menu, hide/exit choice, one-time close explanation, listener lifecycle, storage-size view, payload-free diagnostics.

### Files/modules

TrayService, WindowService, settings/privacy pages, lifecycle coordinator.

### Dependencies

Phases 5–7 and tray spike.

### Tests

Explorer restart, hide/open/exit loops, shutdown, recording state sync, no stranded process if tray fails.

### Acceptance criteria

Exit always stops recording; hidden state remains accessible; tray state matches Clipboard header.

## Phase 9 — Accessibility, reliability, and performance

### Goal

Turn the complete feature set into release-quality MVP.

### Features

Keyboard completion, UI Automation, high contrast/reduced motion, crash recovery, cache budgets, localization readiness.

### Files/modules

All UI, resource dictionaries, diagnostics/recovery, performance harness.

### Dependencies

Phases 1–8.

### Tests

Accessibility Insights/manual screen reader, mixed DPI/displays, soak, startup/search/scroll profiling, crash injection.

### Acceptance criteria

All MVP flows pass keyboard-only; no critical accessibility findings; resource budgets documented and met or revised openly.

## Phase 10 — Packaging and release candidate

### Goal

Produce a directly runnable, installable, upgradable, recoverable Preview.

### Features

Portable EXE, MSIX identity/signing plan, installer assets, AppData durability, upgrade/uninstall behavior, privacy statement, release diagnostics, and GitHub Release automation.

### Files/modules

Packaging project/manifest, release scripts/config, release checklist.

### Dependencies

Phase 9.

### Tests

Silent per-user install, custom path inheritance, running-app graceful upgrade, installed smoke, normal uninstall preserving data, complete uninstall, external sentinel protection, MSIX regression, plus manual installer wizard/Installed Apps review.

### Acceptance criteria

Unsigned Setup and portable Preview start without external runtimes/elevation, stable AppId upgrades preserve data/path, independent uninstall works even when the app cannot launch, complete uninstall is root-confined, MSIX remains buildable, release hashes are published, and future signing is credential-gated without repository secrets.

## Phase 10A — Top Overlay delivery

### Goal

Make Temporary Space available as a low-idle, top-edge file drop interaction without replacing the full main window.

### Features

Formal interruptible Overlay state machine; independent per-monitor Win32/OLE drag activation hosts; truly hidden visual HWND; continuously spring-driven Dynamic Island/Notch geometry; Compact/Expanded actions; external drag-out; Reduced Motion; per-monitor DPI placement/topology rebuild; fullscreen suppression; Win32 clipboard listener health and integration smoke.

### Tests

State transition matrix, settings migration/round trip, 100-cycle lifecycle test, WinUI Release build, portable smoke test, and real Windows 11 drag/display/manual matrix.

### Acceptance criteria

Empty idle is visually hidden without a frame loop; standard file drag reveals a valid drop target; item presence controls Compact lifetime; mode changes preserve data/state; all existing main-window/tray/clipboard functions remain shared and available.

## V1.1 phases

### Phase 11 — Global hotkey and Overlay refinements

#### Goal

Provide optional keyboard access and field-driven refinements without creating a duplicate quick-panel product.

#### Features

Configurable `RegisterHotKey`, keyboard-first Overlay expansion, focus restoration, and measured animation/placement refinements.

#### Files/modules

HotkeyService and existing Overlay/WindowService placement/focus extensions.

#### Dependencies

Released MVP search/repositories and performance baseline.

#### Tests

Conflict, IME, rapid invocation, full-screen/elevated app, mixed-DPI/multi-display, focus restore, invocation latency.

#### Acceptance criteria

One panel instance opens on the active work area, reports hotkey conflicts, dismisses reliably, and meets the measured latency target without duplicating storage/search logic.

### Phase 12 — Clipboard file formats delivered; best-effort exclusions remain

#### Goal

Expand clipboard compatibility without turning attribution into a privacy promise.

#### Features

Delivered in Preview.4: Storage-item clipboard capture, separate Clipboard source semantics, folder/reference policy, and configurable item/byte/image limits. Remaining: exclusion settings, Unknown-source attribution behavior, and manual Space intake for text/URLs.

#### Files/modules

Clipboard format adapters, attribution adapter, exclusion policy/settings UI, Space intake commands.

#### Dependencies

MVP clipboard pipeline and completed attribution experiments.

#### Tests

Explorer file copy, virtual/mixed formats, false attribution, password-manager and remote-session limitations, exclusion race, text/URL intake ambiguity.

#### Acceptance criteria

Unsupported/unknown sources remain safe and clearly labeled; exclusions never claim guaranteed protection; file/text/URL records preserve correct source and retention semantics.

### Phase 13 — Startup and update foundation delivered

#### Goal

Improve launch and shell integration only after the utility is stable.

#### Features

Delivered through v0.1.0: default-on per-user startup, hidden `--startup`, portable path reconciliation, uninstall cleanup, Stable/Preview SemVer discovery, startup-only/manual checks, verified streaming download, Inno `/UPDATE`, graceful restart, and deployment-mode separation. Remaining: trusted production signing, richer tray update UX, and Explorer integration behind a separate decision.

#### Files/modules

Activation/startup adapter, packaging/update configuration, tray UI, decision and integration documents.

#### Dependencies

Stable packaged release and real user evidence.

#### Tests

Startup enabled/disabled by Windows, update/rollback, offline launch, Explorer restart, upgrade migration.

#### Acceptance criteria

Actual Windows startup state matches UI, update failure preserves the working install/data, and no Explorer extension ships without a new accepted decision and performance validation.

### v0.1.0 Stable

- Windows 11 Drop Tray compatibility guidance and official Share Target contract.
- Stable, signing-ready external-location identity package integrated with optional Artifact Signing and Inno uninstall.
- Canonical Brand Master pipeline and all Windows icon surfaces.
- Machine-readable update manifests, Stable/Preview channels, verified downloads, and installer upgrade/restart foundation.
- Direct Compact/Expanded Overlay drop with exclusive activation/visual ownership.

### v0.2.0-preview.1 experimental smart drag wake

- Default Hidden idle owns no top-edge hit-test or OLE target window.
- UI Automation drag signals and bounded read-only mouse thresholds recognize Explorer/Desktop candidates; real `CF_HDROP` remains mandatory.
- Temporary Island/Notch target appears on the active drag monitor below the Windows Drop Tray region.
- Smart, Classic top-edge and Disabled modes switch immediately with safe schema migration.
- Graduation gate: real Windows 11 evidence for false positives, Explorer/Desktop coverage, cancellation, Drop Tray on/off, multi-monitor DPI and third-party limitations.
- Serialized Temporary Space projections and Main + Expanded deletion crash stress coverage.

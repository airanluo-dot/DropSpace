# DropSpace Roadmap

## Current implementation snapshot

The v0.2.1 Stable production slice remains the Stable baseline. v0.3.0-preview.5 is the current targeted-hardening Preview: it preserves Preview.4's exclusive direct Dynamic Island placement editing and fixes Automatic projection so the edit starts from the visible surface coordinate while existing Custom coordinates remain unchanged. It also retains stable DisplayConfig identities and runtime-only Custom rejection. The v0.3.0-preview.2 capabilities remain the base: Universal Access, virtual-file materialization, Drop Batch grouping, Quick Panel, best-effort exclusions, Windows Share for file/image/text/URL, and per-monitor Dynamic Island placement. CI/release automation tests portable, installed `/UPDATE`, restart, upgrade and uninstall lifecycles, emits SHA-256 plus an exact update manifest, and publishes Stable or Preview from one SemVer source. Commercial signing remains optional and credential-gated.

Phase 0 boundary adapters are implemented rather than left as throwaway spikes. Automated Windows lifecycle, drag, projection, DPI, update, and packaging coverage remains paired with real-target desktop evidence for Explorer/Desktop drag-in, Overlay drag-out, mixed-DPI geometry, fullscreen behavior, animation feel, and tray recreation after Explorer restart.

v0.2.0 promotes the original event-driven Smart drag candidate detector and temporary visual OLE target to Stable. The old top-edge band remains an explicit compatibility option; v0.3 Smart never falls back to it implicitly. Preview.2 separates drag intent from payload proof, uses official Shell item resolution before its bounded CIDA fallback, and materializes virtual content only after a real Drop.

## v0.3 — Smart Drag Detection v2 Preview sequence

### v0.3.0-preview.1 — Evidence, probe, and classifier foundation

- Keep observing unknown press origins until threshold/release; do not hardcode provider process names.
- Preserve Explorer/Desktop exact-item and documented accessibility drag-start fast paths.
- Model evidence/state/session transitions in Core and reject stale timeout/probe callbacks.
- Reveal speculatively while one 144-pixel hollow, no-activate OLE probe performs query-only verification for at most 60 ms.
- Centralize `CF_HDROP`, Shell IDList, and virtual-file descriptor classification across every native OLE target.
- Prove native styles, real Region hole, registration/revoke, timeout cleanup, double-dispose, and synthetic format negotiation in the Windows executable smoke.

### v0.3.0-preview.2 — Consolidated Smart Drag and Universal Access

- Ship bounded, cancellable, staging-root-confined virtual-file materialization for `FileGroupDescriptorW` + `FileContents` only after a real Drop.
- Split `DragIntentConfidence` from `PayloadConfidence`; accessibility proves intent but unknown payloads still require OLE verification.
- Split lossy coalesced pointer movement from reliable press/cancel/completion/probe signals; new pointer-down supersedes completion grace immediately.
- Ship file/image/text/URL drag-out, manual text/URL intake, packaged Windows Share, Drop Batch metadata/group actions, Quick Panel, exclusions, diagnostics, and custom placement.
- Record the real-Windows provider matrix for Explorer/Desktop, WeChat, QQ, Feishu/Electron, Office/Outlook attachments, mixed DPI, display changes, cursor feedback, race cases, and false reveals.
- Tune probe geometry/lifetime only from evidence; never introduce a permanent Smart hot edge, full-screen transparent target, polling, injection, or elevation.
- Promote to v0.3 Stable only after the provider matrix, privacy/performance gates, upgrade paths, release assets, updater discovery, and website API all pass.

### v0.3.0-preview.3 — Targeted placement and signal hardening

- Replace the bounded `TryWrite` critical lane with an unbounded reliable lifecycle queue while retaining one-slot coalescing for pointer movement and timestamp-ordered merge.
- Add stable DisplayConfig target identity resolution and schema-9 `OverlayPlacements` with best-effort schema-8 HMONITOR migration.
- Keep missing monitor entries Automatic, clamp only the runtime projection, and make Reset monitor-local.
- Add the no-activate **Adjust Island Position…** workflow with physical-pointer-to-DIP preview, one-shot release commit, Smart Drag suppression, and Escape rollback.
- Re-run the existing en-US/zh-CN CI, packaging, updater, and website/release gates; do not expand into a third-party compatibility matrix in this Preview.

### v0.3.0-preview.4 — Targeted placement fix

- Suppress every non-selected monitor island and native drop target for the full placement-edit session; restore all surfaces on commit, cancel, topology change, and shutdown.
- Arm the edit from the clamped runtime projection while retaining saved coordinates as the Cancel rollback source.
- Disable Custom placement, coordinates, Apply, and Adjust for runtime-only monitor identities, with service-level rejection and localized guidance.

### v0.3.0-preview.5 — Automatic placement projection fix

- Include `SurfaceTopOffsetDips` when projecting Automatic placement into Custom coordinates so Adjust begins at the visible Dynamic Island surface.
- Keep Custom placement projection unchanged and limit validation to focused coordinate-conversion regressions.

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

Formal interruptible Overlay state machine; independent per-monitor Win32/OLE drag activation hosts; truly hidden visual HWND; continuously spring-driven Dynamic Island geometry; Compact/Expanded actions; external drag-out; Reduced Motion; per-monitor DPI placement/topology rebuild; fullscreen suppression; Win32 clipboard listener health and integration smoke.

### Tests

State transition matrix, settings migration/round trip, 100-cycle lifecycle test, WinUI Release build, portable smoke test, and real Windows 11 drag/display/manual matrix.

### Acceptance criteria

Empty idle is visually hidden without a frame loop; standard file drag reveals a valid drop target; item presence controls Compact lifetime; mode changes preserve data/state; all existing main-window/tray/clipboard functions remain shared and available.

## V1.1 phases

### Phase 11 — Global hotkey and Overlay refinements (Preview.2 foundation delivered)

#### Goal

Provide optional keyboard access and field-driven refinements without creating a duplicate quick-panel product.

#### Features

Preview.2 delivers configurable `RegisterHotKey`, keyboard-first Overlay expansion, interruptible geometry/shadow motion, and per-monitor custom placement. Focus restoration, explicit hotkey-conflict UI, and broader compatibility measurements remain follow-up refinements.

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

Delivered through v0.3.0-preview.2: Storage-item clipboard capture, separate Clipboard source semantics, folder/reference policy, configurable item/byte/image limits, best-effort Smart Drag exclusions, and manual Space intake for text/URLs. Unknown clipboard-owner attribution remains explicitly non-authoritative.

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
- Documented accessibility drag signals and bounded read-only mouse thresholds recognize Explorer/Desktop candidates; UIA/MSAA perform item hit-testing and real `CF_HDROP` remains mandatory.
- Temporary Dynamic Island target appears on the active drag monitor below the Windows Drop Tray region.
- Smart, Classic top-edge and Disabled modes switch immediately with safe schema migration.
- Graduation gate: real Windows 11 evidence for false positives, Explorer/Desktop coverage, cancellation, Drop Tray on/off, multi-monitor DPI and third-party limitations.
- Serialized Temporary Space projections and Main + Expanded deletion crash stress coverage.

### v0.2.0-preview.2 field hotfix

- Correct Smart detector COM apartment ownership on the asynchronous classifier thread.
- Recognize Explorer/Desktop file item ancestors without accepting blank Shell surfaces; retain system threshold and final OLE validation.
- Add a versioned official website release API, runtime website refresh, GitHub Pages mirror and resilient in-app metadata fallback.
- Graduation remains blocked on new real Windows 11 Explorer/Desktop evidence; Stable stays v0.1.0.

### v0.2.0-preview.3 field hotfix

- Add independent `EVENT_OBJECT_DRAGSTART/CANCEL/COMPLETE` and provider-supplied `EVENT_SYSTEM_DRAGDROPSTART/END` signals so transient UIA/MSAA item hit-test failure no longer leaves Smart mode completely silent.
- Retain verified Explorer/Desktop file-view origin evidence while requiring exact item evidence for mouse-threshold-only fallback and real OLE `CF_HDROP` for acceptance.
- Use one DPI-aware Drop Tray-safe anchor for every Dynamic Island state; enlarge the host so offset Expanded geometry and the native target region cannot diverge.
- Add Windows smoke health gates for the observation-only mouse hook and object-drag event hook plus Core placement/DPI regressions.
- Graduation remains blocked on real Windows 11 Explorer/Desktop event delivery, false-positive evidence, mixed-DPI visual alignment, and Drop Tray coexistence; Stable stays v0.1.0.

### v0.2.0-preview.4 single-Island and brand refresh

- Keep the field-confirmed Smart drag detector unchanged while removing the former Notch visual mode, Settings selector, transition state, asymmetric region path, and mode-specific geometry.
- Migrate legacy settings safely to one rounded Dynamic Island surface without changing Smart, Classic compatibility, Disabled, OLE `CF_HDROP`, source-file safety, DPI placement, or Hidden behavior.
- Adopt the final true-alpha transparent logo across every active App, taskbar, tray, installer, MSIX, Share identity, documentation, and website surface while retaining prior generated assets only in an inactive provenance archive.
- Fix the API-updated Stable status to one 7-pixel green dot with horizontal copy, and redesign the website showcase around Compact, Drop Ready, and Expanded Dynamic Island states.
- Promote GitHub Pages to the single official website and versioned release API, remove the unavailable Cloudflare endpoint from App update checks, and preserve GitHub Releases as the validated fallback.
- Publish the Preview installer, portable EXE, unsigned MSIX, SHA-256 checksums, and update manifest after the full Windows release and lifecycle suite passes; Stable remains v0.1.0.

### v0.2.0-preview.5 fail-closed website release metadata

- Require a fresh, contract-validated GitHub Releases response before the official GitHub Pages site can deploy.
- Keep the previous known-good Pages deployment live instead of rebuilding from stale fixture data when synchronization fails.
- Remove the obsolete Cloudflare Pages release API and add network, HTTP, JSON, provenance, asset, and fixture-isolation regressions.

### v0.2.0-preview.6 release consistency and Stable candidate

- Derive the updater summary from the matching release notes and reject mismatches across the canonical version, notes, README, ROADMAP, and generated manifest.
- Verify every publication end to end across the GitHub Release, five public assets, checksums, manifest, official website API, and Stable website presentation.
- Apply explicit short retention to disposable Actions artifacts while preserving public GitHub Release assets.
- Keep the user-confirmed Smart drag detector and single Dynamic Island behavior unchanged as the final v0.2.0 Stable candidate.

### v0.2.0 Stable

- Promote the user-confirmed Smart drag architecture without changing its bounded signal, cancellation, final `CF_HDROP`, privacy, or compatibility-mode behavior.
- Ship the Dynamic-Island-only interface, final transparent branding, GitHub Pages release API, GitHub Releases fallback, fail-closed production synchronization, and cross-surface release consistency as one Stable build.
- Publish a separately built Stable installer, portable EXE, unsigned MSIX, SHA-256 checksums, and update manifest; Stable and Preview channels both select v0.2.0 over the older Preview sequence.

### v0.2.1-preview.1 API-driven latest changes

- Add a dedicated, versioned `/api/v1/latest-change.json` website presentation contract derived from validated GitHub Releases without changing the App's schema-v1 update API.
- Keep the large “What's new” headline design while updating its channel-aware headline, release title, date, official link, and variable-length bilingual highlights at build time and runtime.
- Preserve the last validated build snapshot when runtime refresh fails, and verify the new endpoint plus live homepage after every release.
- Keep Smart drag, the Dynamic-Island-only App interface, local data behavior, packaging, and update selection unchanged.

### v0.2.1-preview.2 English localization foundation

- Establish `en-US` as the complete base resource set and ship a synchronized `zh-CN` resource set for the main window, Dynamic Island, native tray, update feedback, error dialogs, and accessibility names.
- Persist System default, English, and Simplified Chinese display-language choices; System follows Windows display language for Chinese and uses English for the other currently shipped-language cases. The selection takes effect on restart.
- Add a localization policy gate that rejects new CJK hardcoding in source, proves resource-key parity, and runs the full Windows CI workload in both resource contexts.
- Require actual English and Simplified Chinese Windows 11 display-language validation as Preview evidence; CI resource contexts do not substitute for operating-system locale testing.

### v0.2.1-preview.3 Zero-flicker Windows startup

- Keep the main-window HWND available to background services without initially showing or activating it for `--startup`.
- Preserve normal launch, tray Open, redirected activation, Share Target, clipboard, and Dynamic Island behavior.
- Add Win32 startup main-window visibility regression coverage in English and Simplified Chinese portable smoke runs.

### v0.2.1 Stable

- Promote the release-driven latest-changes presentation, complete English/Simplified Chinese resource foundation, and zero-flicker Windows sign-in startup from the validated v0.2.1 Preview sequence.
- Preserve the v0.2.0 Smart drag, single Dynamic Island, local-first data boundaries, installer/portable/MSIX paths, and strict Stable/Preview update ordering without behavior changes.
- Rebuild and publish a separate Stable installer, portable EXE, unsigned MSIX, SHA-256 checksums, and update manifest from the v0.2.1 source commit; do not rename or reuse Preview assets.

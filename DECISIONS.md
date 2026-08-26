# DropSpace Architecture and Product Decisions

Decisions use: Proposed, Accepted, Superseded, Rejected. Changing an Accepted decision requires a new entry that links back to it.

## D-001 — Product identity and source separation

- Status: Accepted
- Context: File staging and clipboard history can feel like unrelated tools.
- Decision: Define DropSpace as one temporary content workspace; keep Space deliberate and Clipboard automatic, while sharing item/search/action foundations.
- Reason: Common job and mechanics justify fusion; visible source boundaries prevent confusion.
- Alternatives: Separate apps; one undifferentiated feed.
- Trade-offs: More navigation than a single feed; less cross-app confusion and safer retention semantics.

## D-002 — Pinned is state, not storage

- Status: Accepted
- Context: Copying items into a Pinned collection creates identity/deletion ambiguity.
- Decision: `IsPinned` is a retention/filter property on the same item.
- Reason: One source of truth and predictable unpin behavior.
- Alternatives: Junction table or cloned item.
- Trade-offs: Future multiple collections/tags need a separate model.

## D-003 — Quick Panel moves to V1.1 (superseded by D-044 for Preview.2)

- Status: Accepted
- Context: Quick Panel adds separate-window focus, hotkey conflicts, multi-display positioning, and performance work.
- Decision: MVP ships main window + tray; Quick Panel and global hotkey follow after core reliability.
- Reason: Core value can be validated without the highest UI integration risk.
- Alternatives: Include in MVP; drop permanently.
- Trade-offs: MVP is less instant, but more achievable and testable.

## D-004 — Composition over model inheritance

- Status: Accepted
- Context: Items share lifecycle fields but have different payload shapes.
- Decision: One `DropItem` aggregate with optional typed payload components and capability policies.
- Reason: Clean SQLite mapping, unified views, less subclass/null-table complexity.
- Alternatives: Class-table inheritance; one JSON blob; separate aggregate per type.
- Trade-offs: Requires invariant validation so kind and payload remain consistent.

## D-005 — SQLite rather than JSON

- Status: Accepted
- Context: Search, retention, pinning, pagination, migrations, and atomicity exceed a simple settings file.
- Decision: Use SQLite through `Microsoft.Data.Sqlite`; store large payloads in app-owned files.
- Reason: Indexed queries, transactions, bounded updates, explicit migrations.
- Alternatives: JSON files; EF Core; embedded document DB.
- Trade-offs: Schema/migration work; lower complexity than syncing whole JSON or adding ORM.

## D-006 — Explicit SQL, no EF Core in MVP

- Status: Accepted
- Context: Schema is small and performance/startup should remain visible.
- Decision: Parameterized SQL repositories with small mapping helpers.
- Reason: Fewer dependencies and predictable migrations/queries.
- Alternatives: EF Core.
- Trade-offs: More manual mapping; simpler runtime and migration ownership.

## D-007 — MSIX packaged application

- Status: Superseded by D-019; retained as the alternative deployment
- Context: App needs installation, identity, lifecycle, and future startup/update behavior.
- Decision: Retain packaged WinUI 3/MSIX, but it is no longer the preferred ordinary-user download.
- Reason: Predictable deployment and app-local storage/identity.
- Alternatives: Unpackaged self-contained installer.
- Trade-offs: Packaging/signing complexity and some shell integration constraints.

## D-019 — Portable EXE established the unpackaged Preview payload

- Status: Accepted and implemented; ordinary-user recommendation superseded by D-026
- Context: Ordinary users must be able to download one file and run without installing .NET, Windows App SDK Runtime, a certificate, or an MSIX package.
- Decision: Publish win-x64 with `WindowsPackageType=None`, .NET and Windows App SDK self-contained, single-file bundling, and content self-extraction. Keep MSIX as an alternative build.
- Reason: The Preview's lowest-friction path is `DropSpace.exe` while one codebase and deployment abstraction preserve package support.
- Trade-offs: Larger executable, first-run extraction, and unsigned Preview SmartScreen reputation.

## D-020 — Overlay is a state projection, not another item system

- Status: Accepted and implemented
- Context: The top interaction must not duplicate Temporary Space or clipboard logic.
- Decision: Keep lifecycle in a pure Core state machine and project the existing repository/use cases through one Overlay ViewModel.
- Reason: Interruptible transitions are testable and every surface observes the same item count/data.
- Trade-offs: Window/animation adapters must carefully ignore stale completions.

## D-021 — Hidden drag reveal uses event-driven tool windows

- Status: Accepted and implemented
- Context: A destroyed/fully absent HWND cannot receive drag entry, while global cursor polling and low-level hooks are inappropriate for an idle resident utility.
- Decision: Maintain a transparent 3-DIP top-center `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` drop zone per enabled monitor, registered with WinUI/OLE standard file drag/drop.
- Reason: It is invisible, focus-safe, monitor-specific, and dormant until Windows sends a drag event.
- Trade-offs: A deliberately tiny top-edge region still owns hit testing and requires real Explorer/DPI compatibility validation.

## D-022 — WinIsland is behavioral research only

- Status: Accepted
- Context: WinIsland demonstrates fluid island geometry but is GPL-3.0 and uses a Rust/winit/Skia/D3D architecture unlike DropSpace.
- Decision: Reference only public interaction ideas: continuous spring targets, compact/expanded lifecycle, frame scheduling only while active, edge placement, and fullscreen suppression. No WinIsland source, translated code, assets, algorithms, or runtime are copied into DropSpace.
- Reason: Independent C#/WinUI/Composition implementation preserves DropSpace's architecture and avoids creating a GPL-derived work.
- Trade-offs: Equivalent behavior must be designed and tested independently; WinIsland's implementation details are not reusable.
- Preview.2 review record: public commit `6b5745bdce434a33753e4c328479d5bb35834f6d` was inspected, especially `window/app/startup.rs`, `layout.rs`, `frame.rs`, `events.rs`, `utils/physics.rs`, and the rounded background renderer. The independent design lessons were: keep a transparent undecorated maximum host around a separately drawn rounded surface; drive width/height/radius/view/hide as replaceable spring targets; restrict pointer hit testing to visible geometry; use event-loop wait deadlines and request redraw only for animation/playback/interaction; and keep monitor placement in physical coordinates. No GPL implementation text, constants, control flow, renderer code, assets, or dependencies were copied.
- Preview.4 review record (2026-08-09): the current public repository and its reorganized `src/window`/`src/ui` structure were re-reviewed, including the window app's separate window/renderer state, compact/expanded/visible targets, spring collection, timestamps, and next-frame scheduling. DropSpace retained only behavioral lessons—one surface hierarchy, replaceable targets, render scheduling while unsettled, and physical monitor placement. The 12-pixel OLE activation band, native `IDropTarget`, WinUI/HRGN morph, responsive layout, and all C# code were independently designed; no GPL source, translated control flow, constants, algorithms, assets, or dependencies were used.

## D-023 — Desktop clipboard notification uses the Win32 listener list

- Status: Accepted and implemented; supersedes the notification-source portion of D-008
- Context: Real unpackaged `v0.1.0-preview.1` testing showed no captures even though the WinRT `Clipboard.ContentChanged` delegate was attached; the UI had no registration health and could falsely say Recording.
- Decision: Register the stable main HWND through `AddClipboardFormatListener`, handle `WM_CLIPBOARDUPDATE` via `SetWindowSubclass`, and attach `GetClipboardSequenceNumber` to bounded capture signals. Continue using WinRT only to read/normalize actual text and bitmap formats.
- Reason: This is the standard desktop notification contract, survives main-window hide-to-tray, exposes registration failure, and remains event-driven.
- Trade-offs: Snapshot reads still race clipboard ownership and therefore use bounded retry plus sequence-change cancellation.

## D-024 — Native drag activation and visual Overlay are separate HWND lifecycles

- Status: Superseded by D-027; the separate-lifecycle boundary remains
- Context: The shared 280 × 3 XAML window was nearly impossible to hit and `Hidden` still showed a real HWND, producing a white line/transparent rectangle on Windows 11.
- Decision: Truly hide and empty-region the visual HWND. Keep a separate zero-alpha 680 × 72 DIP per-monitor native host registered through OLE `IDropTarget`/`RegisterDragDrop`; register the visual HWND with the same target adapter for handoff during Reveal. Both targets accept `CF_HDROP` with copy semantics and invoke the existing `AddPathsAsync` use case.
- Reason: A practical activation area no longer requires a visible XAML/DWM host, and diagnostics distinguish target discovery, data format, reveal, Drop, and repository acceptance.
- Trade-offs: Zero-alpha cross-process OLE targeting and `HTTRANSPARENT` click-through still require real Explorer/Desktop validation on the supported Windows builds; no polling/hook fallback is introduced.

## D-026 — Stable per-user Inno Setup lifecycle is the recommended channel

- Status: Accepted and implemented
- Context: A normal Windows application needs Installed Apps registration, a standalone uninstaller, custom path, in-place upgrade, downgrade protection, and explicit data retention independent of whether the main EXE can launch.
- Decision: Build `DropSpaceSetup.exe` with pinned Inno Setup 7.0.2 and permanent AppId `E11EC281-BCE7-4F98-8EEF-2387E202CF0F`. Install the existing self-contained x64 EXE per user at `%LOCALAPPDATA%\Programs\DropSpace` by default; preserve custom paths and `%LOCALAPPDATA%\DropSpace` through upgrades; keep Portable EXE and MSIX assets.
- Reason: It gives Windows-standard install/upgrade/uninstall behavior without administrator rights or another application runtime.
- Trade-offs: Preview artifacts are unsigned and can trigger SmartScreen. CI must test the installer itself, not only compilation.

## D-027 — OLE activation uses a one-pixel discoverable hot edge and single-owner expansion

- Status: Superseded by D-028; the single-owner and `HTCLIENT` decisions remain
- Context: Real Preview.2 Explorer/Desktop testing showed that `RegisterDragDrop` success did not yield `DragEnter`; `HTTRANSPARENT` made cross-thread target discovery skip the zero-alpha HWND, and overlapping activation/visual targets created handoff ambiguity.
- Decision: Return `HTCLIENT`, use visually imperceptible but nonzero uniform alpha 1/255, and reduce idle interception to one physical top-edge pixel across 960 DIP. Fully transparent layered HWNDs are skipped by target discovery. On valid OLE entry, expand the same HWND to 760 × 112 DIP, keep it above the visual Overlay and own the operation through Drop/Leave. Let the shaped visual HWND own direct drops only when already visible.
- Reason: `WindowFromPoint`/OLE can discover a real registered target, while normal non-drag input loses at most the single topmost physical row and no polling/hook is needed.
- Trade-offs: The exact top pixel is intentionally owned by DropSpace; real Explorer/Desktop and maximized-title-bar behavior remains manual acceptance evidence.

## D-028 — OLE activation uses a bounded edge band and never re-rejects an accepted owner

- Status: Accepted and implemented; supersedes the geometry and final-Drop acceptance portions of D-027
- Context: Real Preview.3 testing still produced no reveal. A one-physical-pixel target required the Explorer cursor hotspot to land on the exact first scan line. Separately, final `Drop` re-evaluated a smaller Ready rectangle while spring geometry and pointer samples were changing, so an operation that had already entered the target could end with `DROPEFFECT_NONE`.
- Decision: Keep `HTCLIENT`, nonzero 1/255 alpha, no polling/hooks, and single native OLE ownership, but expose a bounded 960-DIP × 12-physical-pixel monitor-edge safety band. Expand the same owner to 840 × 144 DIP after accepted `CF_HDROP`. Once this owner accepts the operation, final Drop depends on valid extracted paths, not a second animated-edge hit test. The already-visible shaped Overlay remains an independent direct target.
- Reason: Real cursor hotspots can reach the target and accepted Explorer operations are not lost during visual morphing, while ownership and persistence still converge on `MainViewModel.AddPathsAsync`.
- Trade-offs: The bounded top-center band intentionally owns ordinary hit testing in those 12 physical rows. It is no-activate and visually imperceptible, but its interaction trade-off and real Explorer/Desktop delivery remain Windows 11 manual acceptance items.

## D-029 — Clipboard file history reuses references but not Temporary Space identity

- Status: Accepted and implemented
- Context: Explorer file copies belong in Clipboard History, but automatic history must not make the file an actively staged Temporary Space item or copy/move the source.
- Decision: Read `StandardDataFormats.StorageItems` before bitmap/text, inspect each external file/folder through `IFileReferenceService`, and store it in the existing item/reference schema with `Source=Clipboard`. Keep normalized-path reuse only for deliberate Temporary Space staging; Clipboard capture events remain separate rows unless the consecutive-capture coordinator suppresses the immediately repeated snapshot. Never enumerate captured folders, and expose validated user limits for item count and known bytes.
- Reason: File availability, Copy again, Open, Show in folder, retention, search, pinning, and SQLite migrations reuse established behavior while the product surfaces remain unambiguous.
- Trade-offs: Virtual shell objects without stable file-system paths are skipped; folder contents do not contribute to known-size limits.

## D-030 — Executable resources are the desktop icon authority

- Status: Accepted and implemented
- Context: A self-contained single-file runtime does not guarantee that `Assets/AppIcon.ico` exists beside the process. Path-only window/tray loading therefore produced a generic taskbar icon on another machine.
- Decision: Treat the multi-resolution canonical ICO embedded as Win32 resource 101 as the window/taskbar/tray authority. Inno shortcuts and Installed Apps point to `DropSpace.exe` icon index 0; loose WinUI/MSIX assets remain packaging derivatives documented in `BRAND_ASSETS.md`.
- Reason: Portable and installed builds obtain the same icon without depending on extraction layout or the current directory.
- Trade-offs: Branding changes must regenerate the canonical ICO and MSIX PNG derivatives, then pass automated PE/icon-chain and manual Windows cache/scaling checks.

## D-031 — Startup is a default-on per-user preference owned by the app

- Status: Accepted and implemented
- Context: DropSpace clipboard/tray behavior is useful only when the user can opt into sign-in launch without elevation, and Portable paths can move.
- Decision: Persist `StartWithWindows=true` by default and reconcile the exact quoted current EXE plus `--startup` in the current user's Run key on launch/settings changes. Apply external registration before settings replacement and roll it back if the settings transaction fails. Start hidden to tray; uninstall removes only DropSpace's value.
- Reason: It works for Installed and Portable builds with ordinary user rights, preserves the user's explicit disable choice through upgrades, and automatically repairs a moved Portable path after manual launch.
- Trade-offs: Windows can independently disable startup apps, and real sign-in behavior remains a manual acceptance item.

## D-025 — Overlay morphs real geometry through interruptible spring targets

- Status: Accepted and implemented
- Context: Preview.1 assigned final Width/Height/CornerRadius first and animated only scale/opacity, visibly jumping to a rectangular endpoint.
- Decision: Keep a bounded fixed host during transitions and integrate visible width, height, top offset, top/bottom radius, opacity, content reveals, and drop scale with a real-time damped spring. Update the shaped HRGN and XAML geometry each frame; stop `CompositionTarget.Rendering` immediately on settlement/Hidden.
- Reason: New targets retain current values and velocity, so reversal and mode changes are continuous rather than queued storyboards.
- Trade-offs: Geometry frames perform bounded XAML/HRGN updates only during transitions; real-device animation feel and edge antialiasing remain visual acceptance items.

## D-008 — Event-driven clipboard capture

- Status: Accepted
- Context: Long-running utility must remain efficient.
- Decision: Use clipboard change events plus a bounded serialized ingestion channel; no polling.
- Reason: Lower idle cost and correct platform pattern.
- Alternatives: Timer polling; separate Windows service.
- Trade-offs: Events can race with delayed/replaced content and require careful recovery.

## D-009 — App exclusions are best effort and V1.1 (delivery timing superseded by D-044)

- Status: Accepted
- Context: Clipboard owner HWND/process attribution is incomplete and sometimes indirect.
- Decision: Do not ship exclusions in MVP; when added, label them best effort and preserve Pause as reliable control.
- Reason: Avoid a false security promise.
- Alternatives: Ship exclusion as guaranteed; omit permanently.
- Trade-offs: MVP captures more broadly; honest controls reduce misleading protection.

## D-010 — File references are paths with explicit availability state

- Status: Accepted
- Context: Windows does not provide a universal durable identity that follows arbitrary moves across providers.
- Decision: Store reference path + observed metadata; mark Missing/Unavailable and offer Locate/Replace.
- Reason: Honest and implementable.
- Alternatives: Copy files into app; broad file watchers; filesystem-ID tracking.
- Trade-offs: Moved files need user repair; source custody remains safe.

## D-011 — Source files are never deleted by record actions

- Status: Accepted
- Context: A staging utility must not surprise users with destructive behavior.
- Decision: No source-file deletion/move command in MVP; “Remove from DropSpace” affects only app data.
- Reason: Preserves user trust and product boundary.
- Alternatives: Optional delete/move actions.
- Trade-offs: Users use Explorer for file management.

## D-012 — Local storage without vault claims

- Status: Accepted for MVP
- Context: Clipboard data can be sensitive; application encryption has key/search/recovery trade-offs.
- Decision: User-scoped local storage, finite retention, redacted logs, no network. Evaluate DPAPI-backed protection later against a defined threat.
- Reason: Honest baseline without a misleading “encrypted vault” claim.
- Alternatives: Encrypt all payloads now; store only in memory.
- Trade-offs: Same-account malware/admin remains a risk; product remains usable/searchable.

## D-013 — Pause persists across restart

- Status: Accepted
- Context: Automatically resuming after a privacy pause can surprise users.
- Decision: Paused state persists until explicit resume, including app restart.
- Reason: Privacy intent outlives process lifecycle.
- Alternatives: Resume every launch; time-limited pause.
- Trade-offs: Users may forget recording is paused; persistent visible status mitigates it.

## D-014 — Retention defaults

- Status: Accepted, subject to usability testing
- Context: Unlimited history creates privacy and disk growth.
- Decision: 30 days or 1,000 unpinned clipboard items; byte/pixel limits; pinned exempt; Space does not auto-expire.
- Reason: Bounded useful default with predictable mental model.
- Alternatives: Unlimited; seven days; size-only.
- Trade-offs: Some desired old history expires; settings can adjust later.

## D-015 — Mica foundation, Acrylic transient only

- Status: Accepted
- Context: The app should look native without excessive transparency.
- Decision: Mica primary backdrop, Acrylic primarily for transient system surfaces, full solid/high-contrast fallback.
- Reason: Aligns with Windows guidance and performance/readability.
- Alternatives: Acrylic whole window; opaque custom dashboard.
- Trade-offs: Less visual spectacle, better consistency.

## D-016 — No network metadata in MVP

- Status: Accepted
- Context: Favicons/page titles create network/privacy/caching complexity.
- Decision: URL presentation uses locally parsed host/display URL only.
- Reason: Keeps local-only promise simple.
- Alternatives: Fetch favicon/title automatically.
- Trade-offs: Less rich previews.

## D-017 — Future AI is explicit action, not background processing

- Status: Accepted as future guardrail
- Context: AI may later summarize/translate selected items.
- Decision: Any future network AI action is user-invoked, shows provider/data boundary, and is architected after MVP through action interfaces only when needed.
- Reason: Avoid overengineering and silent disclosure.
- Alternatives: Background AI classification; build plugin framework now.
- Trade-offs: Later integration work, lower current complexity/risk.

## D-018 — Windows 11 24H2 and current stable toolchain baseline

- Status: Accepted
- Context: WinUI packaging and Windows integration behavior require one reproducible target rather than an open-ended SDK range.
- Decision: Target `net10.0-windows10.0.26100.0` and Windows 11 build 26100 or later; use .NET 10, Windows App SDK 2.3.1, Windows SDK Build Tools 10.0.26100.8249, CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 10.0.10, and SQLitePCLRaw bundle 2.1.12 to replace its vulnerable 2.1.11 native transitive dependency. CI builds x64; the solution also defines ARM64.
- Reason: These are stable servicing releases aligned with the supported Windows 11 24H2 SDK baseline and the repository's WinUI 3 requirements.
- Alternatives: Older .NET/Windows App SDK LTS baseline; unpackaged deployment; floating package versions.
- Trade-offs: Windows 10 and pre-24H2 Windows 11 builds are unsupported; explicit package pins require deliberate servicing updates.

## D-032 — Treat Windows Drop Tray as a cooperating Shell owner, not a window to defeat

**Status:** Accepted; the default passive-host portion is superseded by D-037 for v0.2 Preview. Share/Drop Tray boundaries remain current.

**Decision:** Preserve the native top-edge host only as explicit Classic compatibility mode, add honest Settings guidance, and implement the public Windows Share Target contract behind a trusted external-location identity. Never poll, inject, probe undocumented flags, import certificates or fight Shell z-order. D-037 permits observation-only low-level hooks for bounded session signals; they never suppress input. A Share Target is guaranteed in the Share UI; direct placement in Drop Tray suggestions is not guaranteed.

**Rationale:** Drop Tray and DropSpace intentionally occupy the same user gesture. Windows Shell can acquire OLE ownership first, and no application-local HWND style can reliably override that without harming the desktop. Package identity/signature is the supported integration boundary.

## D-033 — Visible input follows visible pixels; Temporary Space projections are serialized

**Decision:** Stable Compact/Expanded disables the passive host, applies WinUI `AllowDrop` to the visible surface and retains a native root `CF_HDROP` adapter. Repository mutation publishes one monotonically increasing Space revision. Overlay refresh is serialized/coalesced and synchronizes cards by identity on the UI Dispatcher.

**Rationale:** Preview.4 registered the root HWND but visible WinUI pixels could resolve to a child HWND, while the passive topmost host could compete. The deletion crash path separately allowed one removal to start two repository refreshes and overlapping `ObservableCollection.Clear/Add` during Dismissing. Exclusive target ownership and a single projection coordinator remove both races without duplicating storage logic.

## D-034 — License DropSpace's original work under Apache-2.0

**Decision:** DropSpace's original source, documentation, configuration, tests, scripts, and repository-owned brand image files are licensed under Apache License 2.0, with `Copyright 2026 Airan Luo`. Contributions use Apache-2.0 inbound and outbound terms without a CLA. Third-party dependencies retain their own licenses, and trademark/source-identification policy remains separate from copyright licensing.

**Rationale:** Apache-2.0 permits source access, forks, modification, redistribution, and commercial use while providing explicit patent terms and preserving NOTICE and attribution requirements. A project-level SPDX policy avoids mechanical headers on generated, binary, manifest, and third-party files.

**Audit boundary:** Git history contains only the project owner's `Luo Airan` and `Aren Vox` identities plus GitHub merge automation. No vendored third-party source or visual asset was found. WinIsland remains a GPL-3.0 behavioral reference only under D-022 and is not incorporated. Runtime/build dependencies remain governed by [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## D-035 — Narrow, opt-out GitHub Release update boundary

**Decision:** D-012/D-016 continue to prohibit content/network enrichment, telemetry, accounts and sync, but v0.1.0 adds one explicit update-only boundary. When enabled, one process-start check and user-invoked checks read at most 20 public releases from the versioned official website API, its GitHub Pages mirror, or the official GitHub REST API. No Clipboard content, Temporary Space path, filename, query, username, machine ID or install identifier is sent. Stable accepts only Stable versions; Preview accepts all releases; both choose the highest SemVer strictly above the running version.

**Security boundary:** Remote metadata cannot provide arbitrary execution URLs. A bounded, exact-schema `update-manifest.json` names only fixed official assets. Downloads remain inside `%LOCALAPPDATA%\DropSpace\Updates`, stream to `.download`, require exact size and SHA-256, and are atomically promoted. Integrity is not publisher authenticity; unattended installation additionally requires WinVerifyTrust and the compiled DropSpace signer identity. Unsigned builds require explicit user installation.

**Rationale:** A first Stable desktop application needs an understandable upgrade path without a service, timer, scheduled task, tracking identity, or silent trust downgrade. The abstraction keeps Portable and Windows-managed Package deployments from invoking Inno Setup.

## D-036 — Clipboard duplicate suppression is consecutive, observed, and transactional

**Decision:** Reuse each snapshot's canonical `FingerprintService` identity inside a serialized process-local coordinator. Suppress only when the current fingerprint equals both the immediately previous observed identity and the last successfully persisted identity. A different supported or policy-rejected observation ends the run; a failed commit never marks success. Clear History and Resume reset the coordinator. SQLite stores valid non-consecutive repetitions and performs no historical fingerprint lookup or time-window collapse.

**Rationale:** Windows can emit repeated notifications for one effective state, but Clipboard History is chronological rather than a set. This yields `A,A → A` and `A,B,A → A,B,A`, avoids races and time heuristics, keeps failures retryable, and requires no schema or duplicate counter.

## D-037 — Smart drag candidates replace the default permanent top-edge target

**Decision:** Smart mode is the v0.2.0-preview.1 default. Hidden idle creates no topmost edge HWND and no registered OLE target. Public accessibility drag signals are combined with an observation-only low-level mouse/keyboard queue, Explorer/Desktop file-view classification and Windows `SM_CXDRAG`/`SM_CYDRAG` thresholds. A candidate reveals the normal shaped visual Overlay on the pointer's display, offset below Drop Tray, and temporarily calls `RegisterDragDrop`. Only a real OLE `IDataObject` containing `CF_HDROP` can be accepted. The previous edge host remains user-selected Classic compatibility mode; Disabled creates neither automatic-wake mechanism.

**Rationale:** The old host was reliable after Explorer selected it, but permanently owned a real top-edge hit-test band and competed with title bars and Windows Drop Tray. Mouse-down or movement alone creates unacceptable false positives, while UIA events alone are optional provider behavior. Layered evidence bounds the experiment without moving final file validation out of OLE.

**Constraints and trade-offs:** No Explorer injection, Shell/registry hacks, input suppression, undocumented Drop Tray detection, persistent polling or path-bearing diagnostics. Unknown third-party and virtual-file sources may not wake Smart mode. Real Windows 11 evidence, not CI alone, determines whether this experiment graduates. Classic mode remains the explicit reliability/input-area trade-off.

## D-038 — Repair Smart source evidence and publish a versioned release API

**Decision:** Explorer/Desktop source classification initializes COM on every actual asynchronous classifier thread and walks a bounded UI Automation raw-view ancestor chain from `ElementFromPoint` to the enclosing ListItem/TreeItem/DataItem. It does not accept blank Shell surfaces. The initial Preview.2 website architecture exposed schema-v1 release metadata through Cloudflare and GitHub Pages while requiring exact same-release GitHub asset identities. The Cloudflare implementation was later retired when GitHub Pages became the single official website; D-041 records the current deployment authority.

**Rationale:** Preview.1 initialized COM only on the hook/message thread while classification ran after an `await` on a thread-pool thread. It also assumed the deepest hit-tested UIA element was the item, although Explorer commonly returns a nested text/image child. Both caused safe evidence to collapse to `Unknown`, so no Island appeared. Separately, anonymous GitHub REST requests share a per-IP rate limit and may be blocked even when public Release downloads work. A first-party, versioned read-only contract provides a stable future integration seam without weakening the network-to-execution boundary.

**Constraints:** UI Automation still supplies only candidate evidence. Movement must cross system drag thresholds and OLE `CF_HDROP` remains final authority. The API cannot name arbitrary URLs, exposes no user data, has no mutable client identity, and does not replace size, SHA-256 or future trusted-publisher verification.

## D-039 — Promote verified Shell drags with documented object events and keep one visual anchor

**Decision:** Smart mode registers out-of-context `SetWinEventHook` observers for the documented `EVENT_OBJECT_DRAGSTART/CANCEL/COMPLETE` range and the supplemental provider-supplied `EVENT_SYSTEM_DRAGDROPSTART/END` range on its existing message thread. Mouse-down classification now retains two distinct facts: a known Explorer/Desktop file-view surface and, when available, an exact UI Automation or MSAA item. A strong drag event may promote a verified explorer.exe source root even if the exact item leaf or hosted-window class is transiently unavailable; mouse-threshold-only promotion still requires exact item evidence. Duplicate WinEvent, pointer, OLE and timeout signals converge through the serialized `DragSessionPolicy`. OLE `CF_HDROP` remains the only content acceptance boundary.

The deprecated root-wide `UiaAddEvent` subscription is not part of observer startup. All native observers register before the thread reports readiness and enters `GetMessage`; UIA/MSAA are bounded point classifiers on the serialized worker. Registration status records each source and its immediate Win32 error so an inert Smart mode is diagnosable instead of appearing healthy.

Smart placement is also state-independent. A single DPI-aware policy computes the display-mode gap plus the 76-physical-pixel Drop Tray compatibility offset, and every visible/morphing state uses that same anchor. The fixed host is tall enough for the offset 340-DIP Expanded surface at all supported scales; its HRGN remains limited to the current visible rounded geometry.

**Rationale:** Preview.2 still treated one asynchronous UIA item hit-test as an effective gate. Once Explorer entered its modal OLE loop, that inspection could fail and the later signal reclassified the cursor after it had left the source item, producing no candidate. Preview.3's first implementation also performed deprecated root-wide UIA event registration synchronously before entering its observer message pump; a slow provider could therefore leave every native signal inert. The two WinEvent ranges identify real provider-reported drags, MSAA gives older Shell views an independent item classifier, and none requires a second path-bearing point inspection. Separately, adding the Drop Tray offset only to DragApproaching/DragReady made the post-Drop Compact/Expanded/Notch target jump to a different coordinate and could place the visible surface outside its HWND region.

**Constraints:** The WinEvent callback is observation-only, performs no COM or path work, never suppresses input, and always leaves final validation to the temporary OLE target. System drag events are supplemental because Windows does not synthesize them for providers that omit them. Source HWNDs must still resolve to the Explorer/Desktop Shell boundary; taskbar Shell surfaces are explicitly excluded. Provider coverage remains best effort and real Windows 11 evidence is required; Classic mode remains the explicit compatibility fallback.

## D-040 — Use one Dynamic Island visual surface

**Decision:** Remove the Notch display mode, its Settings selector, display-mode state transitions, asymmetric top-edge HRGN path, and mode-specific geometry tests. Compact, Drop Ready, Expanded, Dismissing, full-screen restoration, Smart placement, and direct OLE drop continue on one rounded Dynamic Island surface. Settings schema 6 no longer serializes an Overlay display mode; older JSON may contain the removed property and is migrated by safely ignoring it while preserving all other preferences.

**Rationale:** Maintaining two shapes added a second geometry/lifecycle branch without changing Temporary Space semantics, and field use showed that the attached-edge variant was disproportionately fragile. A single surface reduces native-region, animation, recovery, and product-explanation complexity while retaining the interaction users actually use.

**Constraints:** This decision does not alter Smart drag detection, Classic compatibility mode, file acceptance, source-file safety, display selection, motion preference, or the truly Hidden lifecycle. Historical release notes and prior decision rationale remain factual records of older releases.

## D-041 — Fail closed when producing official website release metadata

**Decision:** GitHub Releases is the only production authority for website release metadata. A GitHub Pages production job must fetch the first 20 releases, validate schema-v1 official release and same-tag asset URLs, require a Stable release plus the standard assets on the latest Stable and Preview, and only then write the build input. Any network, HTTP, JSON, contract, or required-asset failure terminates the job before the Pages artifact is built or deployed. The committed release JSON is an explicit local/PR fixture only and is forbidden in production mode. The obsolete Cloudflare Pages Function is removed.

**Rationale:** Rebuilding from a stale committed snapshot after an upstream failure can redeploy older metadata and make the official API appear to move backwards. Keeping the last successfully deployed Pages artifact is safer and more truthful than publishing a new artifact whose release data was not freshly verified.

**Constraints:** The public schema remains version 1; the App continues to prefer the official GitHub Pages API and fall back to GitHub Releases REST. Release and asset URL allow-lists, update-manifest verification, filenames, channels, and historical GitHub Releases remain unchanged.

## D-042 — English base resources with a bounded System language mapping

**Status:** Accepted

**Decision:** Keep one complete `en-US` resource file as the default display-language source, ship an exactly keyed `zh-CN` companion, and prohibit CJK UI hardcoding in production `.cs` and `.xaml` files. Persist `System`, `English`, and `SimplifiedChinese` choices in settings schema 7. On startup, System maps a Windows `zh-*` display-language preference to `zh-CN`; all other system languages use `en-US` until another resource set is deliberately shipped. Dependency-object XAML uses an app-owned override that reapplies supported properties and accessibility names instead of WinUI's implicit `x:Uid` resource lookup; `Window` roots use that override directly after XAML initialization because they are not dependency objects. Imperative user-facing strings—including tray, Dynamic Island, update status, and errors—resolve from the same resource collection. The unpackaged portable build regenerates one packaging-free `DropSpace.resources.pri`, bundles it with the self-extracting executable, and opens it only through an explicit `ResourceManager` context so it does not replace WinUI's default `resources.pri`; it does not use `ApplicationLanguages.PrimaryLanguageOverride`, which Windows App SDK does not support for unpackaged processes.

**Rationale:** English must be a predictable complete base and fallback resource set while Chinese Windows users retain a full native-language surface through the System choice. A narrow, explicit mapping avoids pretending that untranslated Windows languages are fully localized, and an app-restart boundary avoids mixed resource contexts in existing XAML, native tray, and service objects.

**Constraints:** CI validates resource-key parity, blocks new CJK hardcoding, and executes the Windows workload under both resource contexts. It does not claim the hosted runner's operating-system display language has been switched; complete validation still requires separate English and Simplified Chinese Windows 11 manual evidence.

## D-043 — Verify source-agnostic Smart candidates with one ephemeral hollow OLE target

**Status:** Accepted for the v0.3 Preview sequence

**Decision:** Smart mode keeps observing every left/right press until release or a Windows drag-threshold crossing. Explorer/Desktop exact-item evidence remains a high-confidence fast path, and a documented accessibility drag-start may establish strong evidence even when the provider/source kind is unknown. A threshold-only unknown or non-exact Shell press creates a generic session, begins speculative Dynamic Island reveal, and creates at most one local 144-pixel hollow `IDropTarget` centered on the current physical screen point. Its real 12-pixel Region hole initially contains the cursor; the surrounding ring is layered, topmost, tool-window and no-activate, exists for at most 60 ms, returns `DROPEFFECT_NONE`, and posts cleanup outside the COM callback. It never exists while idle and Smart never creates or falls back to a permanent edge/full-screen target.

One `OleFileDataClassifier` queries the `IDataObject` used by the probe, Classic host, and visible Island. `CF_HDROP` and bounded Shell IDLists that resolve to file-system paths can enter the existing reference pipeline. `FileGroupDescriptorW` plus `FileContents` proves virtual-file evidence during query-only verification, but Preview.1 does not accept the final Drop until bounded, cancellable staging materialization is implemented. Session IDs gate verification, timeout, cancellation and OLE completion so old work cannot change a newer reveal. Diagnostics may contain evidence flags, classification kind, counters and elapsed time, but no path, filename or payload.

**Rationale:** The v0.2 source allow-list safely avoided false reveals but stopped observation for unknown origins and rejected strong provider evidence, excluding many standard OLE sources. Real OLE format negotiation is a provider-agnostic authority. A tiny bounded ring can observe that authority only during an active candidate without reintroducing an idle hit surface or delaying Reveal.

**Constraints:** No provider process-name rules, polling, input suppression, cross-process injection, administrator requirement, foreground activation, taskbar/Alt+Tab entry, content read during verification, or unbounded PIDL/item parsing is permitted. Real source compatibility, pointer feedback, mixed-DPI/display races and false-reveal rate remain manual Preview gates. Classic stays an explicit user-selected compatibility mode.

## D-044 — Consolidate Preview.2 around independent proof, universal intake, and one Island

**Status:** Accepted for v0.3.0-preview.2

**Decision:** `DragIntentConfidence` and `PayloadConfidence` are separate axes. Accessibility drag-start proves intent only; an unknown payload remains speculative until OLE proves file-like data. High-frequency pointer moves use a one-slot coalescing lane, while press, release, cancel, completion, timeout, and probe results use a reliable lane. Pointer release enters `AwaitingOleCompletion`; a new pointer-down supersedes that session immediately within the 350 ms grace window. All visual and probe work remains session-gated.

Shell IDList evidence is divided into `IsFileLikeEvidence`, `CanAcceptNow`, and `CanMaterialize`; only Shell items resolved to paths are immediately acceptable. The official `SHCreateShellItemArrayFromDataObject` API is preferred, with bounded CIDA parsing as fallback. A real Drop may materialize `FileGroupDescriptorW` plus indexed `FileContents` into one confined staging batch using bounded streaming, cancellation, duplicate-safe names, rollback, and `IDataObjectAsyncCapability` when supplied. Probe cleanup is three-layered: owner `PostMessage`, owner work queue, then forced watchdog.

The existing Dynamic Island is also the Quick Panel, with a configurable default `Win+Shift+Space` hotkey and existing keyboard actions. Files, payload-backed images, text, and URLs may leave Space; manual intake and packaged Windows Share may add those types without writing Clipboard History. A single acquisition receives one `DropBatchId` and optional `DropSessionId`, enabling group expansion, drag, pin, and removal without duplicate records. Smart Drag process exclusions are explicitly best effort. Settings schema 8 stores the original per-monitor Custom X/Y DIP values; topology/DPI clamping is transient and every visual state uses one resolved anchor.

**Rationale:** Intent signals, payload proof, content custody, and presentation placement have different trust and lifetime boundaries. Keeping them independent prevents false file acceptance, stale-session hides, whole-file buffering, and saved-coordinate corruption while allowing more providers and access paths.

**Constraints:** No payload is read by the verification probe; materialization occurs only after Drop and only below DropSpace staging. No source path, filename, text, or payload appears in diagnostics. Portable builds disclose that Windows Share Target requires package identity. Real WeChat/QQ/Feishu/Outlook/Electron provider evidence remains a manual Windows gate and must never be inferred from hosted CI.

## D-045 — Make Preview.3 signal and monitor placement boundaries explicit

**Status:** Accepted for v0.3.0-preview.3

**Decision:** Keep `PointerMoved` in a one-slot `DropOldest` lane, but move press/release/cancel/completion/probe/timeout lifecycle signals to an unbounded, single-reader reliable lane. Hook callbacks never wait; a critical write failure is recorded only when the lane is already completing or closed. The merge reader compares `Stopwatch` timestamps across both lanes so a pre-release movement cannot be overtaken by a later release.

Replace HMONITOR string persistence keys with a `DisplayIdentityService` result derived from the normalized DisplayConfig target device path. `MonitorDescriptor.Id` is the hashed persistent identity and `Handle` is runtime-only; a `runtime:` fallback is explicitly non-persistent when resolution fails. Schema 9 stores `OverlayPlacements` per monitor. Missing entries are Automatic, Reset removes only the selected entry, and clamp is a runtime projection.

The Settings placement editor is a transient no-activate state machine. It captures only the fixed island host, translates pointer deltas from physical pixels to monitor-local DIP, suppresses Smart candidate creation and Classic activation interference, commits one final Custom coordinate on release, and restores the entry snapshot on Escape. It reuses the normal settings transaction and preserves the precise X/Y editor.

**Rationale:** The Preview.2 critical channel configured `Wait` but used `TryWrite`, so it could report a false reliable design while silently rejecting events at capacity. HMONITOR handles are valid runtime selectors but not durable display identities. A per-monitor mode eliminates the global Custom fallback that moved unconfigured displays, while a transient edit avoids settings writes on every pointer move and keeps file-drag semantics isolated.

**Constraints:** DisplayConfig mapping is best effort and must fail to an explicit runtime fallback without blocking startup. The new editor does not create a full-screen surface, does not change the Smart classifier or payload boundary, and does not claim that hosted CI replaces real Windows 11 pointer/DPI/provider evidence.

## D-045 — v0.3 3.0 features remain explicit, bounded, and Windows-only

- Status: Accepted for v0.3.0-preview.6
- Decision: Add Quick Preview, Quick Actions, DropLink v1, opt-in cross-device clipboard, Nearby Share, and client-encrypted Internet Share as separate contracts. Keep native macOS/iOS/iPadOS/Android/Linux clients, accounts, WebRTC, and automatic public sharing out of scope.
- Rationale: The feature plan requires real handoff and share behavior, but the existing local-first/privacy boundary must remain legible. Provider/action registries isolate malformed input and future extension; pairing, certificate pinning, SAS, HMAC, staging, and explicit approval bound the network risk.

## D-046 — Internet Share is fail-closed until an operator deploys the Worker

- Status: Accepted for v0.3.0-preview.6
- Decision: Ship `share-worker/` as the Cloudflare Worker/R2 reference implementation, but require an HTTPS `DROPSPACE_SHARE_BACKEND_URL` and the `EnableInternetSharing` setting before any upload. Never use a fake endpoint or claim deployment from source presence.
- Rationale: The client can prove AES-256-GCM/HKDF and browser WebCrypto interoperability locally, while production availability depends on account secrets, R2 lifecycle, domain, rate limits, and logging policy that do not belong in the repository.

## D-047 — Transfer schema v2 stores peer/session metadata, not payloads

- Status: Accepted for v0.3.0-preview.6
- Decision: Migrate schema 1→2 with `paired_devices` and `transfer_sessions`; store DPAPI-protected peer secrets and staged bytes in application data; receive only under Downloads/DropSpace after manifest/hash validation.
- Rationale: SQLite remains queryable and recoverable without becoming a clipboard/file vault, and forward-only migration preserves the existing backup/recovery contract.

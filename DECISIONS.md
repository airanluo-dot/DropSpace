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

## D-003 — Quick Panel moves to V1.1

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
- Decision: Read `StandardDataFormats.StorageItems` before bitmap/text, inspect each external file/folder through `IFileReferenceService`, and store it in the existing item/reference schema with `Source=Clipboard`. Scope path deduplication by source, apply recent fingerprint deduplication to Clipboard, never enumerate captured folders, and expose validated user limits for item count and known bytes.
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

## D-009 — App exclusions are best effort and V1.1

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

**Decision:** Preserve the native top-edge host when available, add honest Settings guidance, and implement the public Windows Share Target contract behind a trusted external-location identity. Never poll, hook, probe undocumented flags, import certificates or fight Shell z-order. A Share Target is guaranteed in the Share UI; direct placement in Drop Tray suggestions is not guaranteed.

**Rationale:** Drop Tray and DropSpace intentionally occupy the same user gesture. Windows Shell can acquire OLE ownership first, and no application-local HWND style can reliably override that without harming the desktop. Package identity/signature is the supported integration boundary.

## D-033 — Visible input follows visible pixels; Temporary Space projections are serialized

**Decision:** Stable Compact/Expanded disables the passive host, applies WinUI `AllowDrop` to the visible surface and retains a native root `CF_HDROP` adapter. Repository mutation publishes one monotonically increasing Space revision. Overlay refresh is serialized/coalesced and synchronizes cards by identity on the UI Dispatcher.

**Rationale:** Preview.4 registered the root HWND but visible WinUI pixels could resolve to a child HWND, while the passive topmost host could compete. The deletion crash path separately allowed one removal to start two repository refreshes and overlapping `ObservableCollection.Clear/Add` during Dismissing. Exclusive target ownership and a single projection coordinator remove both races without duplicating storage logic.

## D-034 — License DropSpace's original work under Apache-2.0

**Decision:** DropSpace's original source, documentation, configuration, tests, scripts, and repository-owned brand image files are licensed under Apache License 2.0, with `Copyright 2026 Airan Luo`. Contributions use Apache-2.0 inbound and outbound terms without a CLA. Third-party dependencies retain their own licenses, and trademark/source-identification policy remains separate from copyright licensing.

**Rationale:** Apache-2.0 permits source access, forks, modification, redistribution, and commercial use while providing explicit patent terms and preserving NOTICE and attribution requirements. A project-level SPDX policy avoids mechanical headers on generated, binary, manifest, and third-party files.

**Audit boundary:** Git history contains only the project owner's `Luo Airan` and `Aren Vox` identities plus GitHub merge automation. No vendored third-party source or visual asset was found. WinIsland remains a GPL-3.0 behavioral reference only under D-022 and is not incorporated. Runtime/build dependencies remain governed by [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## D-035 — Narrow, opt-out GitHub Release update boundary

**Decision:** D-012/D-016 continue to prohibit content/network enrichment, telemetry, accounts and sync, but v0.1.0 adds one explicit update-only boundary. When enabled, one process-start check and user-invoked checks read at most 20 public releases from the official GitHub REST API. No Clipboard content, Temporary Space path, filename, query, username, machine ID or install identifier is sent. Stable accepts only Stable versions; Preview accepts all releases; both choose the highest SemVer strictly above the running version.

**Security boundary:** Remote metadata cannot provide arbitrary execution URLs. A bounded, exact-schema `update-manifest.json` names only fixed official assets. Downloads remain inside `%LOCALAPPDATA%\DropSpace\Updates`, stream to `.download`, require exact size and SHA-256, and are atomically promoted. Integrity is not publisher authenticity; unattended installation additionally requires WinVerifyTrust and the compiled DropSpace signer identity. Unsigned builds require explicit user installation.

**Rationale:** A first Stable desktop application needs an understandable upgrade path without a service, timer, scheduled task, tracking identity, or silent trust downgrade. The abstraction keeps Portable and Windows-managed Package deployments from invoking Inno Setup.

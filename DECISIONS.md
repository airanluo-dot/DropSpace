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

## D-019 — Portable EXE is the preferred Preview deployment

- Status: Accepted and implemented
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

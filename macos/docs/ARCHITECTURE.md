# Native macOS architecture

Target: macOS 26+, arm64. Product identity: `io.github.airanluo-dot.DropSpace.macOS`.
Version: v0.1.0 Beta 1, Windows v0.3.2 Beta 7 feature baseline.
Immutable reference: `1cd4620dd58a67b0963b73487743137813b8864a`.

The Xcode application owns SwiftUI scenes and narrowly scoped AppKit bridges. The local Swift package separates `DropSpaceCore` (values and rules), `DropSpaceInfrastructure` (SQLite, atomic settings, owned payloads and processes), and `DropSpacePlatform` (pasteboard, file references, system integrations). Presentation state belongs to MainActor; database and file services are actors. A view never opens SQLite or starts a helper process.

Data is separate under Application Support/DropSpace/macOS. Ordinary files remain references with bookmarks and volume identity. Only owned payloads enter the cleanup outbox. A single durable undo slot hides removals for eight seconds; finalization is transactional. Settings are validated before atomic persistence, and applied system changes must roll back on save failure. Windows data is neither read nor migrated automatically.

Native UI uses SwiftUI and narrowly scoped AppKit, Quick Look, NSPanel and NSOpenPanel. User clarification requires frozen Windows layout, control hierarchy, state and interaction timing; native materials/platform mechanisms do not authorize redesign. Existing generic main-window and settings presentation remains subject to that acceptance. See ISLAND-EXPERIENCE.md. Ten pinned Windows resource catalogs retain their identifiers; imported Windows wording still requires platform review. No web view is part of the application.

The settings editor is a separate SwiftUI Window scene reached through the native Settings menu; this permits the two configured Settings shortcuts without an additional generated default shortcut. The application delegate owns the single-instance gate before any window task. Quitting retires sheets, schedules final termination on the AppKit run loop (outside a Swift task/main-dispatch block), and asynchronously drains services before replying to `terminateLater`.

Workspace lifecycle attachment covers both windows already created by SwiftUI at
`applicationDidFinishLaunching` and later main-window notifications. The first
background close uses a native explanation sheet, merges only its acknowledgement
into the latest settings and hides only after successful persistence. Quit retires
that sheet without acknowledging it. Music reconnect has a separate app-owned
20-second attempt/retirement owner; see MUSIC-RUNTIME.md.

Native differences: Command replaces Windows Ctrl navigation; ServiceManagement replaces startup registration; Keychain replaces DPAPI; Metal/CPU replace Vulkan/CUDA. Apple Music and Spotify use public scripting interfaces with Automation permission. Unknown media capability must remain unavailable; published app playback metadata is never treated as a global media API. Public notification APIs cover DropSpace and connected modules only. Smart drag must require fresh drag pasteboard evidence during an active pointer gesture. Promised files are materialized only after a real drop.

QQ lyric credentials have an independent device-local Keychain namespace and serialized session owner. The six-field/domain allowlist, authenticated request generation, shared network budget and redirect refusal are implemented. Browser handoff and account UI are user-deferred as of2026-10-20; unsigned Safari installation is not an ad-hoc main-app capability. No Firefox replacement has been approved. See QQ-SESSION.md.

External contracts remain separately versioned: DropLink v1 wire contract, module v1 declarative messages plus explicit macOS/arm64 metadata, and macOS-only update manifests. Windows protocol compatibility changes belong exclusively to the attached compatibility worktree. Public release uses local ad-hoc signatures for bundle integrity, explicitly without Developer ID or Apple notarization, as authorized on 2026-10-20. Actual Mac↔Windows and installation/update tests remain required; local build success is not release qualification.

See PARITY.md and EXECUTION.md for implementation and evidence. File inventory is coverage input, not proof of review or parity.

# Windows integration boundaries

Read this file only for Windows shell, Dynamic Island, native windowing, drag/OLE, focus/DPI, media, notification, volume, hotkey, or startup work.

Use the current source and `WINDOWS_INTEGRATION.md` as the implementation truth. This reference records durable boundaries, not a frozen implementation snapshot.

## Window and Island behavior

- DropSpace is Dynamic-Island-only. Do not restore the removed Notch mode unless the user explicitly asks for a product reversal.
- Keep one authoritative Island/overlay surface. Optional activities such as media, widgets, notifications, or volume must route through the existing Island rather than creating competing always-on-top UI.
- `--startup` must establish the main HWND/services without an initial `Show` or `Activate`. Normal or redirected user activation may show it.
- Preserve no-focus behavior where intended: avoid unnecessary activation, taskbar presence, Alt+Tab presence, or topmost interaction that steals focus from the user's current app.
- Preserve multi-monitor, mixed-DPI, text scaling, high contrast, reduced motion, and capability fallbacks relevant to the touched path.
- Runtime clamping/placement repair must not silently destroy a valid saved user preference merely because a monitor is temporarily absent.

## Smart Drag and OLE

Smart Drag is a regression-sensitive input boundary. Keep it bounded, event-driven, and fail-closed.

- Smart mode must not own a permanent full-screen or top-edge drop target. A compatibility/classic host may exist only as an explicit user-selected fallback when the current product still supports it.
- A held mouse button alone is not verified file-drag intent. Preserve the distinction between pointer/gesture evidence and authoritative OLE/file payload evidence.
- Exact Explorer/Desktop/accessibility evidence may accelerate detection, but generic candidates must be verified before revealing/accepting as a file drag.
- OLE verification must remain ephemeral and non-activating. It must not become a persistent interception surface, input suppressor, injection mechanism, or elevation requirement.
- Keep acceptance/materialization semantics separate. Do not authorize a drop solely because a payload looks vaguely file-like.
- Read virtual-file content only after a real Drop when materialization is required. Keep staging confined, bounded, cancellable, duplicate-safe, and rollback-safe.
- Session/generation identity must gate stale callbacks across probe timeout, pointer release, OLE completion, mode/display changes, and shutdown.
- Cleanup must converge even when a normal callback/post path fails. Revoke OLE registration, destroy owned native resources, cancel timers/tasks, and release registry ownership on the correct owning thread.
- Never log dragged filenames, full paths, or payload contents as routine diagnostics.

Before changing the detailed classifier/probe implementation, inspect the current detector, probe, state machine, tests, and the relevant Windows integration documentation instead of relying on old Preview-specific numbers or class names.

## Shell intake, hotkeys, and app lifetime

- Shell/SendTo/context-menu intake must reuse the existing app-instance/lifetime model and must not activate the main window unless the user-visible flow requires it.
- Shell intake must not copy, move, or delete the external source merely to register it in DropSpace.
- Global hotkey and native-service start/stop paths should be asynchronous and bounded. Do not block the UI thread waiting on native worker shutdown.
- Failed stop/restart must not create two concurrent owners of the same global/native resource.

## Native Island activities

System activities are optional integrations, not startup dependencies.

- Media/session integration must fail closed when the OS API/session is unavailable. It must not block core startup, Smart Drag, or normal file use.
- Audio/spectrum work should run only while useful, release native/COM resources when idle/disabled, and avoid permanent background sampling.
- Volume integration observes/controls the intended system endpoint through supported APIs; do not replace the Windows volume flyout or install broad keyboard hooks to impersonate system UI.
- Notification access is opt-in and must degrade cleanly when permission/API access is unavailable.
- Widgets and media views remain projections inside the existing Island/navigation model; they must not create a second competing overlay.
- Keep media/artwork/lyrics/network lookups bounded and cancellable. They must not block startup or settings commits.

## Current-state discovery

Do not encode the current Preview number, minimum Windows build, exact settings schema, or class inventory here. Resolve those from the current repository:

- minimum OS/build policy: shared build/compatibility sources such as `Directory.Build.props` and Core compatibility code
- settings schema: current settings model/migrations/tests
- current Island/activity architecture: current App/Core/Infrastructure source and tests
- current release state: `RELEASE_VERSION` and GitHub Releases

## Validation

Windows-host evidence is required for claims that depend on real OS integration, including as applicable:

- Explorer/Desktop/third-party OLE behavior
- no-focus/taskbar/Alt+Tab behavior
- mixed-DPI and multi-monitor geometry
- startup and shell registration
- media-session, notification-permission, audio/spectrum, and system-volume behavior
- accessibility/high-contrast/reduced-motion behavior

Hosted CI and source inspection can prove structure and automated contracts; they do not substitute for unrun physical Windows acceptance rows.

# Windows Integration Feasibility

Status labels: **Supported**, **Supported with Win32 interop**, **Complex/validate**, **Limited**, **Deferred**.

## Platform baseline

WinUI 3 is the native UI layer shipped with the Windows App SDK. DropSpace supports the recommended per-user Inno Setup installer, the same unpackaged self-contained single-file x64 EXE as a portable option, and an MSIX package as an alternative. No deployment path stores runtime data beside the executable.

## Capability matrix

| Capability | Assessment | Plan |
|---|---|---|
| WinUI 3 shell, controls, theme | Supported | Native XAML/Fluent resources |
| Mica primary window | Supported | `Window.SystemBackdrop`; solid fallback |
| Acrylic transient surfaces | Supported | Use sparingly on flyouts/menus |
| Clipboard change event | Supported with Win32 interop | `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` on a stable message-pump HWND |
| Text/image clipboard reads | Supported, format-dependent | Snapshot `DataPackageView`, handle transient failures |
| Clipboard source app | Limited/best effort | Win32 clipboard-owner window may be absent/stale/indirect |
| Drag files into app | Supported | XAML drag/drop with `StorageItems` |
| Drag files out to Explorer/apps | Supported, compatibility test required | Standard data package/storage items; test targets |
| Global hotkey | Supported with Win32 interop | `RegisterHotKey`, conflict handling; V1.1 |
| Tray icon | Supported with Win32 interop | `Shell_NotifyIcon`, native menu and restart recovery |
| Hide-to-tray background operation | Supported | Keep desktop process alive; not an OS background task |
| Startup at sign-in | Supported/packaging-dependent | Activation/startup registration; V1.1 preference |
| Single instance | Supported | Windows App SDK AppInstance redirection |
| Hidden top-edge file-drag reveal | Supported with Win32/OLE interop | Independent visually transparent activation HWNDs plus `IDropTarget`/`RegisterDragDrop`/`CF_HDROP` |
| Dynamic Island / Notch | Supported with WinUI Composition and shaped HWND | Shared state/data; visual geometry only differs |
| Per-monitor DPI placement | Supported with Win32 interop | Physical monitor bounds + effective DPI; DIP-to-pixel conversion at window boundary |
| Portable single-file EXE | Supported on Windows App SDK 1.5+ | Unpackaged, Windows App SDK self-contained, .NET self-contained, content extraction enabled |
| SQLite | Supported via library | `Microsoft.Data.Sqlite`, local database |
| File picker | Supported | WinRT picker initialized with HWND where required |
| System file thumbnails | Supported, async | Storage/Shell thumbnail APIs; bounded cache |
| Track arbitrary file moves | Not reliably supported | Explicit Missing and Locate/Replace flow |
| Explorer context menu | Complex | Not MVP/V1.1 unless strong evidence |

## Clipboard monitoring

The unpackaged desktop build registers its stable main-window HWND with `AddClipboardFormatListener` and receives `WM_CLIPBOARDUPDATE` through a narrow window-subclass adapter. The main HWND remains alive while hidden to the tray. The native handler emits only sequence/time metadata; the existing bounded async capture pipeline reads the current value through WinRT `DataPackageView` on the UI thread. Clipboard content can be delayed-rendered, locked, replaced again before async reads complete, or offer several formats.

Implementation requirements:

- Prefer supported standard formats in policy order.
- Bound retries and payload size before decoding where possible; retry only a finite number of times and abandon the old generation if the sequence advances.
- Use `GetClipboardSequenceNumber` to coalesce duplicate notifications and fingerprint information to suppress self-copy loops.
- Treat event occurrence as a hint, not proof that content can be persisted.
- Capture only while the DropSpace process is running.
- Surface listener registration, last notification, observed updates, successful captures, failed reads, dropped signals, and pause state in diagnostics.

Official references: [AddClipboardFormatListener](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener), [WM_CLIPBOARDUPDATE](https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-clipboardupdate), [GetClipboardSequenceNumber](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber), and [Copy and paste in WinUI apps](https://learn.microsoft.com/en-us/windows/apps/develop/communication/copy-and-paste).

## Source-app attribution and exclusions

Win32 `GetClipboardOwner` can return the window that owns the clipboard, but ownership may be null, may change, and may belong to an intermediary/broker rather than the app the user thinks of as the source. Mapping HWND → process → packaged app identity is also not universally stable.

Therefore app exclusions are **best effort**, deferred to V1.1, and never marketed as a secure password-manager boundary. The UI must say that attribution can fail. Pause recording and clear history remain the reliable controls.

Official reference: [GetClipboardOwner](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardowner).

## File drag and drop

### Hidden Overlay activation

DropSpace separates two lifecycles per enabled display:

- The visual WinUI Overlay HWND owns only the Island/Notch. `Hidden` clears its HRGN and calls `SW_HIDE`; no XAML surface, backdrop, frame, shadow, or border remains visible.
- A dedicated native activation HWND uses uniform alpha 1/255 and registers a managed `IDropTarget` with `RegisterDragDrop`. Alpha zero is skipped by Windows point/OLE discovery; 1/255 is visually imperceptible and keeps the surface discoverable. Idle it is 960 DIP wide but exactly one physical pixel high at the monitor top edge. It uses `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED` and `WM_NCHITTEST=HTCLIENT`: Microsoft documents `HTTRANSPARENT` as forwarding only within the same thread, so it cannot be used for reliable Explorer target discovery. A valid `DragEnter` expands the same HWND to 760 × 112 DIP and preserves ownership through Drop/Leave.

The OLE target checks `CF_HDROP`, advertises `DROPEFFECT_COPY`, extracts paths only on Drop, and reports only monitor/DPI/bounds/format/item-count diagnostics. Hidden-edge drags remain owned by the activation HWND for their whole lifetime; Reveal never hands them to the visual window. When Compact/Expanded is already visible, its shaped HWND independently accepts direct Drop outside the one-pixel idle edge. There are no overlapping active targets, global hooks, mouse-button scans, or cursor polling.

### Fullscreen classification

Monitor bounds alone do not imply a full-screen application: the Windows desktop itself covers the monitor. The foreground classifier requires a visible, uncloaked, non-iconic, top-level non-tool window on the target monitor and excludes desktop/shell handles plus `Progman`, `WorkerW`, `Shell_TrayWnd`, and `Shell_SecondaryTrayWnd`. Fullscreen suppression morphs to Hidden and restores from the retained spring state; it does not snap. Drag states override passive suppression.

When a shell drag enters, the known host identifies the monitor, the Core state machine changes targets, and the separate visual surface reveals. Non-active monitors retain only their enabled hosts. Monitor coordinates are physical pixels; host and UI dimensions are DIPs scaled with each monitor's effective DPI. `WM_DISPLAYCHANGE` rebuilds monitor-bound hosts and visual windows.

Official references: [RegisterDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop), [IDropTarget](https://learn.microsoft.com/en-us/windows/win32/api/oleidl/nn-oleidl-idroptarget), and [DragQueryFile](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-dragqueryfilew).

### Drag in

Set `AllowDrop`, inspect advertised formats during `DragOver`, accept copy/link semantics, and retrieve `StandardDataFormats.StorageItems` on Drop. Mixed batches and unsupported virtual files require partial-result behavior.

### External drag out

Set `CanDrag`/handle `DragStarting`, populate a standard data package with storage items, and let the Windows drag loop negotiate with the target. This is real cross-application transfer, not an in-app animation.

Compatibility matrix must include Explorer/Desktop, common browsers, Office, Photoshop or an available image editor, VS Code, network targets, and elevated/non-elevated boundaries. Some targets may reject folders, virtual items, or cross-integrity interactions.

Official reference: [Windows drag and drop](https://learn.microsoft.com/en-us/windows/apps/design/input/drag-and-drop).

## Global hotkeys

`RegisterHotKey` registers system-wide combinations and reports failure when unavailable. A WinUI window/message hook receives `WM_HOTKEY`; registration must be removed on exit. Reserved combinations and collisions are expected.

- Do not default to `Alt+Space` because Windows window menus and PowerToys Run make it conflict-prone.
- Proposed V1.1 default: `Win+Shift+V`, configurable.
- Show conflict state and retain a Settings button to test alternatives.
- Elevated foreground apps may create focus/interaction limitations.

Official reference: [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).

## System tray

WinUI 3 does not remove the need for Win32 notification-area integration. Use `Shell_NotifyIcon` through a narrow adapter. Re-add after the taskbar/Explorer restart message, support keyboard activation, and destroy icon/menu resources on exit.

Official reference: [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw).

## Background operation and lifecycle

“Background” means the desktop process remains alive with no main window and a tray icon. It is not a suspended UWP-style background task or Windows service. Choosing Exit stops capture and closes every Overlay window.

The main window, tray, clipboard service, and Overlay share the same process and single-instance key. Overlay windows are always-on-top only while their surfaces are enabled, never request elevation, and are no-activate except while the user explicitly expands one for controls.

Use Windows App SDK application lifecycle APIs for activation and single-instance redirection. A second launch redirects to the existing process and activates the window. Shutdown and logoff allow only bounded flushing.

Official references: [App lifecycle activation](https://learn.microsoft.com/en-us/windows/apps/develop/launch/activate-an-app), [App instancing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing).

## Startup

Packaged startup activation is feasible but user control, OS policy, disabled startup entries, and activation timing must be handled. Implement after tray/background behavior is stable. Never bypass the user's Windows startup-app preference.

## SQLite

SQLite is preferable to JSON because DropSpace needs indexed multi-field search, retention queries, atomic multi-record updates, migrations, and concurrent reads. Use `Microsoft.Data.Sqlite` and parameterized SQL. EF Core is unnecessary for this small schema and adds mapping/startup complexity.

Official reference: [Use SQLite in a Windows app](https://learn.microsoft.com/en-us/windows/apps/develop/data-access/sqlite-data-access).

## Thumbnails

- File/folder: request system thumbnails/icons asynchronously; results can be absent.
- Clipboard images: encode an app-owned original within limits, then create bounded display thumbnails.
- Decode to target size to avoid full-bitmap memory spikes.
- Include DPI/size in cache keys and regenerate on corruption.
- Never block the UI on shell thumbnail providers, especially network/NAS locations.

## File references

Store paths and observed metadata, not `StorageFile` objects across sessions. Files can move, disappear, become inaccessible, or be cloud placeholders. Existence is checked lazily; failed operations update status. Automatic tracking across arbitrary moves is not promised.

## File picker and HWND interop

Desktop WinUI pickers and some shell UI require initialization with the owning HWND. Centralize this in the Windows adapter layer and verify cancellation, multi-window ownership, and DPI behavior.

## Mica and Acrylic

Use Mica for the primary window and Acrylic mainly for transient surfaces; support system fallback and high contrast. Official reference: [System backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops).

## Notifications

MVP prefers inline/tray feedback. App notifications are reserved for failures requiring action while the window is hidden and must not contain clipboard payloads. Packaging identity and activation handling are prerequisites.

## Explorer integration

Shell context-menu extensions increase packaging, registration, performance, and reliability burden inside Explorer. They are not required to prove the product and are explicitly deferred.

## Biggest technical risks

1. Cross-application drag-out behavior across target apps and integrity levels.
2. Clipboard burst/delayed-rendering behavior without loops or UI-thread stalls.
3. Tray/window lifecycle and clean single-instance activation.
4. Source-app attribution giving users a false privacy guarantee.
5. Image memory/disk growth and thumbnail providers on remote locations.
6. Real Explorer/Desktop drag semantics and mixed-DPI geometry across GPU/display-driver combinations.

Each risk has a dedicated vertical-spike phase before the affected feature is declared committed.

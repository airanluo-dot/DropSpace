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
| Text/image/file clipboard reads | Supported, format-dependent | Snapshot `DataPackageView`, prefer `StorageItems`, handle transient failures |
| Clipboard source app | Limited/best effort | Win32 clipboard-owner window may be absent/stale/indirect |
| Drag files into app | Supported | XAML drag/drop with `StorageItems` |
| Drag files out to Explorer/apps | Supported, compatibility test required | Standard data package/storage items; test targets |
| Global hotkey | Supported with Win32 interop | `RegisterHotKey`, conflict handling; V1.1 |
| Tray icon | Supported with Win32 interop | `Shell_NotifyIcon`, native menu and restart recovery |
| Hide-to-tray background operation | Supported | Keep desktop process alive; not an OS background task |
| Startup at sign-in | Supported | Per-user `HKCU` Run value, default on, Settings-controlled, `--startup` hidden launch |
| Single instance | Supported | Windows App SDK AppInstance redirection |
| Hidden file-drag reveal | Experimental Smart mode plus Classic fallback | UIA drag signals + bounded input observer reveal a temporary visible `IDropTarget`; legacy transparent top-edge target is opt-in |
| Dynamic Island / Notch | Supported with WinUI Composition and shaped HWND | Shared state/data; visual geometry only differs |
| Per-monitor DPI placement | Supported with Win32 interop | Physical monitor bounds + effective DPI; DIP-to-pixel conversion at window boundary |
| Portable single-file EXE | Supported on Windows App SDK 1.5+ | Unpackaged, Windows App SDK self-contained, .NET self-contained, content extraction enabled |
| SQLite | Supported via library | `Microsoft.Data.Sqlite`, local database |
| File picker | Supported | WinRT picker initialized with HWND where required |
| System file thumbnails | Supported, async | Storage/Shell thumbnail APIs; bounded cache |
| Track arbitrary file moves | Not reliably supported | Explicit Missing and Locate/Replace flow |
| Explorer context menu | Complex | Not MVP/V1.1 unless strong evidence |

## Clipboard monitoring

The unpackaged desktop build registers its stable main-window HWND with `AddClipboardFormatListener` and receives `WM_CLIPBOARDUPDATE` through a narrow window-subclass adapter. The main HWND remains alive while hidden to the tray. The native handler emits only sequence/time metadata; the bounded async capture pipeline reads the current value through WinRT `DataPackageView` on the UI thread. It prefers `StandardDataFormats.StorageItems` for Explorer file/folder copies, then bitmap, then text. Clipboard content can be delayed-rendered, locked, replaced again before async reads complete, or offer several formats.

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

### Smart Overlay activation (v0.2 Preview)

DropSpace separates two lifecycles per enabled display:

- Smart mode is the default Preview setting. While idle, every visual Overlay is `SW_HIDE`, has an empty HRGN and has revoked `RegisterDragDrop`; no DropSpace HWND owns the monitor's top-center hit point. Display-topology broadcasts use a never-shown, zero-sized ordinary message window, not a topmost edge surface.
- A dedicated observer thread uses `SetWinEventHook(WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)` for the documented `EVENT_OBJECT_DRAGSTART`, `EVENT_OBJECT_DRAGCANCEL`, and `EVENT_OBJECT_DRAGCOMPLETE` range. It also listens for `UIA_Drag_DragStart/Cancel/Complete` when providers expose them. Both callbacks only enqueue bounded source-window/point signals; they never inspect content, block, suppress, or rewrite input.
- `WH_MOUSE_LL`/`WH_KEYBOARD_LL` remain observation-only supporting signals. Mouse-down first identifies whether the origin is an Explorer/Desktop file-view surface and separately tries to identify the exact UI Automation item. Movement-only fallback requires the exact item plus `SM_CXDRAG`/`SM_CYDRAG`; a documented object-drag event may promote the already verified Shell surface even if the item leaf became unavailable during Explorer's OLE loop.
- The asynchronous worker initializes COM on the exact thread used for each UIA inspection. `ElementFromPoint` may be a deeply nested icon/text child, so the worker walks a maximum of sixteen raw-view parents to locate the enclosing Explorer ListItem/TreeItem/DataItem. Blank file-view movement without an accessibility drag signal remains rejected.
- A candidate reveals the Island/Notch on the pointer's display, offset about 76 physical pixels below the top so it does not compete for the primary Windows Drop Tray strip. One placement policy supplies this same physical anchor to DragApproaching, DragReady, Compact, Expanded, Dismissing, mode transitions, Dynamic Island, and Notch. The fixed host is 440 DIP tall so the offset Expanded surface and native region remain aligned even at 100% DPI. The visible HWND registers an OLE target at reveal and revokes it on Hidden. Final acceptance still requires `CF_HDROP`; a candidate never reads or stores files itself.
- Classic mode retains the former uniform-alpha 1/255, 960-DIP × 12-physical-pixel `HTCLIENT` top-edge host for compatibility. It is default-off, explicitly warns about title-bar/Drop Tray conflicts, and is destroyed immediately when the user changes mode. Disabled mode provides only main-window, already-visible Overlay and Windows Share entry points.

The OLE target checks `CF_HDROP`, advertises `DROPEFFECT_COPY`, extracts paths only on Drop, and reports only monitor/DPI/bounds/format/item-count diagnostics. Compact/Expanded and a Smart-revealed target use the same visual registration and `AddPathsAsync` business path. The low-level hooks observe only session boundaries; there is no injection, input suppression, mouse-button scan or cursor polling loop.

### Fullscreen classification

Monitor bounds alone do not imply a full-screen application: the Windows desktop itself covers the monitor. The foreground classifier requires a visible, uncloaked, non-iconic, top-level non-tool window on the target monitor and excludes desktop/shell handles plus `Progman`, `WorkerW`, `Shell_TrayWnd`, and `Shell_SecondaryTrayWnd`. Fullscreen suppression morphs to Hidden and restores from the retained spring state; it does not snap. Drag states override passive suppression.

When a shell drag enters, the known host identifies the monitor, the Core state machine changes targets, and the separate visual surface reveals. Non-active monitors retain only their enabled hosts. Monitor coordinates are physical pixels; host and UI dimensions are DIPs scaled with each monitor's effective DPI. `WM_DISPLAYCHANGE` rebuilds monitor-bound hosts and visual windows.

Official references: [RegisterDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop), [IDropTarget](https://learn.microsoft.com/en-us/windows/win32/api/oleidl/nn-oleidl-idroptarget), and [DragQueryFile](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-dragqueryfilew).

Smart detection uses only documented observation surfaces: [object drag event constants](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants), [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook), [UI Automation drag support](https://learn.microsoft.com/en-us/windows/win32/winauto/ui-automation-support-for-drag-and-drop), [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc), and the system drag thresholds exposed by [GetSystemMetrics](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics). Provider coverage is best effort; these signals never replace final OLE validation.

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

Installed and portable builds use the current user's standard Run key with an explicitly quoted executable plus `--startup`. It is enabled by default, can be disabled/re-enabled in Settings, follows a moved portable executable on the next manual launch, starts with the main window hidden, and uses the existing single-instance/tray lifecycle. It does not request elevation. The Inno uninstaller deletes only DropSpace's value. Windows may still let the user disable startup centrally; DropSpace never changes system security policy.

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

## Windows 11 Drop Tray and Share Target

Drop Tray is Windows Shell UI at the top-center edge formerly shared with DropSpace's passive reveal host. Smart mode avoids that contest by recognizing a bounded candidate first and revealing the temporary target below the primary Drop Tray strip. DropSpace does not fight Shell with larger transparent topmost windows, injection, polling, registry feature flags or repeated `SetWindowPos`.

Two public paths coexist:

- Smart mode: an identified Explorer/Desktop candidate reveals below the edge and the visible target completes OLE validation. Classic mode remains available when broader legacy source compatibility is more important than the edge-input trade-off.
- Drop Tray on: a trusted identity build declares `windows.shareTarget` for `StorageItems`. The Share operation uses `ReportStarted`, adds accessible files/folders through `MainViewModel.AddPathsAsync`, then calls `ReportCompleted` or `ReportError`. Redirection through `AppInstance` keeps one database writer.

Microsoft's public Drop Tray description says its **More…** action opens Windows Share. A registered DropSpace target is therefore reachable in the full Share UI. No public contract guarantees that DropSpace is directly pinned in the compact Drop Tray suggestion row; that is Windows version/relevance dependent. No stable public API is documented for querying the Drop Tray toggle. Settings therefore explains the conflict and launches `ms-settings:multitasking` without claiming current state.

The external-location identity is `AiranLuo.DropSpace.Identity`, Publisher `CN=airanluo-dot`, Application Id `DropSpace`. The sparse package contains identity/Share metadata and assets; Inno continues to own the self-contained EXE. Trusted signing is mandatory. Unsigned Preview Setup and Portable omit registration and never install certificates. A signed CI build signs EXE, MSIX and identity, verifies the identity, builds Setup with the signed identity, signs Setup, and unregisters identity on uninstall.

Official references: [Receive content with the Share Target contract](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/integrate-sharesheet-receive), [grant package identity to an external-location desktop app](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps), and [Microsoft's Drop Tray Release Preview description](https://blogs.windows.com/windows-insider/2026/04/17/releasing-windows-11-builds-26100-8313-and-26200-8313-to-the-release-preview-channel/).

## In-app update integration

Installer deployments are identified by exact `HKCU\Software\DropSpace\Install\InstallPath` ownership before sparse package identity is considered. Portable has neither a matching installer registration nor package identity. Full MSIX/package deployments remain Windows-managed. The updater launches Inno with structured arguments `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /LOG=<owned path>`; Setup then owns graceful maintenance shutdown and post-success restart. Windows Authenticode validation uses `WinVerifyTrust`, but unattended installation additionally requires the exact compiled DropSpace publisher identity rather than any valid certificate.

Release discovery uses `https://dropspace.pages.dev/api/v1/releases.json`, the GitHub Pages mirror, then GitHub REST. The official website contract is schema-versioned, bounded to 20 entries and contains only release metadata plus official same-tag GitHub asset identities. Cloudflare Pages refreshes it from GitHub with a short cache; the static mirror is rebuilt by release automation. A response cannot supply an arbitrary executable URL, and later manifest/download verification is unchanged.

## Visible Overlay file drop

Compact/Expanded visible pixels are direct targets. The XAML Surface accepts `StorageItems`; the root HWND retains a native `CF_HDROP` adapter as a second compatible route for Shell sources that select the root. The passive top-edge HWND is hidden while stable visible geometry owns input, so targets never overlap. A selected OLE owner is retained through Drop/Leave even if animation changes geometry. Diagnostics record root/descendant `WindowFromPoint`, format availability, target kind and accepted counts, never paths.

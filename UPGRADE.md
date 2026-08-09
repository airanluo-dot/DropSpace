# DropSpace upgrade contract

DropSpace installers use one permanent Inno Setup AppId and `UsePreviousAppDir=yes`. A newer `DropSpaceSetup.exe` therefore recognizes the existing per-user installation, inherits a custom directory and shortcut choices, appends the uninstall log, and replaces the program payload without requiring a prior uninstall.

Before files are replaced, Setup checks the `Local\DropSpace.Running.v1` mutex. If DropSpace is running, Setup opens the application-owned `Local\DropSpace.MaintenanceShutdown.v1` and `Local\DropSpace.MaintenanceStopped.v1` kernel events, signals the request, and waits for bounded completion. This avoids bootstrapping a second WinUI process during maintenance.

External maintenance tools can request the same handshake through:

```text
DropSpace.exe --shutdown-for-maintenance
```

The running process uses the normal shutdown path to stop Overlay animation, revoke OLE targets, remove clipboard/tray listeners, flush settings/SQLite/logging services, close the main window, signal completion, and exit. Setup waits for the stopped event and process mutex release. It never uses `taskkill /f`; if graceful shutdown fails it stops with a clear instruction to exit DropSpace manually.

The installer records a monotonic numeric `VersionCode` below `HKCU\Software\DropSpace\Install`. Older Setup builds block a silent downgrade by default. A deliberate test downgrade requires `/ALLOWDOWNGRADE=1` and should never be used for ordinary updates.

Future in-app update code can download the fixed asset name `DropSpaceSetup.exe`, verify release metadata/hash/signature policy, then invoke:

```text
DropSpaceSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Inno Setup returns `0` only for a completed setup; any non-zero documented or future exit code is failure. The updater must not force-close DropSpace and must not delete `%LOCALAPPDATA%\DropSpace` on upgrade or rollback.

Release version comes from repository-root `RELEASE_VERSION`. It drives EXE ProductVersion/FileVersion, installer AppVersion/VersionInfo, workflow tag/title, baseline/current lifecycle fixtures and release notes selection. Commercial signing remains optional: when Artifact Signing credentials are configured, Setup, portable EXE and MSIX are all signed and verified before publishing; no private signing material is stored in Git.

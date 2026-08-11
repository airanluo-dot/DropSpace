# DropSpace upgrade contract

DropSpace installers use one permanent Inno Setup AppId and `UsePreviousAppDir=yes`. A newer `DropSpaceSetup.exe` therefore recognizes the existing per-user installation, inherits a custom directory and shortcut choices, appends the uninstall log, and replaces the program payload without requiring a prior uninstall.

The `StartWithWindows` preference remains in `%LOCALAPPDATA%\DropSpace\settings.json`, so upgrades preserve an explicit user disable. On the next successful launch DropSpace reconciles its one per-user Run value to the upgraded executable path; Setup does not blindly re-enable startup.

Before files are replaced, Setup checks the `Local\DropSpace.Running.v1` mutex. If DropSpace is running, Setup opens the application-owned `Local\DropSpace.MaintenanceShutdown.v1` and `Local\DropSpace.MaintenanceStopped.v1` kernel events, signals the request, and waits for bounded completion. This avoids bootstrapping a second WinUI process during maintenance.

External maintenance tools can request the same handshake through:

```text
DropSpace.exe --shutdown-for-maintenance
```

The running process uses the normal shutdown path to stop Overlay animation, revoke OLE targets, remove clipboard/tray listeners, flush settings/SQLite/logging services, close the main window, signal completion, and exit. Setup waits for the stopped event and process mutex release. It never uses `taskkill /f`; if graceful shutdown fails it stops with a clear instruction to exit DropSpace manually.

The installer records a monotonic numeric `VersionCode` below `HKCU\Software\DropSpace\Install`. Older Setup builds block a silent downgrade by default. A deliberate test downgrade requires `/ALLOWDOWNGRADE=1` and should never be used for ordinary updates.

The v0.1.0 in-app updater downloads the fixed asset name `DropSpaceSetup.exe`, validates the strict release manifest, size and SHA-256, revalidates immediately before execution, then invokes:

```text
DropSpaceSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /LOG=<DropSpace-owned path>
```

The updater starts Setup before the current process exits. Setup requests the existing maintenance handshake, replaces files, and only `/UPDATE` starts `DropSpace.exe --updated <version>` after success. Inno Setup returns `0` only for a completed setup; any non-zero documented or future exit code is failure. The updater never force-closes DropSpace and never deletes `%LOCALAPPDATA%\DropSpace` on upgrade or rollback.

The public v0.1.0 build is unsigned. It supports checks, streaming download and integrity verification, but unattended installation is disabled. A future signed build must pass `WinVerifyTrust` and an exact compiled DropSpace signer-subject allow-list; any merely valid third-party signature is rejected. Portable deployments never run Setup, and Package/MSIX deployments remain Windows-managed.

Signed upgrades reuse the same `AiranLuo.DropSpace.Identity` Name, `CN=airanluo-dot` Publisher and `DropSpace` Application Id. Only its four-part version changes. Setup registers the newer signed package against the inherited `{app}` path after graceful maintenance shutdown. Unsigned builds do not attempt registration, so an unsigned update never asks users to weaken trust policy.

Release version comes from repository-root `RELEASE_VERSION`. It drives EXE ProductVersion/FileVersion, installer AppVersion/VersionInfo, workflow tag/title, baseline/current lifecycle fixtures and release notes selection. Commercial signing remains optional: when Artifact Signing credentials are configured, Setup, portable EXE and MSIX are all signed and verified before publishing; no private signing material is stored in Git.

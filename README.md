<img alt="DropSpace" src="branding/generated/docs/DropSpace-Logo-Black.png" width="220">

# DropSpace

DropSpace is a local-first Windows 11 workspace for temporarily holding file references and recent clipboard content. Its main window provides full management, while a top-center Dynamic Island/Notch provides a fast file drop surface over the same Temporary Space.

[![Windows CI](https://github.com/airanluo-dot/DropSpace/actions/workflows/ci.yml/badge.svg)](https://github.com/airanluo-dot/DropSpace/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

## Status

DropSpace **v0.1.0 is the first Stable release**. The repository contains the WinUI 3 application, a standard per-user installer, portable and MSIX deployment paths, automated lifecycle tests, Windows CI/release automation, and the product/engineering specifications that define its safety boundaries.

Latest Stable: [v0.1.0](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.1.0). The optional Preview update channel receives both Stable and Preview releases and always selects the highest eligible SemVer without downgrading.

The implemented vertical slice includes:

- Space file/folder reference staging with drag-in, picker intake, external drag-out, open, copy path, pin, remove, and Locate/Replace.
- Event-driven, bounded Clipboard history driven by the desktop `WM_CLIPBOARDUPDATE` listener for text, URLs, colors, code-like text, images, and Explorer file/folder references. Image and file batch limits are user-configurable.
- Unified search, Pinned, image copy/export, retention, range-based clear, persistent Pause, theme, and close behavior.
- SQLite persistence, atomic settings/payload writes, schema validation/recovery, redacted rolling logs, single-instance activation, and a native notification-area menu.
- Deterministic branded Windows assets and x64/ARM64 project configurations.
- A responsive header that stacks controls before text scaling can collapse the page title, an embedded Win32 taskbar/tray icon chain, and a documented brand-asset map.
- A separate visually transparent Win32/OLE top-edge drag host, truly hidden visual Overlay, formal state machine, Compact/Expanded surface, and immediately switchable Dynamic Island/Notch geometry.
- Direct Explorer/Desktop drops onto the visible Compact or Expanded Overlay, with one OLE owner per pixel and Expanded in-place drop feedback.
- Windows 11 Drop Tray compatibility guidance plus a standard `StorageItems` Share Target contract. The external-location identity is registered only by a future trusted-signed Setup; unsigned previews never install a self-signed certificate.
- A win-x64 unpackaged, self-contained, single-file `DropSpace.exe` release path that persists data below `%LOCALAPPDATA%\DropSpace`.
- A pinned Inno Setup 7.0.2 `DropSpaceSetup.exe` with custom per-user install path, independent uninstaller, stable product identity, graceful in-place upgrades, preserve-data uninstall, and explicit complete-uninstall mode.
- Per-user Windows startup enabled by default and controlled in Settings; disabling it removes only DropSpace's own `HKCU` Run value.
- Process-lifetime in-app update checks, repeatable manual checks, Stable/Preview channels, streaming downloads, size/SHA-256 verification, trusted-publisher auto-install gating, and Inno `/UPDATE` graceful restart.

Windows CI audits dependencies, builds the x64 app, portable EXE, installer, unsigned MSIX and external-location identity artifact, and runs policy/persistence tests. It starts the built app and verifies Windows App SDK/SQLite/AppData initialization, real Win32 clipboard notification/persistence/Pause/Resume/self-write suppression, activation and visible-target discovery, synthetic CF_HDROP delivery in Compact/Expanded, 200 serialized deletion cycles, 100 Overlay lifecycle cycles, 1,000 interruptible Notch geometry cycles, second-instance redirection, graceful maintenance shutdown, silent install, in-place upgrade, both uninstall modes, and external-file sentinel protection. Real Explorer/Desktop Shell routing, Drop Tray/Share ranking, zero-pixel Hidden appearance, installer wizard appearance, tray recovery after Explorer restart, accessibility, mixed-DPI geometry, and animation feel remain manual release-candidate validation gates and are not claimed by automation.

## Download and run

### 1. Windows Installer — recommended

Download `DropSpaceSetup.exe` from the official [GitHub Releases](https://github.com/airanluo-dot/DropSpace/releases) page. Setup installs per user by default to `%LOCALAPPDATA%\Programs\DropSpace`, does not require administrator rights, allows a custom writable directory, creates the Start Menu entry, and registers an independent uninstaller in Windows Installed Apps. Running a newer Setup upgrades in place and preserves `%LOCALAPPDATA%\DropSpace`.

Normal uninstall keeps local DropSpace data for reinstall. Select “also delete all DropSpace local data and settings” for complete uninstall. Neither mode follows Temporary Space references or deletes original files outside DropSpace-owned roots. See [INSTALL.md](INSTALL.md) and [UPGRADE.md](UPGRADE.md).

### 2. Direct / Portable EXE

Download `DropSpace.exe` from the official [GitHub Releases](https://github.com/airanluo-dot/DropSpace/releases) page and double-click it. It is self-contained and requires no Visual Studio, separate .NET runtime, Windows App SDK runtime installation, PowerShell, certificate installation, or administrator rights.

The first Stable build is not commercially code-signed. SmartScreen may show an unknown-app warning on first launch; obtain the file only from the official release and compare it with `SHA256SUMS.txt`.

### 3. MSIX — alternative package

`DropSpace-x64.msix` remains an alternate Windows package. The package is unsigned, so ordinary users should prefer Setup; MSIX certificate/signing policy is intentionally not bypassed.

### 4. Developer build

Only contributors building from source need Visual Studio or the .NET/Windows SDK toolchain described below. These tools are not runtime prerequisites for the downloaded `DropSpace.exe`.

## Product boundaries

- Windows 11 native desktop application.
- C#, .NET, WinUI 3, Windows App SDK, and MVVM.
- Local content storage; the updater sends no user content and reads only public GitHub Release metadata when enabled.
- File records are references; removing a record never deletes or moves its source file.
- Clipboard source-app exclusions are best effort and are not treated as a privacy guarantee.
- AI, OCR, accounts, cloud sync, and browser extensions are outside the MVP and V1.1 scope.

## Documentation

- [Product specification](PRODUCT.md)
- [Feature catalogue](FEATURES.md)
- [UX specification](UX.md)
- [Design system](DESIGN_SYSTEM.md)
- [Architecture](ARCHITECTURE.md)
- [Data model](DATA_MODEL.md)
- [Windows integration](WINDOWS_INTEGRATION.md)
- [Privacy and threat model](PRIVACY.md)
- [Edge cases](EDGE_CASES.md)
- [Roadmap](ROADMAP.md)
- [Test plan](TEST_PLAN.md)
- [Decisions](DECISIONS.md)
- [Logo and icon asset map](BRAND_ASSETS.md)
- [Licensing policy](LICENSING.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [Trademark and brand policy](TRADEMARKS.md)
- [Contributing](CONTRIBUTING.md)
- [Agent rules](AGENTS.md)

## Development workflow

### Requirements

- Windows 11 build 26100 or later.
- Visual Studio 2026 with the WinUI application development workload, or the .NET 10 SDK for command-line build/test.

### Build and test

```powershell
dotnet restore DropSpace.sln -p:Configuration=Release
dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj -c Release
dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj -c Release
dotnet build src/DropSpace.App/DropSpace.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Open `DropSpace.sln` in Visual Studio to deploy the packaged app locally. The manifest targets Windows 11 build 26100 and includes x64 and ARM64 configurations.

To validate package generation from PowerShell, run:

```powershell
./scripts/Build-UnsignedPackage.ps1 -Platform x64
```

The output under `artifacts/msix` is deliberately unsigned. MSIX installation requires a certificate whose subject matches the manifest publisher; production signing credentials are never committed to this repository. Windows CI publishes the unsigned package as a build artifact for downstream signing and release validation.

### Local data

Installed, portable and MSIX builds use `%LOCALAPPDATA%\DropSpace` for the database, payloads, thumbnails, backups, settings, quarantine, and redacted logs. Replacing or upgrading `DropSpace.exe` does not replace this data. DropSpace does not require a server or account, and clipboard contents and full file paths are never intentionally written to diagnostics.

Work is implemented on task branches. Each meaningful, verified change is committed and pushed; `main` remains buildable. A phase is merged only after its acceptance criteria pass and the related documentation is updated.

## License

Copyright 2026 Airan Luo.

DropSpace's original source code, documentation, and repository-owned assets are licensed under the [Apache License 2.0](LICENSE) (`Apache-2.0`). You may use, modify, distribute, and use the project commercially subject to that license.

Third-party dependencies remain under their respective licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). The Apache license does not grant a general right to present a modified distribution as an official DropSpace release; see [TRADEMARKS.md](TRADEMARKS.md).

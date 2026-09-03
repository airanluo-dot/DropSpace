<img alt="DropSpace" src="branding/generated/docs/DropSpace-Logo-Transparent.png" width="220">

# DropSpace

DropSpace is a local-first Windows 10 and 11 workspace for temporarily holding file references and recent clipboard content. Its main window provides full management, while a top-center Dynamic Island provides a fast file drop surface over the same Temporary Space.

Official website: https://airanluo-dot.github.io/DropSpace/

[![Windows CI](https://github.com/airanluo-dot/DropSpace/actions/workflows/ci.yml/badge.svg)](https://github.com/airanluo-dot/DropSpace/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

## Status

DropSpace **v0.2.1 is the current Stable release and v0.3.0-preview.11 is the current Preview**. The repository contains the WinUI 3 application, a standard per-user installer, portable and MSIX deployment paths, automated lifecycle tests, Windows CI/release automation, and the product/engineering specifications that define its safety boundaries.

Latest Stable: [v0.2.1](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.1). Latest Preview: [v0.3.0-preview.11](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0-preview.11). The optional Preview update channel receives both Stable and Preview releases and always selects the highest eligible SemVer without downgrading.

The **v0.3.0-preview.11** full-audit hardening build keeps every Smart Drag candidate invisible until positive file-like OLE evidence, hardens the Dynamic Island's borderless native HWND/DWM boundary, closes encrypted-share quota and revoke races, and completes explicit, collision-safe Quick Action image exports. It remains conditional until the hosted Windows and manual Explorer/provider, cursor-feedback, border-leak, DPI, and Quick Action matrix is evidenced; see the [Preview.10 recovery test plan](docs/test-plan/v0.3.0-preview.10.md).

The **v0.3.0-preview.9** usability build adds Installer-only per-user Explorer and SendTo intake, capability-driven custom Quick Actions for four content profiles, and one eight-second Undo slot for removals, Clipboard clear, and pin changes. It preserves the local-first/source-safe boundary and remains conditional until the hosted Windows and manual Explorer, installer, DPI, startup, and Undo matrix is evidenced; see the [Preview.9 test plan](docs/test-plan/v0.3.0-preview.9.md).

The **v0.3.0-preview.8** compatibility build targets 64-bit Windows 10 version 1809 (Build 17763) or later, including Windows 11. Win11-only visuals and APIs are capability-gated with Windows 10 fallbacks. The release remains conditional until the OS/build/DPI/multi-monitor and real Explorer/provider matrix in [compatibility-baseline.md](compatibility-baseline.md) has executable evidence.

The **v0.3.0-preview.7** completion-hardening build finishes the bounded 3.0 contracts: standard mDNS/DNS-SD discovery, bilateral SAS pairing, explicit text/URL handoff, clipboard-pause enforcement, actual bounded PDF/media preview surfaces, source-safe image actions, canonical encrypted-share wire vectors, and explicit share revocation. Internet Share remains unavailable until an operator deploys and configures the reference Worker, and physical two-device LAN/browser acceptance remains a release evidence gate.

The historical **v0.3.0-preview.5** test build fixed Automatic placement projection so Adjust began from the actual visible Dynamic Island surface instead of the host-window origin; existing Custom coordinates remained unchanged.

The implemented vertical slice includes:

- Space file/folder reference staging with drag-in, picker intake, external drag-out, open, copy path, pin, remove, and Locate/Replace.
- Event-driven, bounded Clipboard history driven by the desktop `WM_CLIPBOARDUPDATE` listener for text, URLs, colors, code-like text, images, and Explorer file/folder references. Image and file batch limits are user-configurable; immediately repeated identical snapshots are collapsed without globally deduplicating later `A → B → A` history.
- Unified search, Pinned, image copy/export, retention, range-based clear, persistent Pause, display-language selection, theme, and close behavior.
- SQLite persistence, atomic settings/payload writes, schema validation/recovery, redacted rolling logs, single-instance activation, and a native notification-area menu.
- Deterministic branded Windows assets and x64/ARM64 project configurations.
- A responsive header that stacks controls before text scaling can collapse the page title, an embedded Win32 taskbar/tray icon chain, and a documented brand-asset map.
- A truly hidden visual Overlay, formal state machine, and one continuously morphing Dynamic Island with Compact/Drop Ready/Expanded states. Smart Drag Detection v2 combines documented drag events, bounded UI Automation/MSAA evidence, source-agnostic mouse-threshold candidates, and an ephemeral 60 ms hollow local OLE verification probe while leaving the screen edge unowned at idle.
- An opt-in traditional top-edge OLE activation zone remains as an explicit compatibility fallback. Settings disclose that it participates in top-edge hit testing and may conflict with Windows Drop Tray or title-bar controls; Smart never switches to it implicitly.
- Direct Explorer/Desktop drops onto the visible Compact or Expanded Overlay, with one OLE owner per pixel and Expanded in-place drop feedback.
- Windows Drop Tray compatibility guidance plus a standard `StorageItems` Share Target contract. The external-location identity is registered only by a future trusted-signed Setup; unsigned previews never install a self-signed certificate.
- A win-x64 unpackaged, self-contained, single-file `DropSpace.exe` release path that persists data below `%LOCALAPPDATA%\DropSpace`.
- A pinned Inno Setup 7.0.2 `DropSpaceSetup.exe` with custom per-user install path, independent uninstaller, stable product identity, graceful in-place upgrades, preserve-data uninstall, and explicit complete-uninstall mode.
- Per-user Windows startup enabled by default and controlled in Settings; disabling it removes only DropSpace's own `HKCU` Run value.
- Process-lifetime in-app update checks, repeatable manual checks, Stable/Preview channels, resilient official website/GitHub metadata sources, streaming downloads, size/SHA-256 verification, trusted-publisher auto-install gating, and Inno `/UPDATE` graceful restart.

Windows CI audits dependencies, builds the x64 app, portable EXE, installer, unsigned MSIX and external-location identity artifact, and runs policy/persistence tests. It starts the built app and verifies Windows App SDK/SQLite/AppData initialization, real Win32 clipboard notification/persistence/consecutive-only duplicate suppression/Pause/Resume/self-write suppression, Smart mouse plus accessibility WinEvent observer registration, hidden top-edge pass-through, temporary visible-target discovery, synthetic CF_HDROP delivery in Compact/Expanded, the ephemeral probe's hollow Region/native styles/single ownership/60 ms cleanup, query-only OLE classification, DPI-aware host containment, 1,000 deduplicated candidate sessions, settings migration, 200 serialized deletion cycles, 100 Overlay lifecycle cycles, 1,000 interruptible Island geometry transitions, second-instance redirection, graceful maintenance shutdown, silent install, in-place upgrade, both uninstall modes, and external-file sentinel protection. Each matrix lane also validates synchronized `en-US`/`zh-CN` resources, rejects CJK source hardcoding, and runs the executable smoke under that resource context. Real Explorer/Desktop and third-party provider coverage, cross-process event delivery, false-positive rate, probe cursor feedback, Drop Tray coexistence, zero-pixel Hidden appearance, actual Windows display-language behavior, accessibility, mixed-DPI geometry, and animation feel remain manual Preview gates and are not claimed by automation.

## Download and run

### 1. Windows Installer — recommended

Download `DropSpaceSetup.exe` from the official [GitHub Releases](https://github.com/airanluo-dot/DropSpace/releases) page. Setup installs per user by default to `%LOCALAPPDATA%\Programs\DropSpace`, does not require administrator rights, allows a custom writable directory, creates the Start Menu entry, and registers an independent uninstaller in Windows Installed Apps. Running a newer Setup upgrades in place and preserves `%LOCALAPPDATA%\DropSpace`.

Normal uninstall keeps local DropSpace data for reinstall. Select “also delete all DropSpace local data and settings” for complete uninstall. Neither mode follows Temporary Space references or deletes original files outside DropSpace-owned roots. See [INSTALL.md](INSTALL.md) and [UPGRADE.md](UPGRADE.md).

### 2. Direct / Portable EXE

Download `DropSpace.exe` from the official [GitHub Releases](https://github.com/airanluo-dot/DropSpace/releases) page and double-click it. It is self-contained and requires no Visual Studio, separate .NET runtime, Windows App SDK runtime installation, PowerShell, certificate installation, or administrator rights.

The current Stable build is not commercially code-signed. SmartScreen may show an unknown-app warning on first launch; obtain the file only from the official release and compare it with `SHA256SUMS.txt`.

### 3. MSIX — alternative package

`DropSpace-x64.msix` remains an alternate Windows package. The package is unsigned, so ordinary users should prefer Setup; MSIX certificate/signing policy is intentionally not bypassed.

### 4. Developer build

Only contributors building from source need Visual Studio or the .NET/Windows SDK toolchain described below. These tools are not runtime prerequisites for the downloaded `DropSpace.exe`.

## Product boundaries

- 64-bit Windows 10 version 1809 (Build 17763) and later native desktop application, including Windows 11.
- C#, .NET, WinUI 3, Windows App SDK, and MVVM.
- Local content storage; the updater sends no user content and reads only the public versioned DropSpace website/GitHub Release metadata when enabled.
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
- [Windows compatibility baseline](compatibility-baseline.md)
- [Preview.8 compatibility test plan](docs/test-plan/v0.3.0-preview.8.md)
- [Preview.9 shell/Quick Actions/Undo test plan](docs/test-plan/v0.3.0-preview.9.md)
- [Preview.10 P0 recovery test plan](docs/test-plan/v0.3.0-preview.10.md)
- [Decisions](DECISIONS.md)
- [Logo and icon asset map](BRAND_ASSETS.md)
- [Licensing policy](LICENSING.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [Trademark and brand policy](TRADEMARKS.md)
- [Contributing](CONTRIBUTING.md)
- [Agent rules](AGENTS.md)

## Development workflow

### Requirements

- 64-bit Windows 10 version 1809 (Build 17763) or later, including Windows 11. Windows 10 is no longer supported by Microsoft, but remains the DropSpace minimum runtime baseline.
- Visual Studio 2026 with the WinUI application development workload, or the .NET 10 SDK for command-line build/test.

### Build and test

```powershell
dotnet restore DropSpace.sln -p:Configuration=Release
dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj -c Release
dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj -c Release
dotnet build src/DropSpace.App/DropSpace.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Open `DropSpace.sln` in Visual Studio to deploy the packaged app locally. The manifest targets Windows 10 build 17763 and includes x64 and ARM64 configurations; Windows 11-only visuals are selected at runtime.

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

## v0.3.0-preview.7 feature boundary

The 3.0 Preview adds bounded Quick Preview providers, capability-driven Quick Actions, Windows-only DropLink handoff, opt-in cross-device clipboard, expiring Nearby browser links, and client-encrypted Internet Share. Preview.7 hardens the protocol boundaries and keeps network features disabled until explicitly enabled: handoff requires trusted peers, Nearby requires a private IPv4 address, and Internet Share requires a configured HTTPS Worker backend. See the [protocol](docs/protocol/droplink-v1.md), [validation plan](docs/test-plan/v0.3.0-preview.7.md), and [network threat model](docs/security/network-threat-model.md). macOS, iOS/iPadOS, Android, Linux, accounts, WebRTC, and native mobile clients remain out of scope.

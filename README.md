# DropSpace

DropSpace is a local-first Windows 11 workspace for temporarily holding file references and recent clipboard content, so it can be found, reused, and moved later without forcing immediate organization.

[![Windows CI](https://github.com/airanluo-dot/DropSpace/actions/workflows/ci.yml/badge.svg)](https://github.com/airanluo-dot/DropSpace/actions/workflows/ci.yml)

## Status

DropSpace is now a native **MVP release candidate**. The repository contains the production WinUI 3 application, Core and Infrastructure layers, automated tests, Windows CI, MSIX configuration, and the product/engineering specifications that define its safety boundaries.

The implemented vertical slice includes:

- Space file/folder reference staging with drag-in, picker intake, external drag-out, open, copy path, pin, remove, and Locate/Replace.
- Event-driven, bounded Clipboard history for text, URLs, colors, code-like text, and resource-limited images.
- Unified search, Pinned, image copy/export, retention, range-based clear, persistent Pause, theme, and close behavior.
- SQLite persistence, atomic settings/payload writes, schema validation/recovery, redacted rolling logs, single-instance activation, and a native notification-area menu.
- Deterministic branded Windows assets and x64/ARM64 project configurations.

Windows CI builds the x64 app and runs the policy/persistence test suites. Explorer/Desktop drag compatibility, tray recovery after Explorer restart, accessibility, mixed-DPI, and signed install/upgrade remain manual release-candidate validation gates and are not claimed by the automated build.

## Product boundaries

- Windows 11 native desktop application.
- C#, .NET, WinUI 3, Windows App SDK, and MVVM.
- Local storage only by default.
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

### Local data

The packaged app stores its database, payloads, thumbnails, backups, settings, and redacted logs below its user-scoped `ApplicationData.LocalFolder/DropSpace` directory. It does not require a server or account. Clipboard contents and file paths are never intentionally written to diagnostics.

Work is implemented on task branches. Each meaningful, verified change is committed and pushed; `main` remains buildable. A phase is merged only after its acceptance criteria pass and the related documentation is updated.

## License

No open-source license has been granted. All rights are reserved unless a license is added later.

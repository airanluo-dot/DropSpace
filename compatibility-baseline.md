# DropSpace Windows compatibility baseline

Status: implementation baseline for `v0.3.0-preview.10` (conditional until the
Windows matrix below has executable evidence).

## Supported operating systems

DropSpace supports 64-bit Windows 10 version 1809 (Build 17763) or later,
including Windows 11. Windows 10 is no longer supported by Microsoft, but it
remains the minimum runtime baseline for this application. Windows App SDK's
support table and versioning guidance are the authority for the framework
relationship:

- [Windows App SDK supported versions](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/support)
- [Windows App SDK versioning overview](https://learn.microsoft.com/en-us/windows/apps/get-started/versioning-overview)
- [Windows App SDK self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

The app is compiled with the Windows 10 SDK Build Tools 10.0.26100.8249 and
targets `net10.0-windows10.0.17763.0`. `SupportedOSPlatformVersion` and
`TargetPlatformMinVersion` are both tied to the shared MSBuild baseline. The
minimum is also repeated in the MSIX manifests, Inno Setup, and the signed
update manifest contract so a distribution path cannot silently advertise a
newer or older platform.

## Compatibility boundary

The existing three-layer architecture remains intact:

- `DropSpace.Core` owns the pure Windows compatibility constants, capability
  states, and policy contracts.
- `DropSpace.Infrastructure` consumes the minimum-build policy when validating
  `update-manifest.json`.
- `DropSpace.App` probes the OS, Windows App SDK runtime, and optional Windows
  API types. It blocks direct portable launches below Build 17763 with a
  diagnostic marker, and the installer/package paths block them before launch.

Win11-only presentation is optional. Mica is applied only after the runtime
capability probe reports it, and the main window has a solid theme resource as
its base visual. The overlay's modern DWM corner/border attributes are skipped
below Build 22000; the existing borderless overlay, physical-pixel placement,
empty idle region, OLE ownership, and Smart Drag v2 state machine remain the
same. PDF, media, and Windows Share APIs are capability-reported so an absent
optional API becomes an unavailable feature rather than a startup or drop-path
failure. Package identity is still checked separately before Windows Share
registration is claimed.

The following product contracts are unchanged:

- Dynamic Island/Overlay behavior, direct visible drops, and Smart Drag v2
  remain the same business path.
- Placement persistence remains schema 9 and continues to use the existing
  per-monitor/DPI policy.
- Clipboard capture remains event-driven, bounded, local, and pause-aware.
- Updates remain official-source, size/hash-verified, publisher-gated, and
  deployment-mode aware; lowering the OS baseline does not weaken update
  validation.

## Distribution consistency

| Surface | Required value | Enforcement |
| --- | --- | --- |
| App target | `net10.0-windows10.0.17763.0` | `DropSpace.App.csproj` |
| Supported platform | `10.0.17763.0` | `SupportedOSPlatformVersion`, `TargetPlatformMinVersion` |
| MSIX and identity | `MinVersion=10.0.17763.0` | `Package.appxmanifest`, identity template |
| Inno Setup | `MinVersion=10.0.17763` | `installer/DropSpace.iss` |
| Update manifest | `minimumWindowsBuild=17763` | generator, parser, tests |
| Modern visual gate | Build `22000` | runtime capability service |
| Compile-time SDK | Build `26100` | pinned Windows SDK Build Tools |

`scripts/Test-WindowsCompatibility.ps1` is executed in both Windows CI and the
release workflow before restore/build. It rejects target/minimum drift, direct
Mica XAML parsing, unguarded modern DWM attributes, updater-policy drift, and
missing baseline documentation.

## Required evidence matrix

The following is the acceptance matrix, not a claim that every row has already
been run. A row is complete only when its OS build, display scale, monitor
topology, deployment mode, application version, and result are recorded from a
real Windows environment.

| OS baseline | Build | Required focus |
| --- | ---: | --- |
| Windows 10 1809 | 17763 | minimum launch, portable guard, installer/MSIX minimum, classic/base visuals |
| Windows 10 1909 | 18363 | normal launch, clipboard, drag/drop, updater, DPI |
| Windows 10 20H2 | 19042 | normal launch, clipboard, drag/drop, updater, DPI |
| Windows 10 22H2 | 19045 | full Windows 10 regression and multi-monitor matrix |
| Windows 11 21H2 | 22000 | Mica/DWM capability boundary and Drop Tray coexistence |
| Windows 11 22H2 | 22621 | full feature and share-contract regression |
| Windows 11 23H2 | 22631 | full feature and share-contract regression |
| Windows 11 24H2 | 26100 | release runner baseline, full feature and packaging regression |

For every supported row, exercise 100%, 125%, 150%, 175%, and 200% display
scales where the OS can configure them; one, two, and three monitor layouts;
primary and non-primary placement; monitor reconnect/topology refresh; and
both x64 Installer and Portable deployments. On Windows 11, include the
packaged/identity Share Target path when a trusted identity package is
available. On Windows 10, verify that the same content path remains usable
through the main window and visible Overlay even though Mica, modern DWM
attributes, and Drop Tray-specific behavior are unavailable.

The critical flows are:

1. Normal launch, tray lifecycle, single-instance redirect, and `--startup`
   with no visible main window.
2. Clipboard text/image/file-reference capture, pause/resume, bounded history,
   and clear/retention behavior.
3. Explorer/Desktop file and folder drags, Smart Drag v2 observation, Classic
   mode fallback, visible Compact/Expanded direct drops, external drag-out,
   and cancellation/escape.
4. Mixed-DPI placement, one-to-three monitors, fullscreen suppression, and
   topology rebuild without a stale or visible idle hit region.
5. PDF/image/text/media preview fallback, source-safe actions, and unavailable
   optional API states.
6. Installer custom path, in-place update, startup registration, normal
   uninstall, complete uninstall, Portable startup, unsigned MSIX validation,
   and update size/SHA-256/publisher gates.

## Current evidence boundary

The source-level compatibility gate, pure policy tests, updater tests, and
Windows workflow definitions are part of this implementation. The current
Linux development environment does not contain `dotnet`, PowerShell, WinUI,
or a Windows display stack, so it cannot produce honest Windows executable,
installer, DPI, OLE, clipboard, or multi-monitor evidence. The first Windows
CI/release run must be inspected after publication, and the rows above remain
conditional until real Windows machines or equivalent dedicated test fixtures
record them. Hosted Windows CI is useful for build/smoke coverage but does not
replace the historical OS/DPI/monitor/provider matrix.

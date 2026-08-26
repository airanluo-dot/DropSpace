# Third-party components and license notices

DropSpace's original source is licensed under Apache-2.0. The components below are dependencies or build services; their source is not incorporated into DropSpace and is not relicensed by the root [LICENSE](LICENSE). Release binaries may contain redistributable object code from the runtime components identified below.

Versions are the versions pinned by `Directory.Packages.props`, `global.json`, the GitHub Actions workflows, and the installer scripts at the time of this notice. Transitive components remain subject to the notices supplied by their upstream packages.

## Runtime and distributed components

| Component | Use | Upstream | License | Distribution status |
|---|---|---|---|---|
| .NET 10 runtime and base libraries | Self-contained managed runtime | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT, with upstream third-party notices | Object code is bundled by the self-contained publish process; no .NET source is copied into this repository. |
| Microsoft Windows App SDK 2.3.1 | WinUI 3, application lifecycle, deployment, and Windows integration | [Microsoft.WindowsAppSDK NuGet package](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.3.1) | Microsoft Software License Terms for the NuGet binary; the package includes its own `NOTICE.txt` | Files binplaced by the package are distributed in the Portable/installed build under Microsoft's distributable-code terms. No Windows App SDK source is copied into this repository. |
| CommunityToolkit.Mvvm 8.4.2 | MVVM source generators and helpers | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) | MIT | Compiled dependency/generator output may be present; upstream source is not vendored. |
| Microsoft.Data.Sqlite 10.0.10 | SQLite ADO.NET provider | [dotnet/efcore](https://github.com/dotnet/efcore) | MIT | Runtime object code is distributed through publish; upstream source is not vendored. |
| Microsoft.Extensions.DependencyInjection 10.0.10 | Dependency injection | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | Runtime object code may be distributed through publish; upstream source is not vendored. |
| Microsoft.Extensions.Logging 10.0.10 and Logging.Abstractions 10.0.10 | Application logging abstractions and services | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | Runtime object code may be distributed through publish; upstream source is not vendored. |
| SQLitePCLRaw.bundle_e_sqlite3 2.1.12 and transitive SQLitePCLRaw packages | Native SQLite binding and bundled SQLite engine | [ericsink/SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) | Apache-2.0; SQLite itself is dedicated to the public domain | Native and managed object code is distributed through publish; source is not vendored. |

The Windows App SDK binary package uses Microsoft-specific terms even though portions of its upstream source repository are open source. Apache-2.0 applies to DropSpace's own work, not to those Microsoft binaries. The package's permitted redistributable files and bundled third-party notices remain governed by its package license.

## Build and test dependencies

| Component | Use | License | Distribution status |
|---|---|---|---|
| Microsoft.Windows.SDK.BuildTools 10.0.26100.8249 | Windows SDK packaging/resource build tools | [Microsoft Windows SDK license](https://aka.ms/WinSDKLicenseURL) | Build-time tool; not vendored or shipped as a standalone DropSpace component. |
| Microsoft.Windows.SDK.BuildTools.WinApp 0.5.0 | Windows application build integration | MIT | Build-time NuGet dependency; source is not vendored. |
| Microsoft.NET.Test.Sdk 18.8.1 | Test host | MIT | Test/build-time only. |
| MSTest.TestAdapter and MSTest.TestFramework 4.3.3 | Automated tests | [MIT](https://github.com/microsoft/testfx/blob/main/LICENSE) | Test/build-time only. |
| Inno Setup 7.0.2 | Builds `DropSpaceSetup.exe` and its independent uninstaller | [Inno Setup License](https://jrsoftware.org/files/is/license.txt) | The compiler is downloaded only by the build environment. Generated Setup/uninstaller components remain subject to the Inno Setup License; Inno source is not copied into this repository. |

## GitHub Actions

The workflows call the following external actions. They execute in CI and are not incorporated into DropSpace source or release binaries:

| Action | License |
|---|---|
| `actions/checkout@v4` | MIT |
| `actions/setup-dotnet@v4` | MIT |
| `actions/upload-artifact@v4` | MIT |
| `actions/download-artifact@v4` | MIT |
| `azure/login@v3` | MIT |
| `azure/artifact-signing-action@v2` | MIT |
| `softprops/action-gh-release@v2` | MIT |

## Source and asset provenance review

- No vendored third-party source tree, generated SDK source, external font, or third-party visual asset was found in `src`, `installer`, `scripts`, `identity`, `tests`, or `.github`.
- The DropSpace icon and its PNG/ICO derivatives were introduced in the project's own implementation history and are covered by the project-level Apache-2.0 policy. Trademark use is addressed separately in [TRADEMARKS.md](TRADEMARKS.md).
- [WinIsland](https://github.com/Eatgrapes/WinIsland) is GPL-3.0 and was reviewed only as a public behavioral and interaction reference. It is not a dependency. No WinIsland source, translated code, control flow, constants, algorithms, assets, or runtime were copied into DropSpace; the audit record is maintained in `DECISIONS.md`.

If a future change incorporates third-party source or assets rather than merely depending on a package, its exact provenance, license text, required notices, and compatibility with Apache-2.0 must be reviewed before merge.

Preview.7 continues to use the QRCoder NuGet package for local QR PNG generation and `System.Security.Cryptography.ProtectedData` for Windows DPAPI-backed identity/peer secrets. Both remain replaceable infrastructure dependencies; neither receives clipboard content or network credentials. Windows.Data.Pdf, Windows.Media.Playback, and Windows.Graphics.Imaging are platform APIs supplied by the target Windows SDK, not vendored third-party code. The reference Cloudflare Worker is first-party repository code and has no runtime dependency in the Windows build.

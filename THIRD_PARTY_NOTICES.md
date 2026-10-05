# Third-party components and license notices

## Optional, independently downloaded NetEase enhancement components

After explicit confirmation, DropSpace can download unchanged official
[BetterNCM 1.3.4](https://github.com/std-microblock/chromatic/tree/1.3.4) and
[InfLink-rs](https://github.com/apoint123/inflink-rs) release artifacts. These
independent GPL-licensed projects run inside the music player and are not
included in DropSpace's executable, linked into its assemblies, or copied as
source. Their upstream source and license notices remain authoritative.
The required Microsoft Visual C++ Redistributable is downloaded separately
from Microsoft and retains Microsoft's software license terms.

DropSpace's original source is licensed under Apache-2.0. The components below are dependencies or build services; their source is not incorporated into DropSpace and is not relicensed by the root [LICENSE](LICENSE). Release binaries may contain redistributable object code from the runtime components identified below.

Versions are the versions pinned by `Directory.Packages.props`, `global.json`, the GitHub Actions workflows, and the installer scripts at the time of this notice. Transitive components remain subject to the notices supplied by their upstream packages.

## Runtime and distributed components

| Component | Use | Upstream | License | Distribution status |
|---|---|---|---|---|
| .NET 10 runtime and base libraries | Self-contained managed runtime | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT, with upstream third-party notices | Object code is bundled by the self-contained publish process; no .NET source is copied into this repository. |
| Microsoft Windows App SDK 2.5.1 | WinUI 3, application lifecycle, deployment, and Windows integration | [Microsoft.WindowsAppSDK NuGet package](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1) | Microsoft Software License Terms for the NuGet binary; the package includes its own `NOTICE.txt` | Files binplaced by the package are distributed in the Portable/installed build under Microsoft's distributable-code terms. No Windows App SDK source is copied into this repository. |
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
| `actions/checkout@v6` | MIT |
| `actions/setup-dotnet@v6` | MIT |
| `actions/upload-artifact@v6` | MIT |
| `actions/download-artifact@v7` | MIT |
| `azure/login@v3` | MIT |
| `azure/artifact-signing-action@v2` | MIT |
| `softprops/action-gh-release@v2` | MIT |

## Source and asset provenance review

- No vendored third-party source tree, generated SDK source, external font, or third-party visual asset was found in `src`, `installer`, `scripts`, `identity`, `tests`, or `.github`.
- The DropSpace icon and its PNG/ICO derivatives were introduced in the project's own implementation history and are covered by the project-level Apache-2.0 policy. Trademark use is addressed separately in [TRADEMARKS.md](TRADEMARKS.md).
- [WinIsland](https://github.com/Eatgrapes/WinIsland) is GPL-3.0 and was reviewed only as a public behavioral and interaction reference. It is not a dependency. No WinIsland source, translated code, control flow, constants, algorithms, assets, or runtime were copied into DropSpace; the audit record is maintained in `DECISIONS.md`.

If a future change incorporates third-party source or assets rather than merely depending on a package, its exact provenance, license text, required notices, and compatibility with Apache-2.0 must be reviewed before merge.

Preview.7 continues to use the QRCoder NuGet package for local QR PNG generation and `System.Security.Cryptography.ProtectedData` for Windows DPAPI-backed identity/peer secrets. Both remain replaceable infrastructure dependencies; neither receives clipboard content or network credentials. Windows.Data.Pdf, Windows.Media.Playback, and Windows.Graphics.Imaging are platform APIs supplied by the target Windows SDK, not vendored third-party code. The reference Cloudflare Worker is first-party repository code and has no runtime dependency in the Windows build.

## Optional local AI lyric translation

- **llama.cpp v0.5.0**, source commit `7fe450e19305b828c199d602c23a8337aaa1f03b`: MIT license. DropSpace builds fixed CPU-only Windows runtime variants from [the official source](https://github.com/ggml-org/llama.cpp/tree/7fe450e19305b828c199d602c23a8337aaa1f03b). Upstream and bundled vendor notices are embedded with the runtime payload in `LICENSE-llama.cpp`.
- **Tencent Hy-MT2-1.8B**: Apache-2.0. Model weights are optional, downloaded only with user consent and are not bundled in the app. [Upstream license](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/9a341cd1b679d3efd23b46e847b01745a71ed792/LICENSE.txt).
- **Tencent Hy-MT2-7B-GGUF Q8_0**: Apache-2.0, Copyright (C) 2026 Tencent. Optional model weights are downloaded from the official repository only after explicit user consent and are not bundled in DropSpace. The default remains the 1.8B model. [Pinned upstream license](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/blob/ab8472660ac61fac25f1af43fac2599d52a8a775/LICENSE.txt) (license SHA256 `746750afa6af28fe4f8b326751ad2a40c700d2e5c459c0a1f6a2e76d99ace224`); [pinned model repository](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/tree/ab8472660ac61fac25f1af43fac2599d52a8a775). The Q8_0 payload is 7,981,928,896 bytes, SHA256 `58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0`.
- **mradermacher Hy-MT2-1.8B IQ3_S**: third-party quantization of those Tencent weights, offered as the experimental compact download under Apache-2.0. [Pinned model repository](https://huggingface.co/mradermacher/Hy-MT2-1.8B-i1-GGUF/tree/9f5c7d98d8b625800775e6197e55c7ed38f2f33a). The app uses a hash-specific runtime EOS correction; it does not modify or rehost the downloaded weights.
## OpenCC artist orthography dictionary

DropSpace embeds OpenCC's TSCharacters dictionary (Apache-2.0), pinned at commit
`3ac34aa439a9908dd49fa92b5174b46314787ac2`. The unmodified dictionary and license are in
`src/DropSpace.Core/Lyrics/Data/` and embedded in `DropSpace.Core.dll`.
Project: https://github.com/BYVoid/OpenCC .

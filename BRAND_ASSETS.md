# DropSpace brand asset map

The repository-owned logo and icon files are distributed under Apache-2.0 as copyrighted works. Their use as source-identifying trademarks remains separate; see [TRADEMARKS.md](TRADEMARKS.md).

## Canonical sources

`DropSpace_Logo_BlackWhite_Package.zip` is the design authority for this release. Its exact source files and SHA-256 values are recorded in [`branding/SOURCE_MANIFEST.json`](branding/SOURCE_MANIFEST.json).

| Role | Canonical source | Runtime policy |
|---|---|---|
| Black Main | `branding/master/black/DropSpace-Black-Main-Original.png` | The only source used to generate runtime, package, installer, and documentation assets. |
| Black packaged resamples | `branding/master/black/*Resampled*.png` | Retained verbatim for provenance; not used as a substitute for the original. |
| White Backup | `branding/master/white/DropSpace-White-Backup-Original.png` | Retained verbatim as the supplied backup; never selected by the default build. |
| White packaged resamples | `branding/master/white/*Resampled*.png` | Retained verbatim for provenance; never selected by the default build. |

The artwork includes an authored black canvas, glow, and reflection. DropSpace does not try to extract a transparent mark or synthesize a light-theme variant, because either operation would materially change the supplied artwork. Generated square, wide, and splash surfaces therefore keep a black canvas.

Run `scripts/Generate-BrandAssets.ps1` on Windows to regenerate `src/DropSpace.App/Assets/` and `branding/generated/docs/`. `-Verify` regenerates into a temporary directory and byte-compares every output. CI performs generation before compiling and validates every consumer.

## Windows icon sizing

`AppIcon.ico` contains exactly 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel PNG-compressed frames. Every frame is a high-quality downscale of Black Main Original. The package does not provide a separate small-size optical mark, so DropSpace does not invent one or reuse the retired purple Mini Mark.

## Runtime and packaging consumers

| Surface | Source / integration |
|---|---|
| EXE, main window, taskbar, Alt+Tab | `DropSpace.rc` embeds generated `AppIcon.ico` as stable resource ID 101; `NativeApplicationIcon` applies both Win32 sizes. |
| Tray | `NativeTrayService` loads embedded resource 101, independent of loose single-file extraction paths. |
| Start Menu / Desktop | Inno shortcuts reference installed `DropSpace.exe`, icon index 0. |
| Setup wizard | `SetupIconFile` consumes generated `AppIcon.ico`. |
| Installed Apps / uninstaller | `UninstallDisplayIcon` references installed `DropSpace.exe`; Inno carries the Setup icon into its uninstaller. |
| MSIX / Search / Share identity | Package manifests resolve generated Square44, Square150, Store, Wide, Splash, scale, targetsize, and altform-unplated PNGs. |
| README | `branding/generated/docs/DropSpace-Logo-Black.png`, generated from Black Main Original. |

## Replacement contract

1. Replace the canonical package files and update `branding/SOURCE_MANIFEST.json` only after verifying their provenance and hashes.
2. Run `scripts/Generate-BrandAssets.ps1`, review every generated change, then run it again with `-Verify`.
3. Do not change Win32 resource ID 101, the Inno AppId, executable name, or package identities for a visual refresh.
4. Run `scripts/Test-BrandAssets.ps1` against final Portable and Setup executables.
5. Manually verify Windows icon-cache surfaces, 100–200% DPI, Light/Dark themes, and 16–32px clarity. DropSpace never deletes Windows icon caches or restarts Explorer.

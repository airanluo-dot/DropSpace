# DropSpace brand asset map

The repository-owned logo and icon files are distributed under Apache-2.0 as copyrighted works. Their use as source-identifying trademarks remains separate; see [TRADEMARKS.md](TRADEMARKS.md).

## Canonical sources

`branding/master/` is the only design authority. Runtime PNG/ICO exports are generated, not redesigned.

| Brand role | Canonical source |
|---|---|
| Full App Icon | `DropSpace-AppIcon-Master-2048.png` |
| Flat vector | `DropSpace-AppIcon-Flat-Vector.svg` |
| Mini Mark | `DropSpace-MiniMark-Master.svg` and approved purple PNG export |
| Wordmark | `DropSpace-Wordmark-Master.svg` |
| Horizontal Lockup | `DropSpace-Lockup-Horizontal.svg` plus approved black/white exports |
| Brand specification | `branding/BRAND_SPEC.md` and `branding/COLOR_PALETTE.txt` |

Run `scripts/Generate-BrandAssets.ps1` on Windows to regenerate `src/DropSpace.App/Assets/` and `branding/generated/docs/`. CI generates the same outputs before compiling and validates the complete consumer chain.

## Optical sizing

`AppIcon.ico` contains exactly 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel PNG-compressed frames. The official Purple Mini Mark is used at 16–32px for legibility. The full 3D App Icon is used at 40–256px. This is an approved optical export choice, not a separate logo design.

## Runtime and packaging consumers

| Surface | Source / integration |
|---|---|
| EXE, main window, taskbar, Alt+Tab | `DropSpace.rc` embeds generated `AppIcon.ico` as stable resource ID 101; `NativeApplicationIcon` applies both Win32 sizes. |
| Tray | `NativeTrayService` loads the same embedded resource 101, independent of a loose single-file extraction path. |
| Start Menu / Desktop | Inno shortcuts reference installed `DropSpace.exe`, icon index 0. |
| Setup wizard | `SetupIconFile` consumes generated `AppIcon.ico`. |
| Installed Apps / uninstaller | `UninstallDisplayIcon` references the installed EXE; Inno carries the Setup icon into its uninstaller. |
| MSIX / Search / Share identity | `Package.appxmanifest` and the external identity manifest resolve generated Square44, Square150, Store, Wide, Splash, scale, targetsize, and altform-unplated PNGs. |
| README | Light/dark `<picture>` sources use the official black/white Horizontal Lockup from `branding/generated/docs/`. |

## Replacement contract

1. Replace only the approved canonical masters in `branding/master/`.
2. Run `scripts/Generate-BrandAssets.ps1` and review every generated change.
3. Do not change Win32 resource ID 101, the Inno AppId, executable name, or package identities for a visual refresh.
4. Run `scripts/Test-BrandAssets.ps1` against final Portable and Setup executables.
5. Manually verify Windows icon cache surfaces, 100–200% DPI, Light/Dark themes, and 16–32px clarity. DropSpace never deletes Windows icon caches or restarts Explorer.

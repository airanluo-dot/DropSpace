# DropSpace brand asset map

The repository-owned logo and icon files are distributed under Apache-2.0 as copyrighted works. Their use as source-identifying trademarks remains separate; see [TRADEMARKS.md](TRADEMARKS.md).

## Canonical sources

`DropSpace-Logo-Transparent-Final.png` is the active design authority. Its exact SHA-256 and every retained predecessor are recorded in [`branding/SOURCE_MANIFEST.json`](branding/SOURCE_MANIFEST.json).

| Role | Canonical source | Runtime policy |
|---|---|---|
| Transparent Final | `branding/master/transparent/DropSpace-Logo-Transparent-Final.png` | The only active source for runtime, package, installer, documentation, and website assets. The supplied RGBA pixels are preserved; no background is synthesized for square icon surfaces. |
| Black Main | `branding/master/black/DropSpace-Black-Main-Original.png` | Retained verbatim as an inactive predecessor for provenance and explicit rollback only. |
| Black packaged resamples | `branding/master/black/*Resampled*.png` | Retained verbatim and inactive. |
| White Backup | `branding/master/white/DropSpace-White-Backup-Original.png` | Retained verbatim and inactive. |
| White packaged resamples | `branding/master/white/*Resampled*.png` | Retained verbatim and inactive. |
| Previous runtime exports | `branding/legacy/runtime-black-v1/` and `branding/generated/docs/DropSpace-Logo-Black.png` | The complete old 43-file Windows export set plus website and documentation exports remain in the repository, but no active build or website reference points to them. |

The Final artwork is a true-alpha PNG. Square Windows and website images retain that transparency. The Open Graph social card is the sole intentionally opaque export: it places the same unmodified mark on a black 1200×630 presentation canvas so link previews do not choose an arbitrary background.

Run `scripts/Generate-BrandAssets.ps1` on Windows to regenerate `src/DropSpace.App/Assets/`, `branding/generated/docs/`, and the three website brand assets. `-Verify` regenerates into a temporary directory and byte-compares every active output. CI performs generation before compiling and validates every consumer while also proving the old exports remain present but inactive.

## Windows icon sizing

`AppIcon.ico` contains exactly 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel PNG-compressed frames. Every frame is a high-quality alpha-preserving downscale of Transparent Final. No unsupplied optical variant is invented.

## Runtime and packaging consumers

| Surface | Source / integration |
|---|---|
| EXE, main window, taskbar, Alt+Tab | `DropSpace.rc` embeds generated `AppIcon.ico` as stable resource ID 101; `NativeApplicationIcon` applies both Win32 sizes. |
| Tray | `NativeTrayService` loads embedded resource 101, independent of loose single-file extraction paths. |
| Start Menu / Desktop | Inno shortcuts reference installed `DropSpace.exe`, icon index 0. |
| Setup wizard | `SetupIconFile` consumes generated `AppIcon.ico`. |
| Installed Apps / uninstaller | `UninstallDisplayIcon` references installed `DropSpace.exe`; Inno carries the Setup icon into its uninstaller. |
| MSIX / Search / Share identity | Package manifests resolve generated Square44, Square150, Store, Wide, Splash, scale, targetsize, and altform-unplated PNGs. |
| README | `branding/generated/docs/DropSpace-Logo-Transparent.png`, generated from Transparent Final. |
| Website header, footer, demo, download card, favicon and web manifest | `website/_source/src/assets/dropspace-logo.png` and `favicon.png`, generated from Transparent Final. |
| Website social preview | `website/_source/src/assets/og-image.png`, generated from Transparent Final on the documented black presentation canvas. |

## Replacement contract

1. Add a new canonical source and update `branding/SOURCE_MANIFEST.json` only after verifying provenance, Alpha, dimensions, and SHA-256. Do not delete prior sources or archived exports.
2. Run `scripts/Generate-BrandAssets.ps1`, review every generated change, then run it again with `-Verify`.
3. Do not change Win32 resource ID 101, the Inno AppId, executable name, or package identities for a visual refresh.
4. Run `scripts/Test-BrandAssets.ps1` against final Portable and Setup executables.
5. Manually verify Windows icon-cache surfaces, 100–200% DPI, Light/Dark themes, and 16–32px clarity. DropSpace never deletes Windows icon caches or restarts Explorer.

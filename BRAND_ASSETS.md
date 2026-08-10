# DropSpace logo and icon asset map

The repository-owned logo and icon files are distributed under Apache-2.0 as copyrighted works. Their use as source-identifying trademarks is separate from that copyright license; see [TRADEMARKS.md](TRADEMARKS.md).

`src/DropSpace.App/Assets/AppIcon.ico` is the canonical desktop icon source. It contains 16, 24, 32, 48, 64, 128, and 256 pixel square frames. Replace and validate this file first when the DropSpace logo changes.

## Runtime and packaging consumers

| Surface | Source / integration |
|---|---|
| Main window and taskbar | `src/DropSpace.App/DropSpace.rc` embeds `AppIcon.ico` as resource ID 101; `NativeApplicationIcon` loads the executable resource and sends `WM_SETICON` for both sizes. |
| Tray | `NativeTrayService` uses the same embedded resource via `NativeApplicationIcon`; it does not depend on a loose file next to the single-file EXE. |
| Portable EXE metadata | `scripts/Compile-Win32Resource.ps1` compiles `DropSpace.rc` before publish. |
| Start Menu / Desktop shortcuts | `installer/DropSpace.iss` points `IconFilename` to installed `DropSpace.exe`, index 0. |
| Setup wizard | Inno Setup `SetupIconFile` consumes the canonical ICO. |
| Installed Apps / uninstaller | `UninstallDisplayIcon` points to installed `DropSpace.exe`; the Inno-generated uninstaller carries the setup icon. |
| WinUI content/developer layout | `DropSpace.App.csproj` includes the canonical ICO; `AppWindow.SetIcon` is only a secondary physical-file fallback. |
| MSIX | `Package.appxmanifest` and `Assets/` visual assets remain the package identity chain; regenerate those PNG assets from the same approved master artwork when branding changes. |

## Replacement checklist

1. Export a square, transparent master and rebuild `AppIcon.ico` with every required size above.
2. Regenerate the MSIX PNG visual assets in `src/DropSpace.App/Assets/` from the same master where the manifest uses them.
3. Do not change Win32 resource ID 101, the Inno AppId, executable name, or package identity for a visual refresh.
4. Run `scripts/Test-BrandAssets.ps1` against the final Portable EXE and installer. The Windows CI and release workflow run it automatically.
5. Manually verify the main window, taskbar at 100–200% scaling, tray, Start Menu shortcut, optional Desktop shortcut, Setup wizard, Installed Apps, uninstaller, Portable EXE, and MSIX tile.

README and release screenshots are documentation assets, not runtime authorities. Update them deliberately after the executable/package chain has passed validation.

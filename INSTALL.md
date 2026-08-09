# Installing DropSpace

## Recommended setup

Download `DropSpaceSetup.exe` from the official GitHub Release and verify it against `SHA256SUMS.txt`. The Preview installer is not commercially signed, so Windows SmartScreen may require “More info → Run anyway”. Never disable Defender or SmartScreen for DropSpace.

Setup uses Inno Setup 7.0.2 and installs per user without administrator rights. The default program directory is:

```text
%LOCALAPPDATA%\Programs\DropSpace
```

The wizard permits another writable directory such as `D:\Apps\DropSpace`. It creates a Start Menu shortcut and offers an optional Desktop shortcut. Windows Settings → Apps → Installed apps registers “DropSpace” with an independent uninstaller. The permanent installer identity is:

```text
AppId: E11EC281-BCE7-4F98-8EEF-2387E202CF0F
```

Program files and user data are separate. The selected install directory contains `DropSpace.exe` and the uninstaller. Database, settings, Clipboard payloads, cache, logs, backups and quarantine remain below `%LOCALAPPDATA%\DropSpace` for installed, portable and MSIX builds.

On first run, DropSpace enables the current user's standard Windows startup entry by default and launches future sign-in instances with `--startup` hidden to the tray. Settings can disable or re-enable it without elevation. Uninstall always removes only DropSpace's startup value.

## Uninstall

Normal uninstall removes program files, the Start Menu/Desktop shortcuts, DropSpace install metadata and any future DropSpace-owned startup/protocol entries, but preserves `%LOCALAPPDATA%\DropSpace` for reinstall.

The uninstaller also presents “Also delete all DropSpace local data and settings”. Selecting it deletes the exact `%LOCALAPPDATA%\DropSpace` app-owned root. It never reads `dropspace.db` for deletion targets and never follows Temporary Space references, so original files and folders elsewhere are outside the deletion scope.

Silent equivalents for isolated automation are:

```powershell
unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGEDATA=0
unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGEDATA=1
```

## Other channels

- `DropSpace.exe`: portable self-contained x64 build; no install/uninstall registration.
- `DropSpace-x64.msix`: unsigned alternative package for environments with an appropriate trust/signing policy.

No channel needs a separate .NET Runtime or Windows App SDK Runtime, PowerShell, a certificate install, or administrator privileges to run the application payload.

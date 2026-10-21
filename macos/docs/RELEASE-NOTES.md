# DropSpace for macOS v0.1.0 Beta 1

A native SwiftUI/AppKit public beta for everyday use, using Windows v0.3.2 Beta 7 as a functional reference. This is not a complete 1:1 port.

Requires macOS 26 or later on Apple Silicon. Intel Macs are not supported. Current runtime validation is on macOS 27.0.1; macOS 26 has not been exercised on a physical machine.

## Everyday workflows

- Temporary Space stores references to files and folders, with preview, open, copy, reveal, pin, remove and undo. Removing a reference does not delete its source.
- Clipboard history supports text, links and images, pause/resume, search and cleanup.
- Native main window, menu bar and expandable Island with saved preferences.
- Apple Music integration, timed lyrics and optional local Hy-MT2 1.8B translation using CPU or Metal. Translation requires downloading the model; it is not included in the installer.
- Resumable downloads, component management, local file actions and nearby sharing.

## Known limitations

- QQ Music login is deferred. Other players expose only the capabilities their public interfaces support.
- DropLink cross-device interoperability and automatic installation of updates are experimental. Physical Windows interoperability is not validated.
- Internet sharing requires a configured service; no hosted service is included.
- 7B inference is not validated on the 16 GiB test Mac. Use the 1.8B model.
- Clipboard polling can miss intermediate rapid changes. Grant requested permissions only for features you use.
- Ad-hoc updates may require renewed per-app Automation or Accessibility permissions.
- Full display/system coverage and comprehensive human review of all ten languages are deferred.

## Installation and updates

**Not notarized by Apple. No Developer ID signature. Locally ad-hoc signed for bundle integrity.**

Download the Mac DMG and compare its SHA-256 with `SHA256SUMS-macos.txt`. Copy DropSpace to Applications, eject the DMG and open the copied app. If blocked and you trust this download, use System Settings → Privacy & Security → Open Anyway, then confirm Open. A managed Mac may disallow this exception. Stop if macOS reports malware or damaged software.

Do not disable Gatekeeper, SIP or global security protections. See [Apple's guidance](https://support.apple.com/en-us/102445).

For updates, download the newer **Mac** DMG, quit DropSpace, replace only the app bundle and reopen it. Preserve its separate application-support data directory. Windows and Mac packages, versions and update feeds are separate. Automatic updates are not advertised as verified.

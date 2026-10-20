# macOS Beta distribution policy

Approved by the owner on 2026-10-20. The Mac version is independently numbered
`v0.1.0 Beta 1`, with Windows `v0.3.2 Beta 7` as its functional reference. Supported
hardware is Apple Silicon; the minimum system is macOS 26. Intel and Windows
packages are not interchangeable.

The application and every embedded executable use local **ad-hoc code signing**.
This detects changes to the signed contents. It does **not** establish the
developer's identity, provide a Developer ID signature, or constitute Apple
notarization. All public Mac download and release surfaces must say:

> Not notarized by Apple. No Developer ID signature. Locally ad-hoc signed.

Chinese disclosure: **未获 Apple 公证、无 Developer ID 签名；使用本地 ad-hoc 签名。**

## Installation instructions to validate against the final download

1. Download the Mac DMG from the official website or its linked GitHub Release.
   Compare its SHA-256 with that release's `SHA256SUMS-macos.txt` when checking
   download integrity. A checksum is not Apple identity authentication.
2. Open the DMG, then drag DropSpace into Applications (or your own Applications
   folder). Eject the disk image and open the copied application.
3. macOS may report that the developer cannot be verified or that Apple cannot
   check the application. After an attempted launch, if you trust this specific
   download, open System Settings → Privacy & Security → Open Anyway and confirm
   Open. macOS may request your local administrator authentication.
4. A managed Mac can disallow this exception. Follow its administrator's policy.
   If macOS reports damage or malware, stop and verify the download; do not treat
   that warning as ordinary proof of an unsigned developer.

These steps follow [Apple's opening guidance](https://support.apple.com/en-us/102445).
They do not require disabling Gatekeeper, SIP, or other system-wide protections.
Do not provide quarantine-removal commands as an installation step.

## Permissions after an ad-hoc update

An ad-hoc rebuild changes its code identity. During local development, the existing
DropSpace Accessibility switch remained on while macOS rejected the newer executable's
code requirement. Removing the stale DropSpace entry and adding the actual current
application in System Settings restored trust after the user unlocked the panel.
A new build may therefore require a renewed per-app authorization. Do not interpret
an enabled old entry as proof that the running build has permission, or modify the
system permission database to preserve access. The production downloaded update
and its permission experience still require validation.

## Evidence boundaries

`codesign --verify --deep --strict` checks signed bundle integrity. A successful
local unquarantined launch does not validate a browser-downloaded first install.
`spctl` policy rejection is expected for this distribution identity and must not
be relabeled as a passing Gatekeeper check. Record the actual warning, per-app
exception, copied location, launch, restart and update behavior on each tested
system. As of this policy change, the final downloaded installation experience
has **not been tested**.

The 2026-10-21 owner-approved [Beta 1 release gate](BETA1-RELEASE.md) governs acceptance. Basic daily workflows and package/download integrity must pass; advanced differences are disclosed in known issues without individual approval. This is not a complete 1:1 port. Use `macos-v…` Git tags and `/api/macos/v1/` feeds; leave Windows `v…` tags, assets and `/api/v1/` feeds intact.

## Manual updates

Download the newer Mac DMG from the official Mac page or linked GitHub Release. Quit DropSpace, replace only the application bundle in Applications, then reopen it. Keep the separate application-support data directory. Renew per-app permissions if macOS requests them. Automatic update implementation is not yet accepted for public use and must not be advertised as verified.

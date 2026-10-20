# Mac v0.1.0 Beta 1 release gate

The owner's 2026-10-21 scope change supersedes the earlier full-parity release gates. This release targets a **basic daily-usable native macOS public beta**, with Windows v0.3.2 Beta 7 as a functional reference. Historical PARITY, CORE-CONTRACTS and SETTINGS entries remain evidence and backlog, not a requirement to reproduce every Windows edge case before Beta 1.

## Essential acceptance

- [ ] Final candidate launches; file/folder intake, open, copy, reveal, pin, remove and undo preserve source bytes. Reuse recorded file/reference/undo evidence; smoke the final candidate.
- [x] Common system clipboard text, URL and image capture, pause/resume, copy, search and cleanup. Release candidate general-pasteboard capture/pause/search/clear passed; original clipboard restored. Copy/resume evidence reused from earlier native tests.
- [ ] Main window, menu-bar recovery, Island expansion/pages and saved settings. Reuse existing Island/settings evidence; final candidate smoke remains.
- [ ] Apple Music metadata/control, lyrics and 1.8B translation. Reuse actual application CPU/Metal evidence; disclose authorization and model requirements.
- [ ] Existing downloads/components/nearby sharing retain their recorded working paths. DropLink is experimental; internet sharing requires a configured service.
- [ ] Release DMG/ZIP, ad-hoc integrity, public download bytes and copied-app launch. No Developer ID or notarization claim.
- [ ] Website Mac/Windows separation, installation, known issues, release notes and a manual download/install update path. Do not claim validated automatic updates.

## Deferred without blocking this beta

QQ Music login; complete other-player controls; physical Windows interoperability; 7B inference on this 16 GiB host; exhaustive ten-language review; every display/system combination; rare edge cases that do not threaten data. Existing implementations are retained. macOS 26 is the deployment minimum; actual evidence currently comes from macOS 27.0.1.

## Current release state

Not published. Candidate `macos/dist/beta1-daily-candidate` has passed local DMG copy/eject/launch, ad-hoc integrity and targeted daily UI checks; see EXECUTION.md. GitHub publishing is awaiting local authentication. Public browser-download/Gatekeeper and website deployment checks remain open. Public URLs will be recorded only after actual publication.

Final local package directory: `macos/dist/beta1-release-ready`. DMG/ZIP/manifest hashes passed; signed bundle bytes exactly match the daily-tested candidate. Source receipt: `651b3a6e4798371d3823f53509e2bc54388a7b32`. No remote tag exists yet. Authentication command on this host: `macos/build/tooling/github-cli/gh auth login`. Do not send credentials in chat.

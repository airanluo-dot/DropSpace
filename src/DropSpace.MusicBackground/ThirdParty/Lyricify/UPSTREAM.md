# Lyricify Backgrounds attribution and modification notice

Source: https://github.com/WXRIW/Lyricify-Backgrounds

Pinned commit: `cbef8a667c02a01707c78c7e76dc67d65cf161ed`.

Upstream package author: XY Wang; source maintained by WXRIW / Lyricify Backgrounds contributors.
Licensed under Apache License 2.0. The complete original `LICENSE.txt` is included unchanged.
No upstream `NOTICE` file was present at the pinned commit. Upstream copyright/license notices
are retained; this notice identifies the portions incorporated and local modifications.

Incorporated files from that commit:

- `Lyricify.Backgrounds.AppleMusicInspired.Ios.WinUI/AppleMusicIosRenderer.cs`
- `Lyricify.Backgrounds.AppleMusicInspired.Ios.WinUI/SwapChainPanelPresenter.cs`
- `Lyricify.Backgrounds.AppleMusicInspired.Ios.Shared/Rendering/AppleMusicIosMesh.cs`
- `Lyricify.Backgrounds.AppleMusicInspired.Ios.Shared/Resources/AppleMusicIosBackground.hlsl`
- Settings portion of `Lyricify.Backgrounds.AppleMusicInspired.Ios/AppleMusicIosBackground.cs`

DropSpace modifications (2026-10-23):

- Use the existing DropSpace .NET 10 / Windows App SDK 2.5.1 targets, preserving the native
  Direct3D 11 composition swap-chain, mesh and HLSL pipeline. Vortice.Direct3D11 3.2.0 and
  SharpGen.Runtime 2.4.2-beta retain the pinned renderer's API versions.
- Remove upstream global audio analysis/capture and audio-device providers completely.
  Accept only a fresh amplitude supplied by DropSpace's existing process-loopback service.
  Without fresh samples, pulse amplitude decays to zero while the mesh continues ambient motion.
- Remove URL/file artwork loading and host-session/provider abstractions. The public control
  accepts bounded in-memory artwork, uses generation fencing for replacement/close, and releases
  its graphics resources on unload or disposal. No upstream player, lyric UI, or audio engine is included.
- Add reduced-motion/paused/hidden scheduling, bounded recovery and faults, and deterministic
  cleanup. Artwork still crossfades through the upstream texture pipeline.
- Use the .NET 10 `in Guid` COM signature, clean up partial panel attachment, and bound the
  upstream GPU-completion polling loop to 100 ms so a stalled device cannot spin indefinitely.
- Add provenance headers to incorporated source files; keep mesh/shader calculations intact.

DropSpace modifications (2026-10-26):

- Give the four artwork layers different bounded scale responses to the shared audio envelope.
  The host uses the existing island audio-response mapping and process-loopback frames; no
  additional capture source, playback clock or audio monitor is introduced.

The local `MusicFlowBackground.cs` control and its lifecycle adaptation are DropSpace code.

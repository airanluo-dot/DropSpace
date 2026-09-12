# Preview.24 reference audit

Source checkouts live outside DropSpace under `E:/Dev/reference`.
WinIsland is pinned to `705cb011e8951710052dab854defd63b5a1effbf`.
PowerToys material reference is `6022efc25d5fae9880d48448f804581b0bd9a121`.
All 7 target and 8 failure images in the supplied package were inspected individually.

## Read source and resulting implementation boundaries

| Reference | Opened source | Result |
| --- | --- | --- |
| WinIsland | core/config.rs, core/smtc.rs, core/audio.rs | Enabled lyrics defaults; session updates and PCM spectrum are owned backend concerns. No Rust code is transplanted. |
| WinIsland | core/lyrics/providers/{netease,qq,kugou,lrclib,amll}.rs | NetEase search → song ID → lyric/YRC; QQ search → songmid → lyric; Kugou candidate/hash → access key → Base64 LRC; LRCLIB exact then search; AMLL search → ID → TTML. Implement independent bounded C# adapters and a title/artist/album/duration matcher. |
| WinIsland | ui/expanded/music_view.rs, ui/compact/mod.rs, window/settings/pages/{music,widgets}.rs | Compact artwork/lyric/spectrum and separate expanded content. Widget editor has real hit-tested modes/library/drag. No plugin or volume-key interception paths. |
| Microsoft Windows-classic-samples | Samples/ApplicationLoopback/cpp/LoopbackCapture.cpp | Public ActivateAudioInterfaceAsync with VT_BLOB process activation parameters; IAudioClient shared loopback/event callback → IAudioCaptureClient PCM. Do not retain the sample WAV-writing behavior. |
| Microsoft PowerToys | src/modules/AltWindowCycle/AltWindowCycle.cpp at pinned commit | Framed no-activate popup and non-client activation explain inactive DWM behavior. Its whole-window backdrop is unsuitable for DropSpace's fixed transparent host; preserve bounded Surface material. |
| Microsoft WindowsAppSDK-Samples | Samples/Windowing/cs/cs-winui/MainWindow.xaml.cs | HWND interop and physical/DIP conversion remain behind the existing adapter. |
| Files | src/Files.App/Views/Settings/SettingsPage.xaml | Independent settings page presenter and grouped rows; no copy of MPL source/control implementations. |
| EarTrumpet | DataModel/WindowsAudio/WindowsAudioFactory.cs, Internal/AudioDeviceManager.cs, Interop/MMDeviceAPI/IAudioEndpointVolumeCallback.cs | Subscribe to endpoint changes and release callback ownership. Do not copy audio-policy extensions or intercept keys. |

Microsoft's documented process-loopback minimum is build 20348:
https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params

## Preview.22/23 per-file audit

- `Core/Widgets/WidgetModels.cs`: pure serialized layout, no lifecycle/UI coupling.
  Selectively reused with invalid-ID filtering, compact-slot validation and full-grid collision search;
  the old search only scanned down/right and could lose a displaced widget despite free cells.
- `Core/Models/NativeIslandSettings.cs`: not copied. Rebuilt compatible fields with corrected defaults,
  no widget keepalive, hidden-width or whole-host-material fields.
- `Core/Media/MediaModels.cs`: pure SMTC contract is reusable; it does not authorize presence.
- `App/Services/Media/WindowsMediaSessionService.cs`: inspected and rejected as a direct transplant.
  It can detach/dispose while refresh is awaiting, queues unowned refresh tasks, truncates oversized
  artwork instead of rejecting it, and lacks source selection. Rebuild lifecycle around owned work.
- `App/Services/Audio/WindowsSpectrumService.cs`: rejected by plan and prior audit; endpoint peak is not FFT.
- `OverlayWindow`, `MainPage`, and `OverlayStateMachine`: Preview.21 is authoritative. No Preview.23 UI transplant.

## Runtime evidence still required

Source reading and supplied pictures are not a current executable comparison.
WinIsland same-track lyric comparison, actual process-loopback capture, focused/unfocused
material, media controls, interactive widget persistence and all Preview.21 native regression
smokes remain open. No reference binary is included in DropSpace or its release assets.

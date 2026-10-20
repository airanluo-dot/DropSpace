# Native process audio and spectrum

Windows baseline: `v0.3.2-beta.7`, `1cd4620dd58a67b0963b73487743137813b8864a`.
Status: native process capture and display implemented; selected live paths verified.
This is not full media, permission, performance or minimum-OS acceptance.

| Frozen rule / owner | Native mapping | Evidence / open scope |
|---|---|---|
| `SpectrumAnalyzer.cs` | Same2048 samples, Hann window, radix-two FFT, six60…15000Hz logarithmic bands, −70dB floor,0.8 rise/0.25 fall | Actual frozen C# execution produced12 PCM cases at44100/48000/96000Hz; native matches within1e-9 |
| `SpectrumFreshnessPolicy` | Retain through150ms; smooth attenuation to zero by600ms; nonfinite/negative age clears energy |7 frozen vectors plus nonfinite-age check passed |
| `AudioCaptureRecoveryPolicy` | Missing process/no packets retries no faster than5s; silent packets do not trigger restart |48 frozen rule vectors passed. HAL setup/format/cleanup errors remain explicit failure with Retry; repeated permission prompting is avoided |
| `WindowsProcessLoopbackService` | `ProcessAudioTap` owns private unmuted mono tap and private aggregate input, exact HAL PID/bundle match, actual tap UID readback and aggregate binding verification | Real Apple Music PID43839 /HAL process124 at48000Hz received PCM; no global/endpoint capture fallback |
| Producer lifetime | Fixed16384-float lock-free SPSC buffer; IO callback only copies verified Float32 mono input; independent actor drains every20ms, MainActor reads latest frame | Wrap/full/invalidated-format test passed. First UI-driven drain lost6144 samples opening settings; independent consumer repeat43 logged packets lost0. Neither figure is a general stress guarantee |
| Source changes | Check one running bundle and launch date; reconfirm HAL object before start; generation fences, metadata changes do not replace a healthy producer | Current Apple Music path exercised; helper-based players, PID/launch races and abrupt source exit remain unverified |
| Presentation demand | Main window/compact Island/expanded Music expose actual window visibility and current page; ShowSpectrum default remains true; pause/off/hidden owners retire capture | Actual on/off persistence, re-enable and pause passed. Main window/Island/fullscreen/Spaces/minimize combinations remain open |
| Teardown | Cancel actor drain generation, stop IO, destroy IOProc/device, remove format observer, destroy tap; retained handles block new starts after cleanup failure | Actual normal stop and Quit logged zero handles and process exit. Forced HAL restart, blocked driver, failure retries and full shutdown deadline remain open |
| Format/sleep | Tap-format notification invalidates the buffer so old-format bytes cannot be interpreted; sleep retires, wake re-evaluates demand | Buffer invalidation test passed. Real output-route change and sleep/wake not exercised |
| Native view | Six actual-energy bars, silent paused presentation for the same recording, compact width contribution, stable status/retry resources in10 languages | Main Music dual-tone screenshot shows bands1/4. Compact/expanded visual acceptance and ten-language layout remain open |

Public API reference: [Apple Core Audio taps](https://developer.apple.com/documentation/coreaudio/capturing-system-audio-with-core-audio-taps).
The source app remains unmuted. No audio is recorded to disk or sent over the network.
`NSAudioCaptureUsageDescription` is present. Runtime sample receipt does not prove first-install
permission, refusal or revocation behavior; no system permission/security setting was changed in
this validation.

The native mono mixdown uses the sample rate reported by HAL; Windows requests44100Hz stereo PCM
and averages channels. Input-rate-specific fixtures preserve the business analyzer without an
unnecessary resampler. Unknown/ambiguous source processes are unavailable. Only the currently
implemented Apple Music/Spotify script adapters can supply identity; Spotify capture and remaining
baseline player adapters are not yet qualified.

The first quiet dual-tone source produced a real but very small PCM peak (~0.000052); its spectrum
correctly remained below the frozen display floor. A second task-generated source used300Hz and
3000Hz tones followed by silence. Actual capture showed only expected bands1/4 at approximately
0.552/0.563, then decayed on silence. Device and player volume were read without being modified.
The original task silence track was restored paused at10s, along with Music's original title filter
and favorites-only view. Task test audio remains retained; no unrelated library entries were removed.

`build/SpectrumNativeEvidence` holds source hashes, screenshots, AX state, task-only traces, failed
attempts, read-only HAL process inspection and exit readback. DEBUG opt-in trace logs numeric sample
peaks, bands, dropped counts and owned audio IDs, not PCM bytes. Normal builds do not record this trace.
Exact commands/failures/retries are in EXECUTION.md.

Release process measurements on this Mac: silent capture averaged9.18% of one CPU core over20.04s;
paused averaged3.00% over10.01s. A3s read-only stack sample showed repeated SwiftUI invalidation
for unchanged bands. Coalescing identical presentation frames reduced the next15.01s silent
capture measurement to4.66% (145600KiB RSS). Analysis revisions continue in the audio actor.
These short, sequential samples are scoped observations, not energy or long-running acceptance.
The final Release signature integrity check passed; actual Quit left no process or import lease.

Remaining: full player matrix/artwork, authorization grant/refuse/revoke, route/format changes,
sleep/wake, another Mac/macOS26, cleanup failure and broader performance/energy measurements.
Music/AI exterior glow now shares this capture through independent demand; turning off the meter
does not disable eligible glow. See LYRICS-GLOW.md for scoped real evidence and remaining acceptance.
This does not close other lyric effects or media settings.

# Media validation — 2026-10-13

This is an accurate execution summary. Earlier terminal output was returned by the tool but
not captured to raw log files; this document does not reconstruct purported raw logs.
Final linked-source and reference DLL SHA-256 values are in `media-validation-inputs.json`.

## Actual scope

The Linux probe links the production `WindowsMediaSessionService`, native executor,
operation lifetime, session owner and subscription sources. Only the Windows/WinUI
boundary is controlled. It checks thread identity, delivered commands and actual fake
publisher state/track effects separately. Three unique combined scenarios cover:

1. Startup source getter initially throws; source becomes ready and emits only its own
   event. Recovery succeeds without another manager event. Disable/re-enable and later
   manager-event discovery restore pause, play, previous and next.
2. A disconnected wrapper remains OS-current and enumerated. Next throws `0x80010108`;
   refresh selects the replacement without replaying the command. New controls execute.
   Retired session/manager callbacks and late metadata cannot replace new-session state.
3. Await continuations stay on the native owner. A canceled waiter retains outstanding
   native work; admitted queued work completes before shutdown. Disposal is repeatable.
   After a new production change, this scenario additionally checks explicit recovery
   after a failed dispatcher creation and no replacement after an ordinary operation fault.

## Execution ledger

| Attempt | Operation | Result |
| --- | --- | --- |
| 1 | Probe compile via `dotnet run` | Failed before scenarios: shim extension resolved `Windows` under `System`; corrected to `global::Windows`. |
| 2 | Probe `startup`, then `recovery` | Startup passed. Recovery reached final assertion, which failed because the fixture checked `Current` before the successful next command's metadata refresh. `native` was not reached. |
| 3 | Probe `recovery native` | Both passed after adding explicit snapshot waits for previous/next. Previously passed startup was not repeated. |
| 4 | Actual projection C# subset compile | Passed once: 0 warnings, 0 errors, 3.12 seconds. |
| 5 | Probe `native` after production startup-retry change | Passed, including the newly relevant factory recovery checks. Other scenarios not repeated. |

There were **3 unique functional scenarios and 5 scenario executions**, including the
fixture failure. Probe compilation: one failed and three successful local compilations.
Actual projection compilation: one successful compilation. No MSTest suites, XAML builds,
Windows app launches, NetEase launches, release builds or remote workflows ran.
Final probe input validation/output wording were only statically reviewed. Startup used
the earlier control assertions; the later recovery scenario also checked the added
previous/next snapshot waits. No additional execution is implied by the final file hashes.

## Compilation references and reproduction

The actual-projection compilation links the latest production media service, verifier,
executor, `NativeAsyncLifetime`, subscription/session owners, `RetirableMediaWork` and
`MediaSoftRestartOperation`, and references the actual `DropSpace.Core` project. It has
**no application or platform shims**. Package DLLs were downloaded through the configured
network proxy after checking the runtime network policy:

- `Microsoft.Windows.SDK.NET.Ref` **10.0.20348.57**: `Microsoft.Windows.SDK.NET.dll` and
  `WinRT.Runtime.dll` under `lib/net8.0`.
- `Microsoft.WindowsAppSDK.InteractiveExperiences` **2.1.9**:
  `Microsoft.InteractiveExperiences.Projection.dll` under `lib/net6.0-windows10.0.18362.0`.
  This is the dependency version declared by the application's `Microsoft.WindowsAppSDK 2.5.1`.

The actual project used `/workspace/.tools/media-native-compile/NativeCompile.csproj`;
`media-native-subset.csproj.txt` is its reviewable copy with paths replaced by
`DropSpaceRoot` and `MediaReferenceRoot` properties. Copy it to `.csproj` outside the
repository tree, supply those properties, and use .NET SDK **10.0.401**. Package extraction
layout and DLL hashes are recorded in `media-validation-inputs.json`.

Executed command:

```text
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet build /workspace/.tools/media-native-compile/NativeCompile.csproj --nologo
```

Probe commands used the same SDK with `dotnet run --project
scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj --no-launch-profile`, then the
selected arguments `-- recovery native` and `-- native` for necessary follow-up execution.

## Unverified boundaries

The shims do not emulate COM marshaling, Windows STA initialization, Windows SMTC,
NetEase/InfLink or WinUI rendering. Startup and plugin off/on playback, previous and next
still require a real Windows/NetEase run. Compilation does not establish those outcomes.
The verifier's separate late-source discovery branch was compiled and statically reviewed,
but not given an additional functional case. The unrelated second-round
`NeteaseComponentsDlcProvider` change was not part of this compilation.

# Beta10 native crash diagnosis and independent lifetime fix

This lane starts from published `v0.3.1-beta.10`, commit
`4044baed6b1c393a815b91e1406a83e0995f1f94`, and writes only
`fix/beta10-native-crash-lifetime`. No release or default-branch change was made.
The parent integration branch was fetched/read separately; it was not written.

## Exact release and symbol evidence

The complete public release artifacts were downloaded in this cloud executor.
Their SHA256 hashes match the release's `SHA256SUMS.txt`:

| File | Bytes | SHA256 |
| --- | ---: | --- |
| `DropSpace.exe` | 355613696 | `caaf091ac30a355abf85c90be6f622dc5b26f1fe509b7ee0e913816889b840c7` |
| `DropSpace-x64.msix` | 63243128 | `8a38924a2e4371592d3306441fbbc8439920b0478599b4b96b946ce0f06b9b10` |

The executable is x64, image base `0x140000000`, timestamp `0x6a89b77a`.
Its embedded runtime configuration identifies .NET `10.0.12`. Its CodeView
record identifies `singlefilehost.pdb`, GUID
`22b27b3c-5523-4123-b45b-c7cbec2d9ee3`, age 1. The release `.text` section is
byte-identical to the official NuGet `Microsoft.NETCore.App.Host.win-x64`
10.0.12 `singlefilehost.exe`; both section hashes are
`08471db667e0298b42ce7638488104a51a8e516756ab30bac19d215a35d1ea76`.
The host was inspected, not executed. No new app build was substituted for
the released binary in this mapping.

Microsoft's exact PDB download is blocked by this Linux executor's proxy.
An isolated Windows CI job downloaded it and DbgHelp verified the GUID, age,
timestamp and absence of a PDB mismatch. PDB SHA256:
`e6ec59770d2558984b0246cc2a09481f97b0f4b94e8c2d3efd415ea833af0151`.
[Successful symbol-only run 37284716727](https://github.com/airanluo-dot/DropSpace/actions/runs/37284716727)
retains the exact PDB and mapping in
[artifact 11334137117](https://github.com/airanluo-dot/DropSpace/actions/runs/37284716727/artifacts/11334137117)
for 14 days. The permanent text result is
[beta10-native-crash-symbol-map.json](beta10-native-crash-symbol-map.json).

| RVA | Exact symbol |
| --- | --- |
| `0x19c4c8` | `ProcessCLRException + 0x178` (function starts `0x19c350`) |
| `0x5b6e20` | `GetCurrentIP` |
| `0x1022a0` | `EEPolicy::HandleFatalError` |

The released instructions call `GetCurrentIP` at `0x19c4c3`, copy its result
at the reported RVA, then call `EEPolicy::HandleFatalError` at `0x19c4df`.
This agrees with the corrupted-state failfast branch in the matching
[runtime source](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/exceptionhandling.cpp#L618).
It identifies the runtime's fatal reporting site, not the original invalid
access or a C# source line. Likewise the second crash's `ntdll.dll` heap
corruption report identifies detection, without proving Windows caused it.
The exact user `ntdll.dll` image and symbols were not available for mapping.

The earlier
[run 37283936831](https://github.com/airanluo-dot/DropSpace/actions/runs/37283936831)
failed the symbol identity gate and remains failed. That verifier queried
PDB identity while DbgHelp deferred loading was enabled. Commit `5341d96`
loads eagerly; it preserves all exact-match checks and adds diagnostic
identity values to errors. The successful symbol-only run above used this
correction; it did not repeat or substitute application tests.

## Confirmed defect and repair

Beta10 awaits projected `AsTask(token)` reads/decodes while owning streams
and readers in `using` scopes. In the pinned
[CsWinRT source](https://github.com/microsoft/CsWinRT/blob/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/src/cswinrt/strings/additions/Windows.Foundation/Windows.Foundation.cs#L312),
token cancellation completes the projected task immediately and separately
requests native cancellation. Consequently the owner can dispose the input
before the native operation signals `Completed`.

Functional commit `8960dd943ce619f50e960f53ce82bbd9b2af30f4` extracts the
existing correct `WindowsMediaSessionService` ownership contract into
`NativeAsyncLifetime`: await actual native completion without a task-cancel
token, request native Cancel on a worker, and retain ownership until both
completion and any in-flight Cancel call finish. Return a late successful
result so callers can acquire/dispose it before checking cancellation.

The repair covers `MediaApplicationIconService`, `ThumbnailService`,
`ImageDecoderPreflight` and the artwork read in `NeteaseSmtcVerifier`.
Existing media artwork/session consumers use the shared helper. Image/XAML
construction and SetSource remain on the UI dispatcher. Presentation may
cancel promptly, but native owners retain resources and admission slots
until completion. Icon and verifier reads retain their deadlines using the
existing bounded operation owner; thumbnail reads retain the existing
four-slot gate and observe late faults.

No exceptions are swallowed to continue after access violation or heap
corruption. This fixes a demonstrated ownership defect. Neither current
crash was reproduced, so causation and an end-user crash cure are unproved.

## Checks actually completed

- Linux: locked restore/build/test of the standalone project linking the
  production helper and bounded owner: 5 passed, 0 failed/skipped.
- Windows run 37283936831: the same standalone tests: 5 passed, 0 failed/skipped.
- The same Windows run compiled the actual WinUI app in Debug and passed
  all 73 selected `NativeAsyncLifetimeTests`, `MediaPublisherTimeoutTests`,
  `MediaOperationLifetimeTests`, `Preview16ImageDecoderTests` and
  `NeteaseSmtcVerifierTests`, with 0 failed/skipped. TRX evidence is retained
  in [artifact 11333162359](https://github.com/airanluo-dot/DropSpace/actions/runs/37283936831/artifacts/11333162359).
  Its overall failure is the separate symbol gate described above.
- The five new controlled native-operation tests cover delayed completion,
  disposal of late acquisitions, slow Cancel, throwing Cancel with late
  native failure, and presentation timeout retaining input/admission.
- PowerShell AST parsing, inline DbgHelp C# compilation, workflow YAML parse
  and `git diff --check` passed. No Release rebuild, AI-engine compilation,
  end-user desktop exercise or package publication was performed.

Read-only audits covered audio activation dual ownership, worker drain and
capture-buffer release, volume callbacks, SMTC subscriptions/owners,
composition detach/dispose, OLE materialization and glow DIB bounds. No
additional demonstrated corrupting operation was found; this is not a
certification of all native code.

## Evidence limits and remaining root-cause work

Library identified the actual supplied names: `crash(1).marker`,
`dropspace.log(1).1`, `dropspace(1).log`, and
`image(20261005-075404).png` / `image(20261005-075405).png`.
The marker text confirms the old September 16 XAML VirtualKey 188 failure.
Current log excerpts confirm the stated restart gaps and successful lyric
lookup before the second gap, without a fatal managed record. The rotated
log's rendition is truncated. Library's current materialization helper was
used, but signed-download proxy failures prevented byte-complete local
logs and screenshot pixel inspection in this executor; supplied crash
details remain user/parent evidence, not newly verified screenshot pixels.
The parent read the full current log and independently checked the raw
rotated log, as recorded in its cloud checkpoint.

The exact missing evidence is a matching full crash dump containing the
original exception record/address/access type, exception context and native
stack, managed frames and RCW/CCW state, plus the matching OS modules.
That can distinguish the initial corrupting operation from this runtime
reporting path. No registry, GFlags, system settings or user-machine actions
were changed. The completed source repair can be integrated independently
while that evidence remains unavailable.

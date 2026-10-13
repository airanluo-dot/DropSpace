# Round 9 — remote image decode cancellation lifetime

Status: **implemented and locally verified within the controlled boundary below**. Final
implementation, independent review, and root authorization preceded execution. One new
combined scenario ran once and passed (exit 0); no failed or repeated execution occurred.
Existing restore assets were reused for one necessary source-linked probe build. The reused
project retains its existing `EnableNETAnalyzers=false`; this round did not change that
configuration or execute the original three media scenarios.

The probe executes production `ClipboardImageReadLifetime` and `NativeAsyncLifetime` with an
explicitly controlled native-operation boundary. Caller cancellation returns while native
work retains its input and single slot. Excess admission throws `IOException` without
starting its delegate. Even after native completion, a blocked native Cancel keeps the input
and slot alive; releasing Cancel drains retirement and disposes the input. The late result
does not revive the canceled caller. Fresh work then succeeds, with input disposal and slot
release checked immediately on return, before any explicit drain.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj --no-restore --no-launch-profile -- image-native
```

Evidence: [raw output](remote-image-lifetime-probe.log.txt) and
[source/assembly hashes and execution receipt](remote-image-lifetime-validation-inputs.json).
This is a console probe, so no TRX was generated.

No production injection seam or large clipboard platform shim will be added for this check.
Actual `ClipboardCaptureService.ImportRemoteAsync` commit-gate release, bridge lifecycle-gate
release, database non-publication and WinRT image decoding are outside dynamic coverage.
Final production wiring was statically reviewed: only dimension work enters the independent
owner; cancellation/disposal checks precede payload/database writes, and both enclosing gates
release in `finally`. The production five-second timeout/exception mapping was statically
reviewed but no separate timeout subcase ran. No real image decoder, database import, system
clipboard or Windows-native execution occurred. Earlier media/receiver scenarios were not
rerun; successful source-linked execution does not establish those unexecuted behaviors.

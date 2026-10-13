# Focused optional handoff send ownership probe

```sh
dotnet run --project scripts/handoff-send-lifecycle-probe/HandoffSendLifecycleProbe.csproj --no-restore
```

One combined scenario links the actual handoff-send lifetime and typed exception sources,
with Core/Infrastructure project references for the real transfer progress type. Controlled
pending sends let caller retirement, actual work completion and cancellation callback
completion happen independently. Default analyzers and warnings-as-errors are retained.
The scenario fills two slots, retires/re-enables the scope, refuses excess admission, rejects
old and settled progress, retains a completed send's slot while its Cancel callback blocks,
records a late failure, then verifies fresh success, original exception propagation and an
active provider cancellation becoming an `IOException` with the original inner exception.
Actual retirement diagnostics provide completion barriers; no delay or polling is used.

This project does not instantiate the actual DropLink client or host, perform network or
filesystem transfers, or reproduce Main/WinUI. Service settings/disposal and SendFiles/Text
wiring are separate static-review boundaries. Earlier probes and localization checks are not
selected by this project.

The optional `tracked` selector runs only the separate tracker lifecycle scenario:

```sh
dotnet run --project scripts/handoff-send-lifecycle-probe/HandoffSendLifecycleProbe.csproj --no-restore -- tracked
```

It links the actual tracker and send lifetime. Its controlled delegates separate caller
completion from actual ownership retirement; client transport, service wiring and WinUI
remain static-review boundaries. The default scenario is not executed when this selector
is present.

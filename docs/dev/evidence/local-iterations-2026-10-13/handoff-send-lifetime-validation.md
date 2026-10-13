# Round 17 — optional handoff send ownership

Status: **implemented and locally verified within the source-linked boundary** after final
production/probe review and root authorization. One new combined scenario ran once and
passed (exit 0), with no failed or repeated execution. One offline-cache restore succeeded;
two referenced projects were already up to date. One necessary normal build/run retained
default analyzers and warnings-as-errors; its output contained no build warnings or errors.

The probe executes actual `DeviceHandoffSendLifetime` and its typed exception source with
the real Infrastructure `TransferProgress` type. Two controlled sends fill the owned slots.
Disable returns and both callers cancel while actual work remains pending; re-enable still
refuses admission. Old scope progress is rejected. The first actual task then completes while
its cancellation callback remains blocked; a late failure from the second is recorded and
releases only its own slot. A fresh pending send can occupy that slot, and another send remains
busy until the blocked callback is released. Fresh success subsequently returns normally.
Old callers stay canceled, and old/settled progress cannot publish. An active failure preserves
its original exception instance; provider-internal `TaskCanceledException` instead faults the
caller with `IOException` retaining the original cause. All five admitted owners retire, with
zero outstanding sends. Actual retirement diagnostics provide completion barriers; no sleep
or polling is used.

Executed commands from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet restore scripts/handoff-send-lifecycle-probe/HandoffSendLifecycleProbe.csproj --source /home/agent/.nuget/packages/ --ignore-failed-sources --nologo
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet run --project scripts/handoff-send-lifecycle-probe/HandoffSendLifecycleProbe.csproj --no-restore --no-launch-profile
```

Evidence: [raw restore output](handoff-send-lifetime-restore.log.txt),
[raw probe output](handoff-send-lifetime-probe.log.txt),
[exact probe project](handoff-send-lifetime-probe.csproj.txt), and
[source/assembly hashes and execution receipt](handoff-send-lifetime-validation-inputs.json).
This console probe generates no TRX. Existing probes and functional tests were not rerun.

Actual `DeviceHandoffService` settings/disposal and SendFiles/SendText routing, MainPage error
mapping, and clipboard-client independence were reviewed statically. No actual DropLink
client/host, peer, transport, filesystem transfer, settings flow, or WinUI interaction ran.
No full App/XAML compilation occurred, and no claim is made that cancellation retracts data
already received remotely. Already-posted arbitrary `IProgress` callbacks remain a caller
consumption boundary. External caller-token cancellation was reviewed but was not another
dynamic subcase.

The localization agent's existing [raw localization log](handoff-send-lifetime-localization.log.txt)
records nine explicit changed-ID reviews and one successful app integrity check (ten languages,
888 IDs). These are static integrity checks, not functional scenarios, and were not repeated.

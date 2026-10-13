# Round 6 — clipboard receiver generation and retry ownership

The single prepared receiver lifecycle scenario ran **once and passed**, after final
implementation and review. There were no failures, retries, additional cases or repeated
module/media checks. One restore used only the existing local package-cache source; one
Release probe build/run returned exit code 0. Default analyzers and warnings-as-errors
remained enabled. Only the probe suppresses CA1416, with its Windows-boundary explanation.

Raw output is preserved in `clipboard-receiver-restore.log.txt` and
`clipboard-receiver-probe.log.txt`. Final source and assembly hashes, coverage roles and
asserted counts are recorded in `clipboard-receiver-validation-inputs.json`.

The probe links the actual `CrossDeviceClipboardService` and references the real Core and
Infrastructure projects. It snapshots the actual host's subscribed delegate without starting
the server. The actual receiver logic rejects copied callbacks after disable and after later
re-enable, before any import. A current event imports successfully and duplicate callbacks
remain successful without repeating the import, including after a settings toggle. A
same-origin event imports nothing. An injected failure before commit releases its receipt;
the next attempt succeeds and another duplicate does not reimport.

These were actual asserted counters: **3 import attempts**, including the injected failure;
**2 successful imports**; **exactly 2 rows** read back from a real temporary SQLite clipboard
history. Real JSON settings backed the toggles. The unique temporary directory was removed
by the probe's successful cleanup. The host remained `IsRunning=false`, `Endpoint=null`.

`ClipboardCaptureService` and `DeviceHandoffService` are platform shims. Successful shim
imports call the actual repository; discovery throws if attempted. A real ephemeral
certificate seeds only the actual identity store's private in-memory cache, avoiding Windows
DPAPI persistence. No production identity file, OS certificate store, listener, peer request,
download, Windows clipboard action or real user data was involved.

The final outbound capture handler/queue owner change was compiled and statically reviewed,
**not dynamically exercised**. The host's HTTP JSON rejection mapping was compiled and
reviewed, **not exercised through an HTTP route**. Windows capture/import, TLS, authentication,
identity persistence and an in-flight import racing disable remain unexecuted boundaries.
Do not describe this managed delegate/import check as a network or Windows end-to-end test.

Commands used .NET SDK 10.0.401 at its previously recorded local path:

```sh
dotnet restore scripts/clipboard-receiver-probe/ClipboardReceiverProbe.csproj --source /home/agent/.nuget/packages/ --ignore-failed-sources --nologo
dotnet run --project scripts/clipboard-receiver-probe/ClipboardReceiverProbe.csproj --configuration Release --no-restore --no-launch-profile
```

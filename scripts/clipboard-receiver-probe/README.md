# Clipboard receiver lifecycle probe

```sh
dotnet run --project scripts/clipboard-receiver-probe/ClipboardReceiverProbe.csproj --configuration Release
```

One combined case links the actual `CrossDeviceClipboardService` source and references the
real Core and Infrastructure projects. It copies the actual `DropLinkHost.ClipboardReceived`
delegate, disables and re-enables the service, and verifies that retired callbacks reject
without importing. A current callback imports once; duplicate events remain successful
across a toggle. An injected precommit import failure releases its reservation for a retry.
Same-origin envelopes do not import. Settings and the two imported text rows use real JSON
and SQLite services in a unique temporary directory that is removed afterward.

Only `ClipboardCaptureService` and `DeviceHandoffService` have platform shims. The capture
boundary counts calls and writes through the real repository without touching the Windows
clipboard. Discovery throws if invoked; no peers are configured. The real identity store's
private cache is seeded with a real ephemeral certificate because DPAPI persistence cannot
run on Linux. This does not validate identity persistence, trust or authentication.

No receive logic is copied: the linked production service owns subscriptions, generations,
duplicate suppression, failure cleanup and acknowledgement completion. Reflection snapshots
the actual host event and does not invoke a substitute receiver. The host is constructed but
never started. No listener, remote request, download or system clipboard action occurs.

This proves the managed event-to-import boundary only. It does not exercise the HTTP route,
TLS, network delivery, Windows clipboard import or the host's JSON error mapping; the latter
requires code review or separate targeted route validation. The separate outbound capture
callback now retains its own queue; that path is reviewed statically against queue retirement
and is not dynamically exercised by this receiver scenario.

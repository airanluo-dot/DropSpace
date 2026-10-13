# Focused media lifecycle probe

Run locally with .NET 10:

```sh
dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj
# To rerun only one changed scenario:
dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj -- recovery
```

This links the actual service, native-owner executor, operation lifetime, session owner,
subscription admission and subscription retirement sources. Only the Windows/WinUI API
boundary is replaced. The fake publisher checks one owning thread for every native call,
yields asynchronous metadata reads and records actual command effects separately from
acceptance. Three combined scenarios cover:

- Startup with a transiently unavailable source; recovery from its session event without a
  second manager notification; manager-event discovery and controls after disable/re-enable.
- A disconnected wrapper still selected/enumerated by the OS; failed-command rediscovery
  without replay; stale callbacks and a late metadata read across another disable/re-enable.
- Native await affinity, canceled callers retaining outstanding native operations, queued
  work draining before owner shutdown, idempotent disposal and explicit retry of a failed
  dispatcher creation without replacing an already established native owner.

This does **not** emulate COM marshaling, a Windows STA, the Windows SMTC broker, NetEase,
InfLink or WinUI rendering. Passing proves the managed lifecycle against a controlled
publisher; Windows/NetEase startup and plugin-toggle controls still require real execution.
No package publishing, production data, release builds or remote workflows are involved.
